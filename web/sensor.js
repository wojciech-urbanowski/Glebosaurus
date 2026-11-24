// Strona jednego czujnika w chmurze (sensor.html?id=…): stan teraz z odczytem na żądanie, przeglądalny wykres całej
// historii z paskiem przeglądu (jak w lokalnym panelu – wspólny charts.js; czujnik Flower Care ma pod nim drugi wykres,
// światła i żyzności, z tym samym oknem), otwarte alerty, kto go słyszy, ustawienia i ostatnie odczyty. Stan z owner_sensor co 60 s; wykres dociąga dane okna z zapasem po bokach przez owner_series.

import { h, now, syncClock, when, ago, percent, celsius, dbm, plural, kv, dateFmt, num1, lux, microSiemens, whole, isFlowerCare, temperatureLabel } from './format.js';
import { TimeChart, OverviewStrip, resolutionText, soilSeries, LIGHT_SERIES } from './charts.js';
import { start } from './shell.js';
import { command, describe, follow } from './commands.js';
import { button, note } from './ui.js';

const HOUR = 3600e3;
const DAY = 24 * HOUR;
const MIN_SPAN = 2 * HOUR;
const DEFAULT_SPAN = 7 * DAY;
const OVERVIEW_REFETCH_MS = 15 * 60e3;
const READINGS_PAGE = 96;
const PRESETS = [['3 doby', 3 * DAY], ['7 dni', 7 * DAY], ['30 dni', 30 * DAY], ['90 dni', 90 * DAY], ['Rok', 365 * DAY], ['Całość', null]];
const ALERT_TEXT = { NeedsWater: 'Do podlania', LowBattery: 'Słaba bateria', SensorSilent: 'Brak odczytów' };
const ZONE = Intl.DateTimeFormat().resolvedOptions().timeZone;
const sensorId = new URLSearchParams(window.location.search).get('id');

let cloud;
let page;                       // odpowiedź owner_sensor
let first = null;               // najstarszy odczyt (ms); null = brak
let view = null;                // { from, to } w ms
let following = true;           // okno kończy się „teraz”
let loaded = null;              // { from, to, span, bucketMinutes }
let loadSeq = 0;
let overviewAt = 0;
let latestAt;
let readingsCount = READINGS_PAGE;
let readState = null;

const chart = new TimeChart({ interactive: true, onView: (v, settled) => changeView(v.from, v.to, settled) });
/** Światło i żyzność – tylko czujnik Flower Care; te same dane z owner_series i to samo okno co wykres wyżej. */
const lightChart = new TimeChart({ interactive: true, series: LIGHT_SERIES, onView: (v, settled) => changeView(v.from, v.to, settled) });
const overview = new OverviewStrip({ onView: (v, settled) => changeView(v.from, v.to, settled) });
const el = {};

function series(from, to, points) {
  return cloud.rpc('owner_series', {
    p_sensor: sensorId, p_from: new Date(from).toISOString(), p_to: new Date(to).toISOString(),
    p_points: Math.round(Math.min(2000, Math.max(10, points))), p_zone: ZONE
  });
}

// ---- okno wykresu (jak w lokalnym panelu) ------------------------------------------------------------------------

function historyBounds() {
  const end = now().getTime();
  return { start: first ?? end - DEFAULT_SPAN, end };
}

function clampView(from, to) {
  const { start: begin, end } = historyBounds();
  const span = Math.min(Math.max(to - from, MIN_SPAN), Math.max(end - begin, 3 * DAY));
  let right = Math.min(to, end);
  let left = right - span;
  const lower = Math.min(begin, end - span);
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

async function loadChart() {
  const span = view.to - view.from;
  const from = view.from - span;
  const to = Math.min(now().getTime(), view.to + span);
  const seq = ++loadSeq;
  chart.setLoading(true);
  lightChart.setLoading(true);
  try {
    const data = await series(from, to, ((chart.plotWidth / 2) * (to - from)) / span);
    if (seq !== loadSeq) return;
    loaded = { from, to: Math.max(to, view.to), span, bucketMinutes: data.bucketMinutes };
    const firstAt = data.firstAt ? Date.parse(data.firstAt) : null;
    const firstChanged = firstAt !== first;
    first = firstAt;
    chart.setData(data);
    lightChart.setData(data);
    renderViewInfo();
    if (firstChanged || Date.now() - overviewAt > OVERVIEW_REFETCH_MS) loadOverview();
  } catch (error) {
    if (seq === loadSeq) el.viewInfo.textContent = `Nie udało się wczytać danych wykresu: ${error.message}`;
  } finally {
    if (seq === loadSeq) {
      chart.setLoading(false);
      lightChart.setLoading(false);
    }
  }
}

async function loadOverview() {
  overviewAt = Date.now();
  const { start: begin, end } = historyBounds();
  const range = { from: Math.min(begin, end - 3 * DAY), to: end };
  try {
    const data = await series(range.from, range.to, (overview.element.clientWidth || 600) / 2);
    overview.setData(range, data);
    overview.setView(view.from, view.to);
  } catch {
    overviewAt = 0;
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
  if (loaded) parts.push(first === null ? 'brak odczytów w chmurze' : resolutionText(loaded.bucketMinutes));
  el.viewInfo.textContent = parts.join(' · ');
  const span = view.to - view.from;
  for (const presetButton of el.presets.children) {
    const preset = presetButton.dataset.span ? Number(presetButton.dataset.span) : null;
    const active = following && (preset === null ? first !== null && view.from <= first + 60e3 : Math.abs(span - preset) < 60e3);
    presetButton.setAttribute('aria-pressed', String(active));
  }
}

function controls() {
  const presets = h('span', 'presets');
  for (const [label, span] of PRESETS) {
    const preset = button(label, 'quiet small preset', () => {
      const end = now().getTime();
      changeView(span ? end - span : (first ?? end - DEFAULT_SPAN), end, true);
    });
    if (span) preset.dataset.span = String(span);
    presets.append(preset);
  }
  const nav = h('span', 'nav',
    button('◀', 'quiet small', () => chart.pan(-0.5)), button('−', 'quiet small', () => chart.zoom(2)),
    button('+', 'quiet small', () => chart.zoom(0.5)), button('▶', 'quiet small', () => chart.pan(0.5)),
    button('Teraz', 'quiet small', () => {
      const end = now().getTime();
      changeView(end - (view.to - view.from), end, true);
    }));
  for (const [element, label] of [[nav.children[0], 'Wcześniej'], [nav.children[1], 'Pomniejsz'], [nav.children[2], 'Powiększ'], [nav.children[3], 'Później']]) {
    element.title = label;
    element.setAttribute('aria-label', label);
  }
  el.presets = presets;
  const bar = h('div', 'toolbar chart-toolbar', presets, nav);
  bar.setAttribute('role', 'group');
  bar.setAttribute('aria-label', 'Zakres wykresu');
  return bar;
}

// ---- reszta strony ---------------------------------------------------------------------------------------------

function metric(label, value, ink) {
  return h('div', null, h('div', 'metric-label', label), h('div', `metric-value${ink ? ` ink-${ink}` : ''}`, value));
}

function renderNow() {
  const sensor = page.sensor;
  const last = page.last;
  const tone = last && last.moisture !== null && last.moisture !== undefined
    ? (last.moisture < sensor.wateringBelowPercent ? 'red' : last.moisture < sensor.wateringClearAbovePercent ? 'amber' : 'green') : null;
  const card = h('article', `card accent-${page.alerts.length > 0 ? 'red' : last ? 'green' : 'grey'}`);
  if (last) {
    card.append(h('div', 'metrics', metric('Wilgotność', percent(last.moisture), tone), metric(temperatureLabel(sensor), celsius(last.temperature)),
      metric('Bateria', percent(last.battery), last.battery !== null && last.battery <= sensor.lowBatteryPercent ? 'amber' : null)));
    if (last.moisture !== null && last.moisture !== undefined) {
      const fill = h('div', tone ? `fill-${tone}` : null);
      fill.style.width = `${Math.min(100, Math.max(0, last.moisture))}%`;
      card.append(h('div', 'bar', fill));
    }
    if (isFlowerCare(sensor)) card.append(h('div', 'metrics', metric('Światło, lx', whole(last.light)), metric('Żyzność, µS/cm', whole(last.conductivity))));
  }
  card.append(h('div', 'lines',
    h('div', 'muted', last ? `Odczyt ${ago(last.at)} (${when(last.at)})${last.agent ? `, agent ${last.agent}` : ''}` : 'Brak odczytów w chmurze'),
    h('div', page.holder || !sensor.enabled ? 'muted' : 'problem', !sensor.enabled ? 'Czujnik jest wyłączony.'
      : page.holder ? `Czyta agent ${page.holder.agent} (dzierżawa do ${when(page.holder.expiresAt)}).`
        : 'Czujnika nie czyta teraz żaden agent (nikt go nie słyszy albo dzierżawa wygasła).')));
  if (readState) {
    const box = h('div', 'action-note');
    note(box, readState.text, readState.tone);
    card.append(box);
  }
  const read = button(readState && readState.busy ? 'Czeka na odczyt…' : 'Odczytaj teraz', 'quiet small', requestRead);
  read.disabled = Boolean(readState && readState.busy) || !sensor.enabled || !page.holder;
  card.append(h('div', 'card-actions', read));
  el.now.replaceChildren(card);
}

async function requestRead() {
  try {
    const created = await command(cloud, 'read_now', { sensor: sensorId });
    readState = { tone: 'grey', text: 'Odczyt zlecony – czeka na agenta, który czyta czujnik (zwykle 1–2 min)…', busy: true };
    renderNow();
    await follow(cloud, [created.id], (item) => {
      const done = Boolean(item.doneAt || item.expired);
      readState = { ...describe(item), busy: !done };
      renderNow();
      if (done) refreshPage();
    });
  } catch (error) {
    readState = { tone: 'red', text: `Nie udało się zlecić odczytu: ${error.message}`, busy: false };
    renderNow();
  }
}

function renderAlerts() {
  el.alerts.replaceChildren(...(page.alerts.length > 0 ? page.alerts.map((alert) => {
    const card = h('div', `card accent-${alert.kind === 'NeedsWater' ? 'red' : 'amber'}`);
    card.append(h('div', null, h('strong', null, ALERT_TEXT[alert.kind] || alert.kind)), h('div', null, alert.detail),
      h('div', 'muted small', `Otwarty ${when(alert.openedAt)} (${ago(alert.openedAt)}) · ` +
        (alert.lastNotifiedAt ? `mail wysłany ${when(alert.lastNotifiedAt)}` : 'mail jeszcze niewysłany')));
    return card;
  }) : [h('div', 'empty', 'Brak otwartych alertów tego czujnika.')]));
}

function renderDiagnostics() {
  const card = h('div', 'card');
  card.append(kv([
    ['Dzierżawa', page.holder ? `${page.holder.agent} do ${when(page.holder.expiresAt, true)}` : 'nikt – czujnik czeka na agenta, który go usłyszy'],
    ['Najstarszy odczyt', page.firstAt ? `${dateFmt.format(new Date(page.firstAt))} ${new Date(page.firstAt).getFullYear()} (${ago(page.firstAt)})` : 'brak odczytów']
  ]));
  if (page.sightings.length > 0) {
    const table = h('table', 'readings');
    table.append(h('thead', null, h('tr', null, h('th', null, 'Agent'), h('th', null, 'Sygnał'), h('th', null, 'Słyszany'), h('th', null, 'Ostatni odczyt'))));
    const body = h('tbody');
    for (const s of page.sightings) {
      body.append(h('tr', null, h('td', null, s.agent), h('td', null, s.rssi === null ? '–' : dbm(s.rssi)), h('td', null, s.heardAt ? ago(s.heardAt) : '–'),
        h('td', null, s.lastSuccessAt ? `${ago(s.lastSuccessAt)}${s.failures > 0 ? ` (${s.failures} ${plural(s.failures, 'porażka', 'porażki', 'porażek')} z rzędu)` : ''}` : '–')));
    }
    table.append(body);
    card.append(h('div', 'muted small', 'Kto słyszy czujnik (ostatni nasłuch każdego agenta):'), h('div', 'table-wrap', table));
  } else {
    card.append(h('div', 'muted small', 'Żaden agent jeszcze go nie słyszał.'));
  }
  el.diagnostics.replaceChildren(card);
}

function renderSettings() {
  const s = page.sensor;
  const card = h('div', 'card');
  card.append(kv([
    ['Odczyt co', `${num1.format(s.readIntervalMinutes).replace(',0', '')} min${s.ownReadInterval ? '' : ' (wspólny)'}`],
    ['Podlać poniżej', `${s.wateringBelowPercent}% (${s.consecutiveReadings} ${plural(s.consecutiveReadings, 'odczyt', 'odczyty', 'odczytów')} z rzędu)`],
    ['Alert wygasa od', `${s.wateringClearAbovePercent}%`],
    ['Słaba bateria przy', `${s.lowBatteryPercent}% lub mniej`],
    ['Brak odczytów po', `${num1.format(s.silentAfterHours).replace(',0', '')} godz.`],
    ['Rodzaj', isFlowerCare(s) ? 'Xiaomi Flower Care (bez klucza, po adresie MAC)' : 'Tuya BLE'],
    ['Adres MAC', s.mac],
    ['Produkt Tuya', isFlowerCare(s) ? null : s.productId || '–'],
    ['Identyfikator', s.id]
  ]));
  const link = h('a', 'button-link small', 'Zmień w konfiguracji');
  link.href = `config.html#sensor:${encodeURIComponent(s.id)}`;
  card.append(h('div', 'card-actions', link));
  el.settings.replaceChildren(card);
}

async function loadReadings() {
  try {
    const readings = await cloud.rpc('owner_readings', { p_id: sensorId, p_count: readingsCount });
    if (readings.length === 0) {
      el.readings.replaceChildren(h('div', 'empty', 'Brak odczytów w chmurze.'));
      return;
    }
    const extra = isFlowerCare(page.sensor);
    const table = h('table', 'readings');
    table.append(h('thead', null, h('tr', null, h('th', null, 'Czas'), h('th', null, 'Wilgotność'), h('th', null, 'Temperatura'), h('th', null, 'Bateria'),
      extra ? h('th', null, 'Światło') : null, extra ? h('th', null, 'Żyzność') : null)));
    const body = h('tbody');
    for (const r of readings) {
      body.append(h('tr', null, h('td', null, when(r.at)), h('td', null, percent(r.moisture)), h('td', null, celsius(r.temperature)), h('td', null, percent(r.battery)),
        extra ? h('td', null, lux(r.light)) : null, extra ? h('td', null, microSiemens(r.conductivity)) : null));
    }
    table.append(body);
    const parts = [h('div', 'muted small', `Ostatnie ${readings.length} ${plural(readings.length, 'odczyt', 'odczyty', 'odczytów')}, od najnowszego:`), h('div', 'table-wrap', table)];
    if (readings.length === readingsCount && readingsCount < 1000) {
      parts.push(h('div', 'card-actions', button('Pokaż więcej', 'quiet small', () => {
        readingsCount = Math.min(1000, readingsCount * 2);
        loadReadings();
      })));
    }
    el.readings.replaceChildren(...parts);
  } catch (error) {
    el.readings.replaceChildren(h('div', 'empty', `Nie udało się wczytać odczytów: ${error.message}`));
  }
}

let refreshPage = () => {};

function section(title, ...children) {
  return h('section', null, h('h2', null, title), ...children);
}

start(async (client, main) => {
  cloud = client;
  if (!sensorId) {
    window.location.replace('index.html');
    return undefined;
  }
  el.name = h('h1', null, 'Czujnik');
  el.picker = h('select');
  el.picker.setAttribute('aria-label', 'Inny czujnik');
  el.picker.addEventListener('change', () => window.location.assign(`sensor.html?id=${encodeURIComponent(el.picker.value)}`));
  el.line = h('div', 'muted small');
  el.now = h('div');
  el.viewInfo = h('div', 'view-info small');
  el.alerts = h('div');
  el.diagnostics = h('div');
  el.settings = h('div');
  el.readings = h('div');
  el.light = section('Światło i żyzność', lightChart.element,
    h('div', 'muted small hint', 'Światło w luksach (pełne słońce to kilkadziesiąt tysięcy, mieszkanie zwykle kilkaset–kilka tysięcy); żyzność to przewodność gleby w µS/cm – spada, gdy gleba wysycha albo brakuje nawozu.'));
  el.light.hidden = true;
  main.append(
    h('div', 'page-head', h('div', 'page-title', el.name, el.line), h('div', 'picker', el.picker)),
    section('Teraz', el.now),
    section('Wilgotność i temperatura', controls(), chart.element, el.viewInfo, overview.element,
      h('div', 'muted small hint', 'Przeciągnij wykres albo prostokąt na pasku pod nim, żeby przesunąć okres; powiększenie: Ctrl + kółko myszy, dwa palce albo przyciski.')),
    el.light,
    section('Otwarte alerty', el.alerts),
    section('Odczyty', el.readings),
    section('Kto go czyta i słyszy', el.diagnostics),
    section('Ustawienia czujnika', el.settings));

  const refresh = async () => {
    page = await cloud.rpc('owner_sensor', { p_id: sensorId });
    syncClock(page.serverTime);
    const sensor = page.sensor;
    document.title = `${sensor.name} – Glebosaurus`;
    el.name.textContent = sensor.name;
    el.line.textContent = sensor.archivedAt ? `W archiwum od ${when(sensor.archivedAt)}` : sensor.enabled ? `MAC ${sensor.mac}` : 'Wyłączony';
    if (el.picker.childElementCount !== page.others.length) {
      el.picker.replaceChildren(...page.others.map((o) => {
        const option = h('option', null, o.name);
        option.value = o.id;
        return option;
      }));
    }
    el.picker.value = sensorId;
    chart.setThreshold(sensor.wateringBelowPercent);
    if (isFlowerCare(sensor) && el.light.hidden) {
      el.light.hidden = false;
      chart.setSeries(soilSeries(true));
    }
    renderNow();
    renderAlerts();
    renderDiagnostics();
    renderSettings();
    const newestAt = page.last ? page.last.at : null;
    const fresh = newestAt !== latestAt;
    latestAt = newestAt;
    if (!view) {
      first = page.firstAt ? Date.parse(page.firstAt) : null;
      const end = now().getTime();
      changeView(end - DEFAULT_SPAN, end, true);
    } else if (following) {
      const end = now().getTime();
      changeView(end - (view.to - view.from), end, false);
      if (fresh) loadChart();
    }
    if (fresh) loadReadings();
  };
  refreshPage = () => refresh().catch(() => {});
  await refresh();
  return refresh;
});
