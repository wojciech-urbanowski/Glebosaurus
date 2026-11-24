// Wspólne narzędzia stron panelu (pulpit i strona czujnika): API, akcje, karty czujników, alerty, dziennik. Formaty i
// składanie elementów są w format.js (ten sam plik ma strona w chmurze). Wszystkie dane trafiają na stronę jako tekst
// (textContent / append(string)), nigdy jako HTML – nazwy czujników i wpisy dziennika nie mogą wstrzyknąć kodu.

import { $, now, h, when, ago, until, percent, celsius, dbm, plural, badge, dateFmt, whole, isFlowerCare, temperatureLabel } from './format.js';

export * from './format.js';

export async function api(path, method) {
  const init = { credentials: 'same-origin', cache: 'no-store' };
  if (method === 'POST') {
    init.method = 'POST';
    init.headers = { 'X-Glebosaurus-Action': '1' };
  }
  const response = await fetch(path, init);
  if (response.status === 401) {
    window.location.assign('/login');
    throw new Error('Sesja wygasła.');
  }
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return response.json();
}

export function sensorUrl(sensorId) {
  return `/sensor?id=${encodeURIComponent(sensorId)}`;
}

/** Odczyty do wykresu z okna [from, to] (Date), najwyżej „points” punktów – uśrednianie robi monitor. */
export function fetchSeries(sensorId, from, to, points) {
  const query = new URLSearchParams({ from: from.toISOString(), to: to.toISOString(), points: String(Math.round(points)) });
  return api(`/api/sensors/${encodeURIComponent(sensorId)}/series?${query}`);
}

// ---- notki i odczyt na żądanie ------------------------------------------------------------------------------

export function showActionNote(text, tone, id = 'action-note') {
  const note = $(id);
  note.className = `action-note tone-${tone}`;
  note.textContent = text;
  note.hidden = false;
}

const REQUEST_TEXT = {
  queued: 'Odczyt zlecony',
  alreadyQueued: 'Już zlecony w ostatniej minucie',
  unknown: 'Nieznany czujnik',
  unavailable: 'Chmura niedostępna – nie zlecono'
};

function describeRequest(result) {
  const groups = new Map();
  for (const item of result.items) {
    if (!groups.has(item.status)) groups.set(item.status, []);
    groups.get(item.status).push(item.sensorName || item.sensorId);
  }
  // „Już zlecony” znaczy: to samo polecenie zlecono w ostatniej minucie (z tego albo innego panelu).
  const text = (status) => REQUEST_TEXT[status] || status;
  return [...groups].map(([status, names]) => `${text(status)}: ${names.join(', ')}`).join(' · ');
}

/** Zleca odczyt na żądanie (sensorId = null → wszystkie czujniki), pokazuje wynik w notce i wywołuje „done”. */
export async function requestRead(sensorId, done) {
  const path = sensorId ? `/api/sensors/${encodeURIComponent(sensorId)}/read` : '/api/read-all';
  try {
    const result = await api(path, 'POST');
    const queued = result.items.some((i) => i.status === 'queued');
    const hint = queued ? ' Polecenie trafi przez chmurę do agenta, który czyta czujnik; wynik pojawi się zwykle w ciągu 1–2 min.' : '';
    showActionNote(`${describeRequest(result)}.${hint}`, queued ? 'green' : 'amber');
  } catch (error) {
    showActionNote(`Nie udało się zlecić odczytu: ${error.message}`, 'red');
  }
  done();
}

/** Przycisk „Odczytaj teraz” według stanu czujnika (trwa / czeka / odstęp dla baterii). */
export function readButton(sensor, onClick) {
  const read = h('button', 'quiet small');
  read.type = 'button';
  if (sensor.readingNow) {
    read.textContent = 'Odczyt trwa…';
    read.disabled = true;
  } else if (sensor.readRequested) {
    read.textContent = 'Czeka na odczyt…';
    read.disabled = true;
  } else if (sensor.readOnRequestFrom && new Date(sensor.readOnRequestFrom) > now()) {
    read.textContent = `Odczyt możliwy od ${when(sensor.readOnRequestFrom, true)}`;
    read.disabled = true;
  } else {
    read.textContent = 'Odczytaj teraz';
    read.addEventListener('click', () => {
      read.disabled = true;
      onClick();
    });
  }
  return read;
}

// ---- czujnik: stan teraz ------------------------------------------------------------------------------------

export function sensorAccent(sensor) {
  if (sensor.openAlerts.length > 0 || sensor.lastResult === 'unbound') return 'red';
  if (sensor.failures > 0 || sensor.batteryLow) return 'amber';
  if (sensor.latest) return 'green';
  return 'grey';
}

function metric(label, value, ink) {
  return h('div', null, h('div', 'metric-label', label), h('div', `metric-value${ink ? ` ink-${ink}` : ''}`, value));
}

/** Plakietki stanu (alerty, trwający odczyt, nieudane próby); null, gdy nie ma żadnej. */
export function sensorBadges(sensor) {
  const badges = h('div', 'badges');
  for (const alert of sensor.openAlerts) badges.append(badge(alert, 'red'));
  if (sensor.readingNow) badges.append(badge('odczyt trwa…', 'grey'));
  if (sensor.lastResult === 'unbound') badges.append(badge('niesparowany', 'red'));
  else if (sensor.failures > 0) badges.append(badge(`${sensor.failures} ${plural(sensor.failures, 'nieudana próba', 'nieudane próby', 'nieudanych prób')}`, 'amber'));
  return badges.childElementCount > 0 ? badges : null;
}

/**
 * Trzy liczby (wilgotność, temperatura, bateria) i pasek wilgotności w kolorze progu – czujnik Flower Care także światło i
 * żyzność; null bez odczytu.
 */
export function sensorMetrics(sensor) {
  const latest = sensor.latest;
  if (!latest) return null;
  const box = h('div', null, h('div', 'metrics',
    metric('Wilgotność', percent(latest.moisturePercent), sensor.moistureTone),
    metric(temperatureLabel(sensor), celsius(latest.soilTemperatureCelsius)),
    metric('Bateria', percent(latest.batteryPercent), sensor.batteryLow ? 'amber' : null)));
  if (latest.moisturePercent !== null && latest.moisturePercent !== undefined) {
    const fill = h('div', sensor.moistureTone ? `fill-${sensor.moistureTone}` : null);
    fill.style.width = `${Math.min(100, Math.max(0, latest.moisturePercent))}%`;
    box.append(h('div', 'bar', fill));
  }
  if (isFlowerCare(sensor)) {
    box.append(h('div', 'metrics', metric('Światło, lx', whole(latest.lightLux)), metric('Żyzność, µS/cm', whole(latest.conductivityMicroSiemens))));
  }
  return box;
}

/** Kto czyta czujnik (agent z dzierżawą). */
export function holderText(sensor) {
  if (!sensor.holder) return 'Czujnika nie czyta teraz żaden agent (nikt go nie słyszy albo dzierżawa wygasła).';
  return sensor.holderIsMe ? 'Czyta ten agent.' : `Czyta agent ${sensor.holder}.`;
}

/** Wiersze pod liczbami: wiek odczytu i następny odczyt, ewentualnie problem z ostatnią próbą. */
export function sensorLines(sensor, withSignal) {
  const latest = sensor.latest;
  const lines = h('div', 'lines');
  const nobody = !sensor.holder;
  lines.append(h('div', 'muted', latest
    ? `Odczyt ${ago(latest.at)} (${when(latest.at)})${nobody ? '' : ` · następny ${until(sensor.nextReadAt)}`}`
    : `Brak odczytów w bazie${nobody ? '' : ` · pierwszy ${until(sensor.nextReadAt)}`}`));
  lines.append(h('div', nobody ? 'problem' : 'muted', holderText(sensor)));
  if (withSignal && sensor.lastSignalDbm !== null && sensor.lastSignalDbm !== undefined) {
    lines.append(h('div', 'muted', `Sygnał ${dbm(sensor.lastSignalDbm)} (${ago(sensor.lastSignalAt)}) – tu, u tego agenta`));
  }
  if (withSignal && sensor.bestOtherAgent) {
    lines.append(h('div', 'muted', `Najlepiej z innych słyszy go ${sensor.bestOtherAgent} (${dbm(sensor.bestOtherRssi)})`));
  }
  if (sensor.lastResult && sensor.lastResult !== 'ok') {
    const retry = sensor.retryAt ? `; ponowienie ${until(sensor.retryAt)}` : '';
    lines.append(h('div', 'problem', `Ostatnia próba ${when(sensor.lastAttemptAt)}: ${sensor.lastResultText}${retry}`));
  }
  return lines;
}

// ---- alerty -------------------------------------------------------------------------------------------------

export function alertCard(alert, withSensorName) {
  const card = h('div', `card accent-${alert.kind === 'NeedsWater' ? 'red' : 'amber'}`);
  card.append(h('div', null, h('strong', null, withSensorName ? `${alert.sensorName} – ${alert.kindText}` : alert.kindText)));
  card.append(h('div', null, alert.detail));
  card.append(h('div', 'muted small', `Otwarty ${when(alert.openedAt)} (${ago(alert.openedAt)}) · ` +
    (alert.lastNotifiedAt ? `mail wysłany ${when(alert.lastNotifiedAt)}` : 'mail jeszcze niewysłany')));
  return card;
}

// ---- dziennik -----------------------------------------------------------------------------------------------

/** Wypełnia listę dni dziennika (dziś, wczoraj, … do retencji). */
export function buildLogDays(select, retentionDays) {
  if (select.dataset.built) return;
  select.dataset.built = '1';
  const today = now();
  for (let days = 2; days <= retentionDays; days++) {
    const day = new Date(today);
    day.setDate(today.getDate() - days);
    const option = h('option', null, `${days} dni temu (${dateFmt.format(day)})`);
    option.value = String(days);
    select.append(option);
  }
}

/**
 * Końcówka dziennika wybranego dnia. „contains” zawęża do wpisów z tym tekstem (strona czujnika podaje jego nazwę);
 * „subject” to dopowiedzenie do komunikatów („o czujniku X”).
 */
export async function loadLog({ daysAgo, level, contains, subject, note, container }) {
  const query = new URLSearchParams({ daysAgo, level, lines: '200' });
  if (contains) query.set('contains', contains);
  const about = subject ? ` ${subject}` : '';
  try {
    const log = await api(`/api/log?${query}`);
    if (!log.exists) {
      note.textContent = `Brak pliku dziennika z ${log.day} (agent wtedy nie działał albo plik usunęła retencja).`;
      container.replaceChildren();
      return;
    }
    note.textContent = log.entries.length === 0
      ? (level === 'warn' ? `Brak ostrzeżeń i błędów${about} w dzienniku z ${log.day}.` : `Brak wpisów${about} w dzienniku z ${log.day}.`)
      : `${log.day}: ${log.entries.length} ostatnich wpisów${about}, od najnowszego.`;
    container.replaceChildren(...log.entries.slice().reverse().map((entry) => {
      const box = h('div', `entry level-${entry.level}`);
      box.append(h('div', 'meta', `${entry.time} · ${entry.level}${entry.category ? ` · ${entry.category}` : ''}`));
      box.append(h('div', 'message', entry.message));
      return box;
    }));
  } catch (error) {
    note.textContent = `Nie udało się wczytać dziennika: ${error.message}`;
  }
}
