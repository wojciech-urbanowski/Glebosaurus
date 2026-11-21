// Pulpit strony w chmurze (etap 1): czujniki z ostatnim odczytem i agentem, który je czyta, agenci z ostatnim sygnałem
// życia, obecny nadawca. Dane z owner_dashboard, wstawiane wyłącznie jako tekst.

import { h, ago, when, now, percent, celsius, dbm, plural, badge, kv, syncClock, num1 } from './format.js';
import { start } from './shell.js';

const STALE_HOURS = 6;

function moistureTone(sensor) {
  const value = sensor.last?.moisture;
  if (value === null || value === undefined) return null;
  if (value < sensor.wateringBelowPercent) return 'red';
  if (value < sensor.wateringClearAbovePercent) return 'amber';
  return 'green';
}

function sensorAccent(sensor) {
  if (!sensor.enabled) return 'grey';
  if (!sensor.last) return 'grey';
  const hours = (now() - new Date(sensor.last.at)) / 3_600_000;
  if (hours > STALE_HOURS) return 'amber';
  return moistureTone(sensor) === 'red' ? 'red' : 'green';
}

function metric(label, value, ink) {
  return h('div', null, h('div', 'metric-label', label), h('div', `metric-value${ink ? ` ink-${ink}` : ''}`, value));
}

function sensorCard(sensor) {
  const card = h('div', `card accent-${sensorAccent(sensor)}`);
  card.append(h('div', 'sensor-name', sensor.name));
  const badges = h('div', 'badges');
  if (!sensor.enabled) badges.append(badge('wyłączony', 'grey'));
  const last = sensor.last;
  if (last && last.battery !== null && last.battery !== undefined && last.battery <= sensor.lowBatteryPercent) badges.append(badge('słaba bateria', 'amber'));
  if (last && (now() - new Date(last.at)) / 3_600_000 > STALE_HOURS) badges.append(badge('dawno bez odczytu', 'amber'));
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
  lines.append(h('div', 'muted small', `Podlać poniżej ${sensor.wateringBelowPercent}% · MAC ${sensor.mac}`));
  card.append(lines);
  return card;
}

const PLATFORMS = { windows: 'Windows', android: 'Android', linux: 'Linux' };
const NETWORKS = { wifi: 'Wi-Fi', cellular: 'sieć komórkowa', ethernet: 'Ethernet', other: 'inna sieć' };

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

function agentCard(agent, notifier) {
  const minutes = agent.lastSeenAt ? (now() - new Date(agent.lastSeenAt)) / 60_000 : Infinity;
  const silent = agent.enrolled && agent.enabled && minutes > 30;
  const card = h('div', `card accent-${!agent.enabled || !agent.enrolled ? 'grey' : silent ? 'amber' : 'green'}`);
  card.append(h('div', 'sensor-name', agent.name));
  const badges = h('div', 'badges');
  if (notifier && notifier.agent === agent.name) badges.append(badge('nadawca maili', 'green'));
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

function section(title, ...children) {
  return h('section', null, h('div', 'section-head', h('h2', null, title)), ...children);
}

start(async (cloud, main) => {
  const sensorsBox = h('div', 'cards');
  const agentsBox = h('div', 'cards');
  const summary = h('p', 'muted');
  main.append(h('h1', null, 'Pulpit'), summary, section('Czujniki', sensorsBox), section('Agenci', agentsBox));

  const refresh = async () => {
    const data = await cloud.rpc('owner_dashboard');
    syncClock(data.serverTime);
    const enabled = data.sensors.filter((s) => s.enabled);
    const disabled = data.sensors.filter((s) => !s.enabled);
    sensorsBox.replaceChildren(...[...enabled, ...disabled].map(sensorCard));
    if (data.sensors.length === 0) sensorsBox.replaceChildren(h('div', 'empty', 'W chmurze nie ma jeszcze czujników.'));
    agentsBox.replaceChildren(...data.agents.map((a) => agentCard(a, data.notifier)));
    if (data.agents.length === 0) agentsBox.replaceChildren(h('div', 'empty', 'W chmurze nie ma jeszcze agentów.'));
    summary.textContent = `${enabled.length} ${plural(enabled.length, 'czujnik', 'czujniki', 'czujników')}` +
      (disabled.length ? ` (i ${disabled.length} ${plural(disabled.length, 'wyłączony', 'wyłączone', 'wyłączonych')})` : '') +
      ` · ${data.notifier ? `maile wysyła ${data.notifier.agent}` : 'żaden agent nie wysyła teraz maili'}` +
      ` · odświeżono ${when(data.serverTime, true)}`;
  };
  await refresh();
  return refresh;
});
