// Pulpit Glebosaurusa: najważniejsze o każdym czujniku (stan, alerty, liczby, wykres ostatnich 3 dób) oraz agent, chmura,
// poczta, dziennik i ustawienia. Szczegóły czujnika i pełna historia są na jego stronie (/sensor?id=…). Stan pobierany z API co
// 30 s (co 3 s, gdy czeka lub trwa odczyt); dane wykresów – tylko gdy przybył nowy odczyt (albo co 15 min, bo okno się
// przesuwa). Akcje: odczyt czujników i raport mailowy na żądanie (POST z nagłówkiem X-Glebosaurus-Action; polecenie idzie
// przez chmurę do agenta z dzierżawą czujnika albo do nadawcy).
// Raport to prawdziwy mail, więc wymaga potwierdzenia na stronie.

import {
  $, h, now, syncClock, when, ago, until, plural, minutesText, hoursText, kv, badge, api, sensorUrl,
  fetchSeries, showActionNote, requestRead, readButton, sensorAccent, sensorBadges, sensorMetrics, sensorLines, alertCard,
  buildLogDays, loadLog
} from './common.js';
import { TimeChart } from './charts.js';

const REFRESH_MS = 30000;
const BUSY_REFRESH_MS = 3000;
const CHART_WINDOW_MS = 72 * 3600e3;
const CHART_REFETCH_MS = 15 * 60e3;

let timer = 0;
let wasBusy = false;
let report = null;              // ostatni stan raportu na żądanie (status.reports.onRequest)
let reportRecipients = [];
let notifier = null;            // agent, który teraz wysyła maile

/** Wykres każdego czujnika żyje między odświeżeniami (karta jest składana od nowa, wykres przenoszony) – bez migania. */
const charts = new Map();       // id → { chart, latestAt, fetchedAt, loading }

// ---- raport na żądanie --------------------------------------------------------------------------------------

function recipientsText() {
  return reportRecipients.length > 0 ? reportRecipients.join(', ') : 'odbiorców raportów zapisanych w chmurze';
}

function askForReport() {
  $('report-confirm-text').textContent =
    `Raport ze stanem wszystkich czujników trafi teraz do: ${recipientsText()}. Wyśle go nadawca (${notifier || 'brak'}). ` +
    'To prawdziwy mail; zaplanowane raporty pójdą o swoich porach jak zwykle.';
  $('report-confirm').hidden = false;
  $('report-now').disabled = true;
  $('report-send').focus();
}

function cancelReport() {
  $('report-confirm').hidden = true;
  if (report) renderReportAction();
}

async function sendReport() {
  $('report-confirm').hidden = true;
  $('report-now').disabled = true;
  try {
    const result = await api('/api/report', 'POST');
    if (result.status === 'queued') {
      showActionNote(`Raport zlecony w chmurze – wyśle go nadawca (${notifier || 'gdy któryś agent przejmie blokadę'}), zwykle w ciągu minuty.`, 'green', 'report-note');
    } else if (result.status === 'alreadyQueued') {
      showActionNote('Raport już zlecono w ostatniej minucie.', 'amber', 'report-note');
    } else if (result.status === 'tooSoon') {
      showActionNote(`Za wcześnie – poprzedni raport był przed chwilą; kolejny od ${when(result.availableAt, true)}.`, 'amber', 'report-note');
    } else {
      showActionNote('Chmura niedostępna – raportu nie zlecono. Spróbuj za chwilę.', 'red', 'report-note');
    }
  } catch (error) {
    showActionNote(`Nie udało się zlecić raportu: ${error.message}`, 'red', 'report-note');
  }
  tick();
}

/** Przycisk „Wyślij raport teraz” według stanu z agenta. */
function renderReportAction() {
  const button = $('report-now');
  let text = 'Wyślij raport teraz…';
  let blocked = true;
  if (!report.available) text = 'Wyślij raport teraz (żaden agent nie wysyła maili)';
  else if (report.requested) text = 'Raport czeka na wysyłkę…';
  else if (report.availableFrom && new Date(report.availableFrom) > now()) text = `Raport możliwy od ${when(report.availableFrom, true)}`;
  else blocked = false;
  if (blocked) $('report-confirm').hidden = true;     // np. raport zlecony w innej karcie – pytanie jest już nieaktualne
  button.textContent = text;
  button.disabled = blocked || !$('report-confirm').hidden;
}

/** Wiersz w karcie raportów: ostatnie zlecenie raportu z tego panelu (od startu agenta). */
function reportOnRequestText() {
  if (report.requested) return 'czeka na wysyłkę…';
  if (!report.lastAttemptAt) return null;
  return report.lastError
    ? `nie udało się zlecić ${when(report.lastAttemptAt)}: ${report.lastError}`
    : `zlecony z tego panelu ${when(report.lastAttemptAt)} (${ago(report.lastAttemptAt)}) – wysyła nadawca`;
}

// ---- nagłówek i podsumowanie --------------------------------------------------------------------------------

function renderHeader(status) {
  const m = status.monitor;
  document.title = `${m.name} – Glebosaurus`;
  $('monitor-name').textContent = m.name;
  $('monitor-line').textContent = `${m.machine} · agent w chmurze · Glebosaurus ${m.version} · działa od ${when(m.startedAt)} (${ago(m.startedAt)})`;
  $('footer').textContent = `Glebosaurus ${m.version} · panel diagnostyczny · odświeżono ${when(m.now, true)}`;
}

function renderBanner(status) {
  const m = status.monitor;
  const banner = $('banner');
  banner.replaceChildren();
  let tone = 'green';
  let headline = 'Agent pracuje';
  let detail = m.lastCycleAt ? `Ostatni cykl ${ago(m.lastCycleAt)}.` : 'Pierwszy cykl jeszcze trwa.';

  const cycleErrorIsLatest = m.lastCycleErrorAt && (!m.lastCycleAt || new Date(m.lastCycleErrorAt) >= new Date(m.lastCycleAt));
  const stale = m.lastCycleAt && now() - new Date(m.lastCycleAt) > 5 * 60 * 1000;
  const cloud = status.cloud;
  const syncStale = !cloud.lastSyncAt || now() - new Date(cloud.lastSyncAt) > 5 * 60 * 1000;
  if (cloud.revoked) {
    tone = 'red';
    headline = 'Chmura odrzuciła token agenta';
    detail = 'Agent nie synchronizuje się i nie dostaje dzierżaw – potrzebna ponowna rejestracja (glebosaurus enroll z nowym kodem).';
  } else if (syncStale && cloud.lastSyncError) {
    tone = 'red';
    headline = 'Brak połączenia z chmurą';
    detail = `${cloud.lastSyncError}. Ostatnia udana synchronizacja: ${cloud.lastSyncAt ? ago(cloud.lastSyncAt) : 'jeszcze nie było'}; odczyty czekają w buforze (${cloud.unsentReadings}).`;
  } else if (m.readingNow.length > 0) {
    tone = 'grey';
    headline = `Trwa odczyt: ${m.readingNow.join(', ')}`;
    detail = `Od ${when(m.readingSince, true)}.`;
  } else if (cycleErrorIsLatest) {
    tone = 'red';
    headline = 'Ostatni cykl agenta zakończył się błędem';
    detail = `${m.lastCycleError} (${when(m.lastCycleErrorAt)})`;
  } else if (stale) {
    tone = 'amber';
    headline = 'Agent dawno nie zakończył cyklu';
    detail = `Ostatni cykl ${ago(m.lastCycleAt)}.`;
  } else if (cloud.configCachedAt) {
    tone = 'amber';
    headline = 'Agent pracuje na lokalnej kopii konfiguracji';
    detail = `Przy starcie chmura była niedostępna; kopia z ${when(cloud.configCachedAt)}.`;
  }

  const problems = status.sensors.filter((s) => s.failures > 0).length;
  const counts = [
    `${status.sensors.length} ${plural(status.sensors.length, 'czujnik', 'czujniki', 'czujników')}`,
    `${status.alerts.length} ${plural(status.alerts.length, 'otwarty alert', 'otwarte alerty', 'otwartych alertów')}`
  ];
  if (problems > 0) counts.push(`${problems} z nieudanym ostatnim odczytem`);

  banner.className = `banner tone-${tone}`;
  banner.append(h('div', null, headline), h('div', 'detail', `${detail} ${counts.join(' · ')}.`));
}

// ---- czujniki -----------------------------------------------------------------------------------------------

function chartFor(sensor) {
  let entry = charts.get(sensor.id);
  if (!entry) {
    entry = { chart: new TimeChart({ compact: true, emptyText: 'Brak odczytów z ostatnich 3 dób' }), latestAt: undefined, fetchedAt: 0, loading: false };
    charts.set(sensor.id, entry);
  }
  entry.chart.setThreshold(sensor.wateringBelowPercent);
  return entry;
}

function renderSensor(sensor) {
  const card = h('article', `card accent-${sensorAccent(sensor)}`);
  const name = h('a', 'sensor-name', sensor.name);
  name.href = sensorUrl(sensor.id);
  card.append(...[name, sensorBadges(sensor), sensorMetrics(sensor), chartFor(sensor).chart.element, sensorLines(sensor, false)].filter(Boolean));

  const details = h('a', 'button-link small', 'Szczegóły i pełna historia');
  details.href = sensorUrl(sensor.id);
  card.append(h('div', 'card-actions', readButton(sensor, () => requestRead(sensor.id, tick)), details));
  return card;
}

/** Okno wykresu przesuwa się z czasem; dane dociągamy tylko po nowym odczycie albo co 15 min. */
function updateCharts(status) {
  const to = now();
  const from = new Date(to.getTime() - CHART_WINDOW_MS);
  for (const sensor of status.sensors) {
    const entry = chartFor(sensor);
    entry.chart.setView(from.getTime(), to.getTime());
    const latestAt = sensor.latest ? sensor.latest.at : null;
    const stale = latestAt !== entry.latestAt || Date.now() - entry.fetchedAt > CHART_REFETCH_MS;
    if (!stale || entry.loading) continue;
    entry.loading = true;
    entry.chart.setLoading(true);
    fetchSeries(sensor.id, from, to, entry.chart.plotWidth)
      .then((data) => {
        entry.latestAt = latestAt;
        entry.fetchedAt = Date.now();
        entry.chart.setData(data);
      })
      .catch(() => { /* zostaje poprzedni obraz; kolejna próba przy następnym odświeżeniu */ })
      .finally(() => {
        entry.loading = false;
        entry.chart.setLoading(false);
      });
  }
  for (const id of [...charts.keys()]) {
    if (!status.sensors.some((s) => s.id === id)) charts.delete(id);
  }
}

function renderSensors(status) {
  $('sensors').replaceChildren(...status.sensors.map(renderSensor));
  updateCharts(status);
}

// ---- alerty, poczta, ustawienia -----------------------------------------------------------------------------

function renderAlerts(status) {
  const container = $('alerts');
  if (status.alerts.length === 0) {
    container.replaceChildren(h('div', 'empty', 'Brak otwartych alertów.'));
    return;
  }
  container.replaceChildren(...status.alerts.map((alert) => alertCard(alert, true)));
}

function renderMail(status) {
  const mail = status.mail;
  const reports = status.reports;
  const card = h('div', 'card');
  const cloud = status.cloud;
  card.classList.add(cloud.notifier ? 'accent-green' : 'accent-amber');
  card.append(h('div', null, cloud.notifier
    ? `Maile wysyła ${cloud.notifierIsMe ? 'ten agent' : `agent ${cloud.notifier}`} (blokada nadawcy do ${when(cloud.notifierUntil, true)}).`
    : 'Żaden agent nie trzyma teraz blokady nadawcy – maile czekają, aż któryś kandydat ją przejmie.'));
  if (!mail.configured) {
    card.append(h('div', 'muted small', 'Ten agent nie jest kandydatem na nadawcę – odbiorców i stan poczty widać w panelu nadawcy.'));
  } else {
    const failedLast = mail.lastErrorAt && (!mail.lastSentAt || new Date(mail.lastErrorAt) >= new Date(mail.lastSentAt));
    card.classList.add(failedLast ? 'accent-red' : 'accent-green');
    card.append(kv([
      ['Nadawca', mail.sender],
      ['Alerty do', mail.alertRecipients.join(', ') || '–'],
      ['Raporty do', mail.reportRecipients.join(', ') || '–'],
      ['Dziennik do', mail.adminRecipients.length > 0 ? mail.adminRecipients.join(', ') : 'wyłączony'],
      ['Ostatnia wysyłka', mail.lastSentAt ? `${when(mail.lastSentAt)} (${ago(mail.lastSentAt)}) – z tego agenta` : 'od startu agenta brak'],
      ['Ostatni błąd', mail.lastError ? `${mail.lastError} (${when(mail.lastErrorAt)})` : null],
      ['Wysyłka wstrzymana do', mail.retryAt ? when(mail.retryAt) : null]
    ]));
  }
  const reportCard = h('div', 'card');
  reportCard.append(kv([
    ['Raporty', reports.times.length > 0 ? reports.times.join(', ') : 'wyłączone'],
    ['Następny raport', reports.nextAt ? until(reports.nextAt) : null],
    ['Nadrabianie', reports.times.length > 0 ? `do ${hoursText(reports.catchUpHours)} godz. po czasie` : null],
    ['Raport na żądanie', reportOnRequestText()]
  ]));
  $('mail').replaceChildren(card, reportCard);
}

// ---- chmura i agenci ----------------------------------------------------------------------------------------

function roleText(agent) {
  return agent.canSendMail ? `zbiera i może wysyłać maile (priorytet ${agent.senderPriority})` : 'zbiera odczyty';
}

function renderCloud(status) {
  const cloud = status.cloud;
  const syncFailing = cloud.lastSyncAttemptAt && cloud.lastSyncError
    && (!cloud.lastSyncAt || new Date(cloud.lastSyncAttemptAt) > new Date(cloud.lastSyncAt));
  const card = h('div', `card accent-${cloud.revoked ? 'red' : syncFailing ? 'amber' : 'green'}`);
  card.append(kv([
    ['Projekt', cloud.project],
    ['Ten agent', `${cloud.agent || '–'} – ${roleText(cloud)}`],
    ['Synchronizacja', cloud.lastSyncAt ? `${when(cloud.lastSyncAt, true)} (${ago(cloud.lastSyncAt)})` : 'jeszcze nie było'],
    ['Błąd synchronizacji', syncFailing ? `${cloud.lastSyncError} (${cloud.syncFailures} z rzędu)` : null],
    ['Bufor odczytów', cloud.unsentReadings > 0 ? `${cloud.unsentReadings} czeka na wysłanie` : 'wszystkie wysłane'],
    ['Nadawca maili', cloud.notifier ? `${cloud.notifier}${cloud.notifierIsMe ? ' (ten agent)' : ''}` : 'brak – maile czekają'],
    ['Kopia odczytów', cloud.mirrorEnabled
      ? (cloud.mirrorError ? `błąd: ${cloud.mirrorError}` : cloud.mirrorAt ? `odczyty wszystkich agentów, stan z ${when(cloud.mirrorAt, true)}` : 'pierwsze pobieranie…')
      : null],
    ['Konfiguracja', `wersja ${cloud.configVersion}${cloud.configCachedAt ? ` – lokalna kopia z ${when(cloud.configCachedAt)}` : ''}`],
    ['Zegar', cloud.clockOffsetSeconds !== null && Math.abs(cloud.clockOffsetSeconds) >= 5
      ? `różni się od zegara chmury o ${Math.round(cloud.clockOffsetSeconds)} s` : null]
  ]));
  const parts = [card];
  if (cloud.configWarnings.length > 0) {
    const warnings = h('div', 'card accent-amber');
    warnings.append(h('div', null, h('strong', null, 'Uwagi do konfiguracji z chmury')));
    for (const warning of cloud.configWarnings) warnings.append(h('div', 'small', warning));
    parts.push(warnings);
  }
  for (const agent of cloud.agents) {
    const silent = !agent.lastSeenAt || now() - new Date(agent.lastSeenAt) > 30 * 60 * 1000;
    const box = h('div', `card accent-${silent ? 'amber' : 'green'}`);
    const title = h('div', null, h('strong', null, agent.name));
    if (agent.isMe) title.append(' ', badge('ten agent', 'grey'));
    if (agent.name === cloud.notifier) title.append(' ', badge('wysyła maile', 'green'));
    box.append(title, h('div', 'muted small', [
      roleText(agent),
      `${agent.heldSensors} ${plural(agent.heldSensors, 'czujnik', 'czujniki', 'czujników')} w dzierżawie`,
      agent.lastSeenAt ? `ostatnio ${ago(agent.lastSeenAt)}` : 'jeszcze się nie synchronizował'
    ].join(' · ')));
    parts.push(box);
  }
  $('cloud').replaceChildren(...parts);
}

function renderSettings(status) {
  const m = status.monitor;
  const card = h('div', 'card');
  card.append(kv([
    ['Odczyt co', minutesText(m.readIntervalMinutes)],
    ['Okno skanowania', `${m.scanWindowSeconds} s`],
    ['Odczyty w bazie', `${m.retentionDays} dni`],
    ['Pliki dziennika', `${m.logRetentionDays} dni`],
    ['Przypomnienie alertu', `co ${hoursText(m.alertReminderHours)} godz.`],
    ['Bluetooth', m.bleBackend],
    ['Strefa czasowa', m.timeZone],
    ['Konfiguracja', m.configPath],
    ['Baza', m.databasePath],
    ['Dziennik', m.logDirectory]
  ]));
  card.append(h('div', 'muted small',
    'Czujniki, progi, harmonogram i poczta są w chmurze (agent pobiera zmiany sam); w pliku na urządzeniu zostają baza, retencja i panel. Klucze czujników i hasło Gmaila nie są tu pokazywane. Ustawienia i diagnostyka pojedynczego czujnika są na jego stronie.'));
  $('settings').replaceChildren(card);
}

// ---- dziennik -----------------------------------------------------------------------------------------------

function refreshLog() {
  return loadLog({ daysAgo: $('log-day').value, level: $('log-level').value, note: $('log-note'), container: $('log') });
}

// ---- pętla odświeżania --------------------------------------------------------------------------------------

/**
 * Zwraca { busy, wakeIn }: busy – któryś czujnik czeka na odczyt albo jest czytany (panel odświeża się wtedy częściej);
 * wakeIn – za ile ms minie najbliższy odstęp odczytu na żądanie (wtedy odświeżenie odblokuje przycisk).
 */
async function refresh() {
  try {
    const status = await api('/api/status');
    syncClock(status.monitor.now);
    notifier = status.cloud.notifier;
    renderHeader(status);
    renderBanner(status);
    renderSensors(status);
    renderAlerts(status);
    renderCloud(status);
    report = status.reports.onRequest;
    reportRecipients = status.mail.reportRecipients;
    renderMail(status);
    renderReportAction();
    renderSettings(status);
    buildLogDays($('log-day'), status.monitor.logRetentionDays);
    const busy = status.sensors.some((s) => s.readingNow || s.readRequested) || report.requested;
    $('read-all').disabled = status.sensors.every((s) => s.readingNow || s.readRequested);
    const waits = status.sensors
      .map((s) => s.readOnRequestFrom)
      .concat(report.availableFrom)
      .map((iso) => (iso ? new Date(iso) - now() : Infinity))
      .filter((ms) => ms > 0);
    return { busy, wakeIn: waits.length > 0 ? Math.min(...waits) + 500 : Infinity };
  } catch (error) {
    const banner = $('banner');
    banner.className = 'banner tone-red';
    banner.replaceChildren(h('div', null, 'Brak połączenia z agentem'), h('div', 'detail', `${error.message}. Kolejna próba za 30 s.`));
    return { busy: false, wakeIn: Infinity };
  }
}

async function tick() {
  window.clearTimeout(timer);
  if (document.hidden) return;
  const { busy, wakeIn } = await refresh();
  // W trakcie odczytów dziennik czekamy do końca serii (plik bywa duży, a odświeżanie jest wtedy co 3 s).
  if (!busy || !wasBusy) refreshLog();
  wasBusy = busy;
  timer = window.setTimeout(tick, Math.min(busy ? BUSY_REFRESH_MS : REFRESH_MS, wakeIn));
}

$('log-refresh').addEventListener('click', refreshLog);
$('log-day').addEventListener('change', refreshLog);
$('log-level').addEventListener('change', refreshLog);
$('read-all').addEventListener('click', () => {
  $('read-all').disabled = true;
  requestRead(null, tick);
});
$('report-now').addEventListener('click', askForReport);
$('report-send').addEventListener('click', sendReport);
$('report-cancel').addEventListener('click', cancelReport);
document.addEventListener('visibilitychange', () => { if (!document.hidden) tick(); });
tick();
