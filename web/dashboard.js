// Pulpit strony w chmurze (3a) w dwóch widokach. Pełny: otwarte alerty, czujniki z ostatnim odczytem, agentem, który je
// czyta, i wykresem ostatnich 3 dób, odczyt na żądanie (pojedynczo i wszystkie) i raport na żądanie, agenci z sygnałem
// życia, obecny nadawca. Skrócony (domyślny na telefonie): małe kafelki z kodem czujnika (sensor-codes.js),
// wilgotnością, baterią, temperaturą i ostrzeżeniem, agenci po jednym wierszu –
// bez wykresów, więc bez owner_series i bez pobierania charts.js. Dane z owner_dashboard co 60 s; wykresy z
// owner_series – przy nowym odczycie albo co 10 min. W obu widokach czujniki od najsuchszej gleby (tile-order.js).

import { h, ago, when, now, percent, celsius, dbm, plural, badge, kv, syncClock, num1, dateFmt } from './format.js';
import { start } from './shell.js';
import { command, describe, follow } from './commands.js';
import { button, confirmBox, note, PLATFORMS } from './ui.js';
import { sensorCodes } from './sensor-codes.js';
import { tileOrder } from './tile-order.js';

const STALE_HOURS = 6;
const CHART_SPAN = 72 * 3600e3;
const CHART_REFRESH_MS = 10 * 60e3;
const ALERT_TEXT = { NeedsWater: 'Do podlania', LowBattery: 'Słaba bateria', SensorSilent: 'Brak odczytów' };
const ALERT_SHORT = { NeedsWater: 'do podlania', LowBattery: 'bateria', SensorSilent: 'brak odczytów' };
const NETWORKS = { wifi: 'Wi-Fi', cellular: 'sieć komórkowa', ethernet: 'Ethernet', other: 'inna sieć' };
const ZONE = Intl.DateTimeFormat().resolvedOptions().timeZone;
// Widok skrócony startuje na wąskim ekranie; wybór przełącznikiem trwa do zamknięcia karty (powrót ze strony czujnika
// go zachowuje, nowe otwarcie na telefonie znowu zaczyna od skróconego).
const VIEW_KEY = 'glebosaurus.dashboard.view';
const PHONE_QUERY = '(max-width: 699px)';
const SVG_NS = 'http://www.w3.org/2000/svg';

let cloud;
let data;
let view = initialView();
let codes = new Map();         // id czujnika → kod na kafelku
let TimeChart = null;          // z charts.js – wczytywany dopiero w widoku pełnym
const charts = new Map();      // id czujnika → { chart, lastAt, loadedAt }
const reads = new Map();       // id czujnika → stan zleconego odczytu { tone, text, busy }
let refreshNow = () => {};

function initialView() {
  try {
    const saved = sessionStorage.getItem(VIEW_KEY);
    if (saved === 'compact' || saved === 'full') return saved;
  } catch {
    // Przeglądarka bez sessionStorage – widok według szerokości ekranu.
  }
  return window.matchMedia(PHONE_QUERY).matches ? 'compact' : 'full';
}

async function loadCharts() {
  if (!TimeChart) ({ TimeChart } = await import('./charts.js'));
}

async function setView(next) {
  if (next === view) return;
  view = next;
  try {
    sessionStorage.setItem(VIEW_KEY, next);
  } catch {
    // Wybór obowiązuje wtedy tylko do przeładowania strony.
  }
  if (view === 'full') await loadCharts().catch(() => {});      // bez wykresów, gdy plik się nie wczytał
  render();
}

function viewSwitch() {
  const box = h('div', 'view-switch');
  box.setAttribute('role', 'group');
  box.setAttribute('aria-label', 'Widok pulpitu');
  for (const [value, label] of [['compact', 'Skrócony'], ['full', 'Pełny']]) {
    const item = button(label, 'quiet small preset', () => setView(value));
    item.setAttribute('aria-pressed', String(view === value));
    box.append(item);
  }
  return box;
}

function sensorUrl(id) {
  return `sensor.html?id=${encodeURIComponent(id)}`;
}

function moistureTone(sensor) {
  const value = sensor.last?.moisture;
  if (value === null || value === undefined) return null;
  if (value < sensor.wateringBelowPercent) return 'red';
  if (value < sensor.wateringClearAbovePercent) return 'amber';
  return 'green';
}

function hoursSince(iso) {
  return (now() - new Date(iso)) / 3_600_000;
}

function sensorAlerts(sensor) {
  return data.alerts.filter((a) => a.sensor === sensor.id);
}

function sensorAccent(sensor) {
  if (!sensor.enabled || !sensor.last) return 'grey';
  if (sensorAlerts(sensor).length > 0) return 'red';
  if (isStale(sensor)) return 'amber';
  return moistureTone(sensor) === 'red' ? 'red' : 'green';
}

function isStale(sensor) {
  return Boolean(sensor.last) && hoursSince(sensor.last.at) > STALE_HOURS;
}

function batteryLow(sensor) {
  const battery = sensor.last?.battery;
  return battery !== null && battery !== undefined && battery <= sensor.lowBatteryPercent;
}

function metric(label, value, ink) {
  return h('div', null, h('div', 'metric-label', label), h('div', `metric-value${ink ? ` ink-${ink}` : ''}`, value));
}

// ---- wykresy ---------------------------------------------------------------------------------------------------

function chartFor(sensor) {
  let entry = charts.get(sensor.id);
  if (!entry) {
    entry = { chart: new TimeChart({ compact: true, emptyText: 'Brak odczytów z ostatnich 3 dób.' }), lastAt: undefined, loadedAt: 0 };
    charts.set(sensor.id, entry);
  }
  entry.chart.setThreshold(sensor.wateringBelowPercent);
  return entry;
}

async function loadChart(sensor, entry) {
  const to = now().getTime();
  const from = to - CHART_SPAN;
  entry.chart.setView(from, to);
  const lastAt = sensor.last ? sensor.last.at : null;
  if (entry.lastAt === lastAt && Date.now() - entry.loadedAt < CHART_REFRESH_MS) return;
  entry.lastAt = lastAt;
  entry.loadedAt = Date.now();
  try {
    const series = await cloud.rpc('owner_series', {
      p_sensor: sensor.id, p_from: new Date(from).toISOString(), p_to: new Date(to).toISOString(),
      p_points: Math.round(Math.max(60, entry.chart.plotWidth / 2)), p_zone: ZONE
    });
    entry.chart.setData(series);
  } catch {
    entry.loadedAt = 0;      // kolejna próba przy następnym odświeżeniu
  }
}

// ---- odczyt na żądanie ---------------------------------------------------------------------------------------

async function requestRead(sensors) {
  const items = [];
  const sensorOf = new Map();
  for (const sensor of sensors) {
    try {
      const created = await command(cloud, 'read_now', { sensor: sensor.id });
      items.push(created.id);
      sensorOf.set(created.id, sensor.id);
      reads.set(sensor.id, { tone: 'grey', text: created.created ? 'Odczyt zlecony – czeka na agenta, który czyta czujnik…' : 'Odczyt już zlecony w ostatniej minucie.', busy: true });
    } catch (error) {
      reads.set(sensor.id, { tone: 'red', text: `Nie udało się zlecić odczytu: ${error.message}`, busy: false });
    }
  }
  render();
  if (items.length === 0) return;
  try {
    await follow(cloud, items, (item) => {
      const state = describe(item);
      const done = Boolean(item.doneAt || item.expired);
      reads.set(sensorOf.get(item.id), { ...state, busy: !done });
      if (done) refreshNow();
      else render();
    });
  } catch {
    // Stan polecenia zobaczymy przy kolejnym odświeżeniu pulpitu (nowy odczyt).
  }
}

function readButton(sensor) {
  const state = reads.get(sensor.id);
  const element = button(state && state.busy ? 'Czeka na odczyt…' : 'Odczytaj teraz', 'quiet small', () => requestRead([sensor]));
  element.disabled = Boolean(state && state.busy) || !sensor.enabled || !sensor.holder;
  if (!sensor.holder && sensor.enabled) element.title = 'Czujnika nie czyta teraz żaden agent.';
  return element;
}

// ---- karty -----------------------------------------------------------------------------------------------------

function sensorCard(sensor) {
  const card = h('div', `card accent-${sensorAccent(sensor)}`);
  const name = h('a', 'sensor-name', sensor.name);
  name.href = sensorUrl(sensor.id);
  card.append(name);
  const badges = h('div', 'badges');
  const alerts = sensorAlerts(sensor);
  for (const alert of alerts) badges.append(badge(ALERT_TEXT[alert.kind] || alert.kind, 'red'));
  if (!sensor.enabled) badges.append(badge('wyłączony', 'grey'));
  if (sensor.alertsMutedUntil) badges.append(badge(`alerty wyciszone do ${when(sensor.alertsMutedUntil)}`, 'grey'));
  // Ostrzeżenia z ostatniego odczytu – tylko gdy nie mówi tego już otwarty alert (czerwona etykieta wyżej).
  const alerted = new Set(alerts.map((a) => a.kind));
  const last = sensor.last;
  if (batteryLow(sensor) && !alerted.has('LowBattery')) badges.append(badge('słaba bateria', 'amber'));
  if (isStale(sensor) && !alerted.has('SensorSilent')) badges.append(badge('dawno bez odczytu', 'amber'));
  if (badges.childElementCount > 0) card.append(badges);

  if (last) {
    const tone = moistureTone(sensor);
    card.append(h('div', 'metrics',
      metric('Wilgotność', percent(last.moisture), tone),
      metric('Temp. gleby', celsius(last.temperature)),
      metric('Bateria', percent(last.battery))));
    if (last.moisture !== null && last.moisture !== undefined) {
      const fill = h('div', tone ? `fill-${tone}` : null);
      fill.style.width = `${Math.min(100, Math.max(0, last.moisture))}%`;
      card.append(h('div', 'bar', fill));
    }
  }
  const lines = h('div', 'lines');
  lines.append(h('div', 'muted', last
    ? `Odczyt ${ago(last.at)} (${when(last.at)})${last.agent ? `, agent ${last.agent}` : ''}`
    : 'Brak odczytów w chmurze'));
  if (sensor.enabled) {
    lines.append(h('div', sensor.holder ? 'muted' : 'problem',
      sensor.holder ? `Czyta agent ${sensor.holder}.` : 'Czujnika nie czyta teraz żaden agent (nikt go nie słyszy albo dzierżawa wygasła).'));
  }
  card.append(lines);

  if (TimeChart) {
    const entry = chartFor(sensor);
    card.append(entry.chart.element);
    loadChart(sensor, entry);
  }

  const state = reads.get(sensor.id);
  if (state) {
    const box = h('div', 'action-note');
    note(box, state.text, state.tone);
    card.append(box);
  }
  const details = h('a', 'button-link small', 'Szczegóły i historia');
  details.href = sensorUrl(sensor.id);
  card.append(h('div', 'card-actions', readButton(sensor), details));
  return card;
}

function alertCard(alert) {
  const card = h('div', `card accent-${alert.kind === 'NeedsWater' ? 'red' : 'amber'}`);
  const title = h('a', null, `${alert.sensorName} – ${ALERT_TEXT[alert.kind] || alert.kind}`);
  title.href = sensorUrl(alert.sensor);
  card.append(h('div', null, h('strong', null, title)), h('div', null, alert.detail),
    h('div', 'muted small', `Otwarty ${when(alert.openedAt)} (${ago(alert.openedAt)}) · ` +
      (alert.lastNotifiedAt ? `mail wysłany ${when(alert.lastNotifiedAt)}` : 'mail jeszcze niewysłany') +
      (alert.acknowledgedAt ? ` · potwierdzony ${when(alert.acknowledgedAt)}` : '')));
  return card;
}

function deviceText(status) {
  if (!status) return null;
  const parts = [];
  if (status.platform) parts.push(PLATFORMS[status.platform] || status.platform);
  if (status.version) parts.push(`wersja ${status.version}`);
  if (status.batteryPercent !== undefined) parts.push(`bateria ${status.batteryPercent}%${status.charging ? ' (ładuje)' : ''}`);
  if (status.batteryTemperatureCelsius !== undefined) parts.push(`akumulator ${num1.format(status.batteryTemperatureCelsius)} °C`);
  if (status.cpuTemperatureCelsius !== undefined) parts.push(`procesor ${num1.format(status.cpuTemperatureCelsius)} °C`);
  if (status.network) {
    const network = NETWORKS[status.network] || status.network;
    parts.push(status.network === 'wifi' && status.wifiDbm !== undefined ? `${network} ${dbm(status.wifiDbm)}` : network);
  }
  return parts.join(' · ');
}

function agentSilent(agent) {
  const minutes = agent.lastSeenAt ? (now() - new Date(agent.lastSeenAt)) / 60_000 : Infinity;
  return agent.enrolled && agent.enabled && minutes > 30;
}

function agentTone(agent) {
  if (!agent.enabled || !agent.enrolled) return 'grey';
  return agentSilent(agent) ? 'amber' : 'green';
}

function agentCard(agent) {
  const silent = agentSilent(agent);
  const card = h('div', `card accent-${agentTone(agent)}`);
  card.append(h('div', 'sensor-name', agent.name));
  const badges = h('div', 'badges');
  if (data.notifier && data.notifier.agent === agent.name) badges.append(badge('nadawca maili', 'green'));
  if (!agent.enabled) badges.append(badge('wyłączony', 'grey'));
  if (!agent.enrolled) badges.append(badge('czeka na rejestrację', 'grey'));
  if (silent) badges.append(badge('milczy', 'amber'));
  if (badges.childElementCount > 0) card.append(badges);
  card.append(kv([
    ['Rola', agent.canSendMail ? `zbiera i wysyła maile (priorytet ${agent.senderPriority})` : 'zbiera odczyty'],
    ['Ostatnio', agent.lastSeenAt ? `${ago(agent.lastSeenAt)} (${when(agent.lastSeenAt)})` : 'jeszcze nigdy'],
    ['Urządzenie', deviceText(agent.status)],
    ['Bluetooth', agent.status?.ble]
  ]));
  return card;
}

// ---- widok skrócony ----------------------------------------------------------------------------------------------

function svg(tag, attributes) {
  const element = document.createElementNS(SVG_NS, tag);
  for (const [name, value] of Object.entries(attributes)) element.setAttribute(name, String(value));
  return element;
}

/** Bateria jak na telefonie: obrys i wypełnienie według procentu, w kolorze tekstu obok. */
function batteryIcon(value) {
  const icon = svg('svg', { class: 'battery', viewBox: '0 0 18 10', 'aria-hidden': 'true' });
  icon.append(svg('rect', { class: 'shell', x: 0.5, y: 0.5, width: 14, height: 9 }), svg('rect', { class: 'nub', x: 15, y: 3, width: 2, height: 4 }));
  if (value !== null && value !== undefined && value > 0) {
    icon.append(svg('rect', { class: 'level', x: 2, y: 2, width: Math.max(1, Math.round((11 * Math.min(100, value)) / 100)), height: 6 }));
  }
  return icon;
}

function mutedIcon() {
  const icon = svg('svg', { class: 'bell-off', viewBox: '0 0 12 12', 'aria-hidden': 'true' });
  icon.append(svg('path', { d: 'M3 8.5V5a3 3 0 0 1 6 0v3.5l1 1H2zM5 11h2M1.5 1.5l9 9' }));
  return icon;
}

/** „14:05” / „jutro 14:05” w ciągu doby, dalej sama data („30.11”) – mieści się w etykiecie kafelka. */
function shortUntil(iso) {
  return (new Date(iso) - now()) / 3_600_000 < 20 ? when(iso) : dateFmt.format(new Date(iso));
}

/**
 * Ostrzeżenia kafelka od najważniejszego: otwarte alerty (czerwone), kłopoty bez alertu (żółte), informacje (szare).
 * Kafelek pokazuje pierwsze i liczbę pozostałych; wszystkie trafiają do etykiety dostępności.
 */
function tileFlags(sensor) {
  const flags = [];
  const alerts = sensorAlerts(sensor);
  const alerted = new Set(alerts.map((a) => a.kind));
  for (const alert of alerts) flags.push({ tone: 'red', text: ALERT_SHORT[alert.kind] || alert.kind });
  if (sensor.enabled) {
    if (batteryLow(sensor) && !alerted.has('LowBattery')) flags.push({ tone: 'amber', text: 'bateria', label: 'słaba bateria' });
    if (isStale(sensor) && !alerted.has('SensorSilent')) {
      flags.push({ tone: 'amber', text: ago(sensor.last.at), label: `ostatni odczyt ${ago(sensor.last.at)}` });
    }
    if (!sensor.holder) flags.push({ tone: 'amber', text: 'nikt nie czyta', label: 'czujnika nie czyta teraz żaden agent' });
    if (!sensor.last && !alerted.has('SensorSilent')) flags.push({ tone: 'grey', text: 'brak odczytów' });
  } else {
    flags.push({ tone: 'grey', text: 'wyłączony' });
  }
  if (sensor.alertsMutedUntil) {
    flags.push({ tone: 'grey', muted: true, text: `do ${shortUntil(sensor.alertsMutedUntil)}`, label: `alerty wyciszone do ${when(sensor.alertsMutedUntil)}` });
  }
  return flags;
}

function tileAccent(sensor, flags) {
  if (!sensor.enabled) return 'grey';
  if (flags.some((f) => f.tone === 'red') || moistureTone(sensor) === 'red') return 'red';
  if (flags.some((f) => f.tone === 'amber')) return 'amber';
  return sensor.last ? 'green' : 'grey';
}

function flagChip(flags) {
  const [first] = flags;
  const chip = h('div', `chip tone-${first.tone}`);
  if (first.muted) chip.append(mutedIcon());
  chip.append(`${first.tone === 'grey' ? '' : '! '}${first.text}${flags.length > 1 ? ` +${flags.length - 1}` : ''}`);
  return chip;
}

function sensorTile(sensor) {
  const flags = tileFlags(sensor);
  const last = sensor.enabled ? sensor.last : null;        // wyłączony – bez wartości
  const stale = Boolean(last) && isStale(sensor);
  const tone = last ? moistureTone(sensor) : null;
  const tile = h('a', `tile accent-${tileAccent(sensor, flags)}${stale ? ' stale' : ''}${sensor.enabled ? '' : ' off'}`);
  tile.href = sensorUrl(sensor.id);
  tile.title = sensor.name;
  tile.append(h('div', 'tile-head', h('span', 'tile-code', codes.get(sensor.id)),
    h('span', `tile-moisture${tone && !stale ? ` ink-${tone}` : ''}`, last ? percent(last.moisture) : '–')));
  const bar = h('div', 'bar');
  if (last && last.moisture !== null && last.moisture !== undefined) {
    const fill = h('div', tone ? `fill-${tone}` : null);
    fill.style.width = `${Math.min(100, Math.max(0, last.moisture))}%`;
    bar.append(fill);
  }
  tile.append(bar, h('div', 'tile-meta',
    h('span', last && batteryLow(sensor) ? 'low' : null, batteryIcon(last?.battery), last ? percent(last.battery) : '–'),
    h('span', null, last ? celsius(last.temperature) : '–')));
  if (flags.length > 0) tile.append(flagChip(flags));
  const spoken = [sensor.name];
  if (last) spoken.push(`wilgotność ${percent(last.moisture)}`, `temperatura gleby ${celsius(last.temperature)}`, `bateria ${percent(last.battery)}`);
  spoken.push(...flags.map((f) => f.label || f.text));
  tile.setAttribute('aria-label', spoken.join(', '));
  return tile;
}

/** Otwarte alerty jednym paskiem – kafelki i tak je oznaczają; zarządzanie na stronie Alerty. */
function alertsLine() {
  const count = data.alerts.length;
  const list = data.alerts.map((a) => `${codes.get(a.sensor) || a.sensorName} – ${ALERT_SHORT[a.kind] || a.kind}`).join(', ');
  const link = h('a', null, 'Alerty');
  link.href = 'alerts.html';
  return h('div', `banner alerts-line tone-${data.alerts.some((a) => a.kind === 'NeedsWater') ? 'red' : 'amber'}`,
    h('span', null, `${count} ${plural(count, 'otwarty alert', 'otwarte alerty', 'otwartych alertów')}: ${list}`), link);
}

/** Stan „Odczytaj wszystkie” bez kart: kto czeka na odczyt i czyj odczyt nie wyszedł. */
function readNotes(sensors) {
  const waiting = [];
  const failed = [];
  for (const sensor of sensors) {
    const state = reads.get(sensor.id);
    if (state?.busy) waiting.push(codes.get(sensor.id));
    else if (state?.tone === 'red') failed.push(codes.get(sensor.id));
  }
  const notes = [];
  if (waiting.length > 0) {
    const box = h('div');
    note(box, `Czeka na odczyt: ${waiting.join(', ')}.`, 'grey');
    notes.push(box);
  }
  if (failed.length > 0) {
    const box = h('div');
    note(box, `Odczyt nie wyszedł: ${failed.join(', ')} – szczegóły w widoku pełnym.`, 'red');
    notes.push(box);
  }
  return notes;
}

function agentRow(agent) {
  const parts = [];
  if (!agent.enabled) parts.push('wyłączony');
  else if (!agent.enrolled) parts.push('czeka na rejestrację');
  else {
    const notifier = data.notifier && data.notifier.agent === agent.name;
    parts.push(notifier ? 'nadawca maili' : agent.canSendMail ? 'zapasowy nadawca' : 'zbiera odczyty');
    if (agentSilent(agent)) parts.push('milczy');
  }
  parts.push(agent.lastSeenAt ? ago(agent.lastSeenAt) : 'jeszcze nigdy');
  const battery = agent.status?.batteryPercent;
  if (battery !== undefined && battery !== null) parts.push(`bateria ${battery}%`);
  return h('div', 'agent-row', h('span', `dot dot-${agentTone(agent)}`), h('strong', null, agent.name), h('span', 'muted', parts.join(' · ')));
}

function compactSections(sensors, readAll) {
  const sections = [];
  if (data.alerts.length > 0) sections.push(alertsLine());
  const tiles = h('div', 'tiles');
  tiles.append(...sensors.map(sensorTile));
  if (sensors.length === 0) tiles.append(h('div', 'empty', 'W chmurze nie ma jeszcze czujników – dodasz je w Konfiguracji.'));
  const agents = h('div', 'agent-rows');
  agents.append(...data.agents.map(agentRow));
  if (data.agents.length === 0) agents.append(h('div', 'empty', 'W chmurze nie ma jeszcze agentów.'));
  sections.push(
    h('section', null, h('div', 'section-head', h('h2', null, 'Czujniki'), readAll), ...readNotes(sensors), tiles),
    h('section', null, h('div', 'section-head', h('h2', null, 'Agenci')), agents, reportBox));
  return sections;
}

// ---- raport na żądanie -------------------------------------------------------------------------------------------

function reportControls() {
  const box = h('div', 'report');
  const status = h('div', 'action-note');
  status.hidden = true;
  const ask = button('Wyślij raport teraz…', 'quiet small', () => {
    ask.hidden = true;
    const recipients = data.reportRecipients;
    const confirm = confirmBox(recipients.length > 0
      ? `Wysłać PRAWDZIWY raport do: ${recipients.join(', ')}? Wyśle go ${data.notifier ? `obecny nadawca (${data.notifier.agent})` : 'nadawca, gdy przejmie blokadę'}.`
      : 'W konfiguracji nie ma odbiorców raportów ani alertów – raport nie ma dokąd pójść.', 'Tak, wyślij raport', async () => {
      if (recipients.length === 0) throw new Error('Najpierw dodaj odbiorców w Konfiguracji → Poczta.');
      const created = await command(cloud, 'report_now');
      confirm.remove();
      ask.hidden = false;
      note(status, created.created ? 'Raport zlecony – czeka na nadawcę…' : 'Raport już zlecony w ostatniej minucie.', 'grey');
      await follow(cloud, [created.id], (item) => {
        const state = describe(item);
        note(status, state.text, state.tone);
      }).catch(() => {});
    }, () => {
      confirm.remove();
      ask.hidden = false;
    });
    box.append(confirm);
  });
  box.append(h('div', 'section-actions', ask), status);
  return box;
}

// ---- strona ------------------------------------------------------------------------------------------------------

let main;
let reportBox;

function fullSections(sensors, readAll) {
  const sections = [];
  if (data.alerts.length > 0) {
    const alerts = h('div', 'cards');
    alerts.append(...data.alerts.map(alertCard));
    const manage = h('a', 'button-link small', 'Potwierdź, wycisz…');
    manage.href = 'alerts.html';
    sections.push(h('section', null, h('div', 'section-head', h('h2', null, `Otwarte alerty (${data.alerts.length})`), manage), alerts));
  }
  const sensorsBox = h('div', 'cards');
  sensorsBox.append(...sensors.map(sensorCard));
  if (sensors.length === 0) sensorsBox.append(h('div', 'empty', 'W chmurze nie ma jeszcze czujników – dodasz je w Konfiguracji.'));
  const agentsBox = h('div', 'cards');
  agentsBox.append(...data.agents.map(agentCard));
  if (data.agents.length === 0) agentsBox.append(h('div', 'empty', 'W chmurze nie ma jeszcze agentów.'));
  sections.push(
    h('section', null, h('div', 'section-head', h('h2', null, 'Czujniki'), readAll), sensorsBox),
    h('section', null, h('div', 'section-head', h('h2', null, 'Raport')), reportBox),
    h('section', null, h('div', 'section-head', h('h2', null, 'Agenci')), agentsBox));
  return sections;
}

function render() {
  codes = sensorCodes(data.sensors);
  const enabled = data.sensors.filter((s) => s.enabled);
  const disabled = data.sensors.filter((s) => !s.enabled);

  const readable = enabled.filter((s) => s.holder);
  const readAll = button(view === 'compact' ? 'Odczytaj wszystkie' : 'Odczytaj wszystkie teraz', 'quiet small', () => requestRead(readable));
  readAll.disabled = readable.length === 0;

  const summary = `${enabled.length} ${plural(enabled.length, 'czujnik', 'czujniki', 'czujników')}` +
    (disabled.length ? ` (i ${disabled.length} ${plural(disabled.length, 'wyłączony', 'wyłączone', 'wyłączonych')})` : '') +
    ` · ${data.notifier ? `maile wysyła ${data.notifier.agent}` : 'żaden agent nie wysyła teraz maili'}` +
    ` · odświeżono ${when(data.serverTime, true)}`;

  const children = [h('div', 'page-head', h('h1', null, 'Pulpit'), viewSwitch()), h('p', 'muted', summary)];
  if (data.alertsMutedUntil) {
    children.push(h('div', 'banner tone-amber', `Alerty mailowe wyciszone do ${when(data.alertsMutedUntil)} (urlop) – raporty przychodzą dalej.`));
  }
  const sensors = tileOrder(data.sensors);       // oba widoki: od najsuchszej gleby, wyłączone na końcu
  children.push(...(view === 'compact' ? compactSections(sensors, readAll) : fullSections(sensors, readAll)));
  main.replaceChildren(...children);
}

start(async (client, element) => {
  cloud = client;
  main = element;
  const refresh = async () => {
    data = await cloud.rpc('owner_dashboard');
    syncClock(data.serverTime);
    if (view === 'full') await loadCharts().catch(() => {});
    if (!reportBox) reportBox = reportControls();
    render();
  };
  refreshNow = () => refresh().catch(() => {});
  await refresh();
  return refresh;
});
