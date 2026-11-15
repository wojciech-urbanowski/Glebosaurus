// Pulpit Glebosaurusa: najważniejsze o każdym czujniku (stan, alerty, liczby, wykres ostatnich 3 dób) oraz monitor, poczta,
// dziennik i ustawienia. Szczegóły czujnika i pełna historia są na jego stronie (/sensor?id=…). Stan pobierany z API co
// 30 s (co 3 s, gdy czeka lub trwa odczyt); dane wykresów – tylko gdy przybył nowy odczyt (albo co 15 min, bo okno się
// przesuwa). Akcje: odczyt czujników i raport mailowy na żądanie (POST z nagłówkiem X-Glebosaurus-Action; kolejką zarządza
// silnik monitora). Raport to prawdziwy mail, więc wymaga potwierdzenia na stronie.

import {
  $, h, now, syncClock, when, ago, until, plural, minutesText, hoursText, kv, api, sensorUrl, fetchSeries,
  showActionNote, requestRead, readButton, sensorAccent, sensorBadges, sensorMetrics, sensorLines, alertCard,
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
let reportWatch = null;         // { after } – zlecony raport; gdy próba nowsza niż „after” się skończy, notka pokaże wynik

/** Wykres każdego czujnika żyje między odświeżeniami (karta jest składana od nowa, wykres przenoszony) – bez migania. */
const charts = new Map();       // id → { chart, latestAt, fetchedAt, loading }

// ---- raport na żądanie --------------------------------------------------------------------------------------

function recipientsText() {
  return reportRecipients.length > 0 ? reportRecipients.join(', ') : '–';
}

function askForReport() {
  $('report-confirm-text').textContent =
    `Raport ze stanem wszystkich czujników trafi teraz do: ${recipientsText()}. To prawdziwy mail; zaplanowane raporty pójdą o swoich porach jak zwykle.`;
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
  const before = report ? report.lastAttemptAt : null;
  try {
    const result = await api('/api/report', 'POST');
    if (result.status === 'queued') {
      reportWatch = { after: before };
      showActionNote('Raport zlecony – monitor wyśle go za chwilę (jeśli trwa odczyt czujników, zaraz po nim).', 'green', 'report-note');
    } else if (result.status === 'alreadyQueued') {
      showActionNote('Raport już czeka na wysyłkę.', 'amber', 'report-note');
    } else if (result.status === 'tooSoon') {
      showActionNote(`Za wcześnie – poprzedni raport był przed chwilą; kolejny od ${when(result.availableAt, true)}.`, 'amber', 'report-note');
    } else {
      showActionNote('Poczta nie jest skonfigurowana – raportu nie ma czym wysłać.', 'red', 'report-note');
    }
  } catch (error) {
    showActionNote(`Nie udało się zlecić raportu: ${error.message}`, 'red', 'report-note');
  }
  tick();
}

/** Przycisk „Wyślij raport teraz” według stanu z monitora; po zakończeniu zleconej wysyłki notka pokazuje wynik. */
function renderReportAction() {
  const button = $('report-now');
  let text = 'Wyślij raport teraz…';
  let blocked = true;
  if (!report.available) text = 'Wyślij raport teraz (poczta nieskonfigurowana)';
  else if (report.requested) text = 'Raport czeka na wysyłkę…';
  else if (report.availableFrom && new Date(report.availableFrom) > now()) text = `Raport możliwy od ${when(report.availableFrom, true)}`;
  else blocked = false;
  if (blocked) $('report-confirm').hidden = true;     // np. raport zlecony w innej karcie – pytanie jest już nieaktualne
  button.textContent = text;
  button.disabled = blocked || !$('report-confirm').hidden;

  if (reportWatch && !report.requested && report.lastAttemptAt && report.lastAttemptAt !== reportWatch.after) {
    reportWatch = null;
    if (report.lastError) showActionNote(`Nie udało się wysłać raportu: ${report.lastError}`, 'red', 'report-note');
    else showActionNote(`Raport wysłany ${when(report.lastAttemptAt, true)} do: ${recipientsText()}.`, 'green', 'report-note');
  }
}

/** Wiersz w karcie raportów: ostatni raport na żądanie od startu monitora. */
function reportOnRequestText() {
  if (report.requested) return 'czeka na wysyłkę…';
  if (!report.lastAttemptAt) return null;
  return report.lastError
    ? `nieudany ${when(report.lastAttemptAt)}: ${report.lastError}`
    : `wysłany ${when(report.lastAttemptAt)} (${ago(report.lastAttemptAt)})`;
}

// ---- nagłówek i podsumowanie --------------------------------------------------------------------------------

function renderHeader(status) {
  const m = status.monitor;
  document.title = `${m.name} – Glebosaurus`;
  $('monitor-name').textContent = m.name;
  $('monitor-line').textContent = `${m.machine} · Glebosaurus ${m.version} · działa od ${when(m.startedAt)} (${ago(m.startedAt)})`;
  $('footer').textContent = `Glebosaurus ${m.version} · panel diagnostyczny · odświeżono ${when(m.now, true)}`;
}

function renderBanner(status) {
  const m = status.monitor;
  const banner = $('banner');
  banner.replaceChildren();
  let tone = 'green';
  let headline = 'Monitor pracuje';
  let detail = m.lastCycleAt ? `Ostatni cykl ${ago(m.lastCycleAt)}.` : 'Pierwszy cykl jeszcze trwa.';

  const cycleErrorIsLatest = m.lastCycleErrorAt && (!m.lastCycleAt || new Date(m.lastCycleErrorAt) >= new Date(m.lastCycleAt));
  const stale = m.lastCycleAt && now() - new Date(m.lastCycleAt) > 5 * 60 * 1000;
  if (m.readingNow.length > 0) {
    tone = 'grey';
    headline = `Trwa odczyt: ${m.readingNow.join(', ')}`;
    detail = `Od ${when(m.readingSince, true)}.`;
  } else if (cycleErrorIsLatest) {
    tone = 'red';
    headline = 'Ostatni cykl monitora zakończył się błędem';
    detail = `${m.lastCycleError} (${when(m.lastCycleErrorAt)})`;
  } else if (stale) {
    tone = 'amber';
    headline = 'Monitor dawno nie zakończył cyklu';
    detail = `Ostatni cykl ${ago(m.lastCycleAt)}.`;
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
  if (!mail.configured) {
    card.classList.add('accent-amber');
    card.append(h('div', null, 'Poczta nie jest skonfigurowana – alerty i raporty trafiają tylko do dziennika.'));
  } else {
    const failedLast = mail.lastErrorAt && (!mail.lastSentAt || new Date(mail.lastErrorAt) >= new Date(mail.lastSentAt));
    card.classList.add(failedLast ? 'accent-red' : 'accent-green');
    card.append(kv([
      ['Nadawca', mail.sender],
      ['Alerty do', mail.alertRecipients.join(', ') || '–'],
      ['Raporty do', mail.reportRecipients.join(', ') || '–'],
      ['Dziennik do', mail.adminRecipients.length > 0 ? mail.adminRecipients.join(', ') : 'wyłączony'],
      ['Ostatnia wysyłka', mail.lastSentAt ? `${when(mail.lastSentAt)} (${ago(mail.lastSentAt)})` : 'od startu monitora brak'],
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
  card.append(h('div', 'muted small', 'Zmiana ustawień: edycja pliku konfiguracji i ponowne uruchomienie monitora. Klucze czujników i hasło Gmaila nie są tu pokazywane. Ustawienia i diagnostyka pojedynczego czujnika są na jego stronie.'));
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
    renderHeader(status);
    renderBanner(status);
    renderSensors(status);
    renderAlerts(status);
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
    banner.replaceChildren(h('div', null, 'Brak połączenia z monitorem'), h('div', 'detail', `${error.message}. Kolejna próba za 30 s.`));
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
