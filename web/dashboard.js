// Pulpit strony w chmurze (3a): otwarte alerty, czujniki z ostatnim odczytem, agentem, który je czyta, i wykresem
// ostatnich 3 dób, odczyt na żądanie (pojedynczo i wszystkie) i raport na żądanie, agenci z sygnałem życia, obecny
// nadawca. Dane z owner_dashboard co 60 s; wykresy z owner_series – przy nowym odczycie albo co 10 min.

import { h, ago, when, now, percent, celsius, dbm, plural, badge, kv, syncClock, num1 } from './format.js';
import { TimeChart } from './charts.js';
import { start } from './shell.js';
import { command, describe, follow } from './commands.js';
import { button, confirmBox, note } from './ui.js';

const STALE_HOURS = 6;
const CHART_SPAN = 72 * 3600e3;
const CHART_REFRESH_MS = 10 * 60e3;
const ALERT_TEXT = { NeedsWater: 'Do podlania', LowBattery: 'Słaba bateria', SensorSilent: 'Brak odczytów' };
const PLATFORMS = { windows: 'Windows', android: 'Android', linux: 'Linux' };
const NETWORKS = { wifi: 'Wi-Fi', cellular: 'sieć komórkowa', ethernet: 'Ethernet', other: 'inna sieć' };
const ZONE = Intl.DateTimeFormat().resolvedOptions().timeZone;

let cloud;
let data;
const charts = new Map();      // id czujnika → { chart, lastAt, loadedAt }
const reads = new Map();       // id czujnika → stan zleconego odczytu { tone, text, busy }
let refreshNow = () => {};

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
  if (hoursSince(sensor.last.at) > STALE_HOURS) return 'amber';
  return moistureTone(sensor) === 'red' ? 'red' : 'green';
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
  for (const alert of sensorAlerts(sensor)) badges.append(badge(ALERT_TEXT[alert.kind] || alert.kind, 'red'));
  if (!sensor.enabled) badges.append(badge('wyłączony', 'grey'));
  if (sensor.alertsMutedUntil) badges.append(badge(`alerty wyciszone do ${when(sensor.alertsMutedUntil)}`, 'grey'));
  const last = sensor.last;
  if (last && last.battery !== null && last.battery !== undefined && last.battery <= sensor.lowBatteryPercent) badges.append(badge('słaba bateria', 'amber'));
  if (last && hoursSince(last.at) > STALE_HOURS) badges.append(badge('dawno bez odczytu', 'amber'));
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

  const entry = chartFor(sensor);
  card.append(entry.chart.element);
  loadChart(sensor, entry);

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

function agentCard(agent) {
  const minutes = agent.lastSeenAt ? (now() - new Date(agent.lastSeenAt)) / 60_000 : Infinity;
  const silent = agent.enrolled && agent.enabled && minutes > 30;
  const card = h('div', `card accent-${!agent.enabled || !agent.enrolled ? 'grey' : silent ? 'amber' : 'green'}`);
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

// ---- raport na żądanie -------------------------------------------------------------------------------------------

function reportControls() {
  const box = h('div');
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

function render() {
  const enabled = data.sensors.filter((s) => s.enabled);
  const disabled = data.sensors.filter((s) => !s.enabled);
  const sensorsBox = h('div', 'cards');
  sensorsBox.append(...[...enabled, ...disabled].map(sensorCard));
  if (data.sensors.length === 0) sensorsBox.append(h('div', 'empty', 'W chmurze nie ma jeszcze czujników – dodasz je w Konfiguracji.'));
  const agentsBox = h('div', 'cards');
  agentsBox.append(...data.agents.map(agentCard));
  if (data.agents.length === 0) agentsBox.append(h('div', 'empty', 'W chmurze nie ma jeszcze agentów.'));

  const readable = enabled.filter((s) => s.holder);
  const readAll = button('Odczytaj wszystkie teraz', 'quiet small', () => requestRead(readable));
  readAll.disabled = readable.length === 0;

  const summary = `${enabled.length} ${plural(enabled.length, 'czujnik', 'czujniki', 'czujników')}` +
    (disabled.length ? ` (i ${disabled.length} ${plural(disabled.length, 'wyłączony', 'wyłączone', 'wyłączonych')})` : '') +
    ` · ${data.notifier ? `maile wysyła ${data.notifier.agent}` : 'żaden agent nie wysyła teraz maili'}` +
    ` · odświeżono ${when(data.serverTime, true)}`;

  const children = [h('h1', null, 'Pulpit'), h('p', 'muted', summary)];
  if (data.alertsMutedUntil) {
    children.push(h('div', 'banner tone-amber', `Alerty mailowe wyciszone do ${when(data.alertsMutedUntil)} (urlop) – raporty przychodzą dalej.`));
  }
  if (data.alerts.length > 0) {
    const alerts = h('div', 'cards');
    alerts.append(...data.alerts.map(alertCard));
    const manage = h('a', 'button-link small', 'Potwierdź, wycisz…');
    manage.href = 'alerts.html';
    children.push(h('section', null, h('div', 'section-head', h('h2', null, `Otwarte alerty (${data.alerts.length})`), manage), alerts));
  }
  children.push(
    h('section', null, h('div', 'section-head', h('h2', null, 'Czujniki'), readAll), sensorsBox),
    h('section', null, h('div', 'section-head', h('h2', null, 'Raport')), reportBox),
    h('section', null, h('div', 'section-head', h('h2', null, 'Agenci')), agentsBox));
  main.replaceChildren(...children);
}

start(async (client, element) => {
  cloud = client;
  main = element;
  const refresh = async () => {
    data = await cloud.rpc('owner_dashboard');
    syncClock(data.serverTime);
    if (!reportBox) reportBox = reportControls();
    render();
  };
  refreshNow = () => refresh().catch(() => {});
  await refresh();
  return refresh;
});
