// Strona jednego czujnika (/sensor?id=…): stan teraz, przeglądalny wykres całej historii z paskiem przeglądu, otwarte
// alerty, ostatnie odczyty, diagnostyka, ustawienia i wpisy dziennika o tym czujniku. Czujnik Flower Care ma pod
// wykresem drugi – światła i żyzności – z tym samym oknem i z tych samych danych. Stan z /api/status co 30 s (co 3 s
// w trakcie odczytu); wykres dociąga dane okna z zapasem po bokach (przesuwanie jest płynne), w rozdzielczości dobranej
// przez monitor do szerokości wykresu. Gdy okno kończy się „teraz”, nowy odczyt przesuwa je i odświeża dane.

import {
  $, h, now, syncClock, when, ago, until, percent, celsius, dbm, plural, minutesText, hoursText, kv, api, sensorUrl,
  fetchSeries, dateFmt, requestRead, readButton, sensorAccent, sensorBadges, sensorMetrics, sensorLines, alertCard,
  buildLogDays, loadLog
} from './common.js';
import { lux, microSiemens, isFlowerCare } from './format.js';
import { TimeChart, OverviewStrip, resolutionText, soilSeries, LIGHT_SERIES } from './charts.js';

const REFRESH_MS = 30000;
const BUSY_REFRESH_MS = 3000;
const HOUR = 3600e3;
const DAY = 24 * HOUR;
const MIN_SPAN = 2 * HOUR;
const DEFAULT_SPAN = 7 * DAY;
const OVERVIEW_REFETCH_MS = 15 * 60e3;
const READINGS_PAGE = 96;
const PRESETS = [['3 doby', 3 * DAY], ['7 dni', 7 * DAY], ['30 dni', 30 * DAY], ['90 dni', 90 * DAY], ['Rok', 365 * DAY], ['Całość', null]];

const sensorId = new URLSearchParams(window.location.search).get('id');
if (!sensorId) window.location.replace('/');

let timer = 0;
let wasBusy = false;
let sensor = null;
let first = null;               // najstarszy odczyt w bazie (ms); null = brak odczytów
let view = null;                // { from, to } w ms
let following = true;           // okno kończy się „teraz” i przesuwa z czasem
let loaded = null;              // { from, to, span, bucketMinutes } – zakres i rozdzielczość danych w wykresie
let loadSeq = 0;
let overviewAt = 0;
let latestAt;
let readingsCount = READINGS_PAGE;

const chart = new TimeChart({ interactive: true, onView: (v, settled) => changeView(v.from, v.to, settled) });
const lightChart = new TimeChart({ interactive: true, series: LIGHT_SERIES, onView: (v, settled) => changeView(v.from, v.to, settled) });
const overview = new OverviewStrip({ onView: (v, settled) => changeView(v.from, v.to, settled) });

// ---- okno wykresu -------------------------------------------------------------------------------------------

function historyBounds() {
  const end = now().getTime();
  return { start: first ?? end - DEFAULT_SPAN, end };
}

/** Okno w granicach historii: nie dalej niż „teraz”, od najstarszego odczytu, od 2 godzin do całości (co najmniej 3 doby). */
function clampView(from, to) {
  const { start, end } = historyBounds();
  const span = Math.min(Math.max(to - from, MIN_SPAN), Math.max(end - start, 3 * DAY));
  let right = Math.min(to, end);
  let left = right - span;
  const lower = Math.min(start, end - span);
  if (left < lower) {
    left = lower;
    right = left + span;
  }
  return { from: left, to: right };
}

function changeView(from, to, settled) {
  view = clampView(from, to);
  following = view.to >= now().getTime() - 60e3;
  chart.setView(view.from, view.to);
  lightChart.setView(view.from, view.to);
  overview.setView(view.from, view.to);
  renderViewInfo();
  if (settled && needsLoad()) loadChart();
}

function needsLoad() {
  if (!loaded) return true;
  const ratio = (view.to - view.from) / loaded.span;
  return view.from < loaded.from || view.to > loaded.to || ratio < 0.6 || ratio > 1.6;
}

/** Dane okna z zapasem jednej szerokości po obu stronach; około jednego punktu na 2 px. */
async function loadChart() {
  const span = view.to - view.from;
  const end = now().getTime();
  const from = view.from - span;
  const to = Math.min(end, view.to + span);
  const points = Math.min(2000, Math.max(10, ((chart.plotWidth / 2) * (to - from)) / span));
  const seq = ++loadSeq;
  chart.setLoading(true);
  lightChart.setLoading(true);
  try {
    const data = await fetchSeries(sensorId, new Date(from), new Date(to), points);
    if (seq !== loadSeq) return;
    loaded = { from, to: Math.max(to, view.to), span, bucketMinutes: data.bucketMinutes };
    const firstAt = data.firstAt ? Date.parse(data.firstAt) : null;
    const firstChanged = firstAt !== first;
    first = firstAt;
    chart.setData(data);
    lightChart.setData(data);
    renderViewInfo();
    renderDiagnostics();
    if (firstChanged || Date.now() - overviewAt > OVERVIEW_REFETCH_MS) loadOverview();
  } catch (error) {
    if (seq === loadSeq) $('view-info').textContent = `Nie udało się wczytać danych wykresu: ${error.message}`;
  } finally {
    if (seq === loadSeq) {
      chart.setLoading(false);
      lightChart.setLoading(false);
    }
  }
}

async function loadOverview() {
  overviewAt = Date.now();
  const { start, end } = historyBounds();
  const range = { from: Math.min(start, end - 3 * DAY), to: end };
  try {
    const width = overview.element.clientWidth || 600;
    const data = await fetchSeries(sensorId, new Date(range.from), new Date(range.to), Math.max(10, width / 2));
    overview.setData(range, data);
    overview.setView(view.from, view.to);
  } catch {
    overviewAt = 0;   // spróbujemy przy kolejnym wczytaniu wykresu
  }
}

function edgeText(ms) {
  const date = new Date(ms);
  if (date.getFullYear() !== now().getFullYear()) {
    return `${date.toLocaleDateString('pl-PL')} ${date.toLocaleTimeString('pl-PL', { hour: '2-digit', minute: '2-digit' })}`;
  }
  return when(date.toISOString());
}

function renderViewInfo() {
  if (!view) return;
  const parts = [`${edgeText(view.from)} – ${following ? 'teraz' : edgeText(view.to)}`];
  if (loaded) parts.push(first === null ? 'brak odczytów w bazie' : resolutionText(loaded.bucketMinutes));
  $('view-info').textContent = parts.join(' · ');

  const span = view.to - view.from;
  for (const button of $('presets').children) {
    const preset = button.dataset.span ? Number(button.dataset.span) : null;
    const active = following && (preset === null
      ? first !== null && view.from <= first + 60e3
      : Math.abs(span - preset) < 60e3);
    button.setAttribute('aria-pressed', String(active));
  }
}

function buildControls() {
  for (const [label, span] of PRESETS) {
    const button = h('button', 'quiet small preset', label);
    button.type = 'button';
    if (span) button.dataset.span = String(span);
    button.addEventListener('click', () => {
      const end = now().getTime();
      changeView(span ? end - span : (first ?? end - DEFAULT_SPAN), end, true);
    });
    $('presets').append(button);
  }
  $('pan-back').addEventListener('click', () => chart.pan(-0.5));
  $('pan-forward').addEventListener('click', () => chart.pan(0.5));
  $('zoom-in').addEventListener('click', () => chart.zoom(0.5));
  $('zoom-out').addEventListener('click', () => chart.zoom(2));
  $('go-now').addEventListener('click', () => {
    const end = now().getTime();
    changeView(end - (view.to - view.from), end, true);
  });
}

// ---- reszta strony ------------------------------------------------------------------------------------------

function renderHeader(status) {
  const m = status.monitor;
  document.title = `${sensor.name} – ${m.name} – Glebosaurus`;
  $('sensor-name').textContent = sensor.name;
  $('sensor-line').textContent = `Agent ${m.name} · odczyt co ${minutesText(sensor.readIntervalMinutes)}`;
  $('footer').textContent = `Glebosaurus ${m.version} · panel diagnostyczny · odświeżono ${when(m.now, true)}`;

  const pick = $('sensor-pick');
  if (pick.childElementCount !== status.sensors.length) {
    pick.replaceChildren(...status.sensors.map((s) => {
      const option = h('option', null, s.name);
      option.value = s.id;
      return option;
    }));
  }
  pick.value = sensor.id;
}

function renderNow() {
  const card = h('article', `card accent-${sensorAccent(sensor)}`);
  card.append(...[
    sensorBadges(sensor) || h('div', 'muted small', 'Bez alertów i problemów z odczytem.'),
    sensorMetrics(sensor),
    sensorLines(sensor, true),
    h('div', 'card-actions', readButton(sensor, () => requestRead(sensor.id, tick)))
  ].filter(Boolean));
  $('now').replaceChildren(card);
}

function renderAlerts(status) {
  const alerts = status.alerts.filter((a) => a.sensorId === sensor.id);
  $('alerts').replaceChildren(...(alerts.length > 0
    ? alerts.map((alert) => alertCard(alert, false))
    : [h('div', 'empty', 'Brak otwartych alertów tego czujnika.')]));
}

function renderDiagnostics() {
  if (!sensor) return;
  const card = h('div', 'card');
  card.append(kv([
    ['Dzierżawa', sensor.holder
      ? `${sensor.holder}${sensor.holderIsMe ? ' (ten agent)' : ''} do ${when(sensor.holderUntil, true)}`
      : 'nikt – czujnik czeka na agenta, który go usłyszy'],
    ['Ostatnia próba', sensor.lastAttemptAt ? `${when(sensor.lastAttemptAt)} (${ago(sensor.lastAttemptAt)}) – ${sensor.lastResultText}`
      : 'ten agent nie czytał go od startu'],
    ['Nieudane próby z rzędu', String(sensor.failures)],
    ['Ponowienie', sensor.retryAt ? until(sensor.retryAt) : null],
    ['Następny odczyt', until(sensor.nextReadAt)],
    ['Sygnał', sensor.lastSignalDbm !== null && sensor.lastSignalDbm !== undefined ? `${dbm(sensor.lastSignalDbm)} (${ago(sensor.lastSignalAt)})` : 'brak pomiaru od startu agenta'],
    ['Odczyt na żądanie', sensor.readRequested ? 'zlecony przez chmurę, czeka na wynik'
      : sensor.readOnRequestFrom ? `możliwy od ${when(sensor.readOnRequestFrom, true)}` : 'możliwy'],
    ['Najstarszy odczyt w bazie', first !== null ? `${dateFmt.format(new Date(first))} ${new Date(first).getFullYear()} (${ago(new Date(first).toISOString())})` : loaded ? 'brak odczytów' : null]
  ]));
  $('diagnostics').replaceChildren(card);
}

function renderSettings() {
  const card = h('div', 'card');
  card.append(kv([
    ['Odczyt co', minutesText(sensor.readIntervalMinutes)],
    ['Podlać poniżej', `${sensor.wateringBelowPercent}% (${sensor.consecutiveReadings} ${plural(sensor.consecutiveReadings, 'odczyt', 'odczyty', 'odczytów')} z rzędu)`],
    ['Alert wygasa powyżej', `${sensor.wateringClearAbovePercent}%`],
    ['Słaba bateria przy', `${sensor.lowBatteryPercent}% lub mniej`],
    ['Brak odczytów po', `${hoursText(sensor.silentAfterHours)} godz.`],
    ['Rodzaj', isFlowerCare(sensor) ? 'Xiaomi Flower Care (bez klucza, po adresie MAC)' : 'Tuya BLE'],
    ['Adres MAC', sensor.mac || '–'],
    ['Produkt Tuya', isFlowerCare(sensor) ? null : sensor.productId || '–'],
    ['Identyfikator', sensor.id]
  ]));
  card.append(h('div', 'muted small', 'Ustawienia czujnika są w chmurze – agent pobiera zmiany sam.'));
  $('settings').replaceChildren(card);
}

async function loadReadings() {
  const container = $('readings');
  try {
    const readings = await api(`/api/sensors/${encodeURIComponent(sensorId)}/readings?count=${readingsCount}`);
    if (readings.length === 0) {
      container.replaceChildren(h('div', 'empty', 'Brak odczytów w bazie.'));
      return;
    }
    const extra = isFlowerCare(sensor);
    const table = h('table', 'readings');
    table.append(h('thead', null, h('tr', null, h('th', null, 'Czas'), h('th', null, 'Wilgotność'), h('th', null, 'Temperatura'), h('th', null, 'Bateria'),
      extra ? h('th', null, 'Światło') : null, extra ? h('th', null, 'Żyzność') : null)));
    const body = h('tbody');
    for (const r of readings) {
      body.append(h('tr', null,
        h('td', null, when(r.at)), h('td', null, percent(r.moisturePercent)),
        h('td', null, celsius(r.soilTemperatureCelsius)), h('td', null, percent(r.batteryPercent)),
        extra ? h('td', null, lux(r.lightLux)) : null, extra ? h('td', null, microSiemens(r.conductivityMicroSiemens)) : null));
    }
    table.append(body);
    const parts = [h('div', 'muted small', `Ostatnie ${readings.length} ${plural(readings.length, 'odczyt', 'odczyty', 'odczytów')}, od najnowszego:`),
      h('div', 'table-wrap', table)];
    if (readings.length === readingsCount && readingsCount < 1000) {
      const more = h('button', 'quiet small', 'Pokaż więcej');
      more.type = 'button';
      more.addEventListener('click', () => {
        readingsCount = Math.min(1000, readingsCount * 2);
        loadReadings();
      });
      parts.push(h('div', 'card-actions', more));
    }
    container.replaceChildren(...parts);
  } catch (error) {
    container.replaceChildren(h('div', 'empty', `Nie udało się wczytać odczytów: ${error.message}`));
  }
}

function refreshLog() {
  if (!sensor) return Promise.resolve();
  return loadLog({
    daysAgo: $('log-day').value, level: $('log-level').value, contains: sensor.name, subject: `o czujniku „${sensor.name}”`,
    note: $('log-note'), container: $('log')
  });
}

function showProblem(headline, detail) {
  const banner = $('banner');
  banner.className = 'banner tone-red';
  banner.replaceChildren(h('div', null, headline), h('div', 'detail', detail));
  banner.hidden = false;
}

// ---- pętla odświeżania --------------------------------------------------------------------------------------

async function refresh() {
  let status;
  try {
    status = await api('/api/status');
  } catch (error) {
    showProblem('Brak połączenia z agentem', `${error.message}. Kolejna próba za 30 s.`);
    return { busy: false, wakeIn: Infinity };
  }
  syncClock(status.monitor.now);
  sensor = status.sensors.find((s) => s.id === sensorId) || null;
  if (!sensor) {
    $('content').hidden = true;
    $('sensor-name').textContent = 'Nieznany czujnik';
    $('sensor-line').textContent = '';
    showProblem('Nie ma takiego czujnika w konfiguracji', `Identyfikator: ${sensorId}. Wróć do listy wszystkich czujników.`);
    return { busy: false, wakeIn: Infinity };
  }
  $('banner').hidden = true;
  $('content').hidden = false;
  chart.setThreshold(sensor.wateringBelowPercent);
  if (isFlowerCare(sensor) && $('light-section').hidden) {
    $('light-section').hidden = false;
    chart.setSeries(soilSeries(true));
  }

  renderHeader(status);
  renderNow();
  renderAlerts(status);
  renderDiagnostics();
  renderSettings();
  buildLogDays($('log-day'), status.monitor.logRetentionDays);

  const newestAt = sensor.latest ? sensor.latest.at : null;
  const freshReading = newestAt !== latestAt;
  latestAt = newestAt;
  if (!view) {
    const end = now().getTime();
    changeView(end - DEFAULT_SPAN, end, true);
  } else if (following) {
    const end = now().getTime();
    changeView(end - (view.to - view.from), end, false);
    if (freshReading) loadChart();
  }
  if (freshReading) loadReadings();

  const busy = sensor.readingNow || sensor.readRequested;
  const wait = sensor.readOnRequestFrom ? new Date(sensor.readOnRequestFrom) - now() : Infinity;
  return { busy, wakeIn: wait > 0 ? wait + 500 : Infinity };
}

async function tick() {
  window.clearTimeout(timer);
  if (document.hidden) return;
  const { busy, wakeIn } = await refresh();
  if (!busy || !wasBusy) refreshLog();
  wasBusy = busy;
  timer = window.setTimeout(tick, Math.min(busy ? BUSY_REFRESH_MS : REFRESH_MS, wakeIn));
}

$('chart').append(chart.element);
$('light-chart').append(lightChart.element);
$('overview').append(overview.element);
buildControls();
$('sensor-pick').addEventListener('change', (e) => window.location.assign(sensorUrl(e.target.value)));
$('log-refresh').addEventListener('click', refreshLog);
$('log-day').addEventListener('change', refreshLog);
$('log-level').addEventListener('change', refreshLog);
document.addEventListener('visibilitychange', () => { if (!document.hidden) tick(); });
tick();
