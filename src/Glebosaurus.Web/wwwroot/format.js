// Wspólne narzędzia stron: składanie elementów, zegar serwera, formaty liczb i czasu, plakietki. Ten sam plik ma lokalny
// panel (src/Glebosaurus.Web/wwwroot) i strona w chmurze (web/) – test pilnuje, że kopie są identyczne, więc zmiany
// rób w obu naraz. Dane trafiają na stronę wyłącznie jako tekst (textContent / append(string)), nigdy jako HTML.

export const $ = (id) => document.getElementById(id);

export const num1 = new Intl.NumberFormat('pl-PL', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
const timeFmt = new Intl.DateTimeFormat('pl-PL', { hour: '2-digit', minute: '2-digit' });
const timeSecFmt = new Intl.DateTimeFormat('pl-PL', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
export const dateFmt = new Intl.DateTimeFormat('pl-PL', { day: 'numeric', month: 'numeric' });

let skewMs = 0;   // zegar serwera (agenta albo bazy) minus zegar przeglądarki

/** Ustawia przesunięcie zegara według czasu serwera (ISO). */
export function syncClock(serverNowIso) {
  skewMs = new Date(serverNowIso) - Date.now();
}

/** „Teraz” według zegara serwera. */
export const now = () => new Date(Date.now() + skewMs);

export function h(tag, cls, ...children) {
  const element = document.createElement(tag);
  if (cls) element.className = cls;
  for (const child of children) {
    if (child === null || child === undefined || child === false) continue;
    element.append(child instanceof Node ? child : String(child));
  }
  return element;
}

function sameDay(a, b) {
  return a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
}

/** „14:05”, „wczoraj 14:05” albo „13.11 14:05”. */
export function when(iso, withSeconds) {
  if (!iso) return '–';
  const date = new Date(iso);
  const time = (withSeconds ? timeSecFmt : timeFmt).format(date);
  const today = now();
  if (sameDay(date, today)) return time;
  const yesterday = new Date(today);
  yesterday.setDate(today.getDate() - 1);
  if (sameDay(date, yesterday)) return `wczoraj ${time}`;
  const tomorrow = new Date(today);
  tomorrow.setDate(today.getDate() + 1);
  if (sameDay(date, tomorrow)) return `jutro ${time}`;
  return `${dateFmt.format(date)} ${time}`;
}

export function ago(iso) {
  if (!iso) return '–';
  const seconds = (now() - new Date(iso)) / 1000;
  if (seconds < 60) return 'przed chwilą';
  const minutes = Math.round(seconds / 60);
  if (minutes < 90) return `${minutes} min temu`;
  const hours = Math.round(minutes / 60);
  if (hours < 48) return `${hours} godz. temu`;
  return `${Math.round(hours / 24)} dni temu`;
}

export function until(iso) {
  if (!iso) return '–';
  const seconds = (new Date(iso) - now()) / 1000;
  if (seconds <= 30) return 'w najbliższym cyklu';
  const minutes = Math.round(seconds / 60);
  if (minutes < 90) return `za ${Math.max(1, minutes)} min (${when(iso)})`;
  return when(iso);
}

export const percent = (value) => (value === null || value === undefined ? '–' : `${value}%`);
export const celsius = (value) => (value === null || value === undefined ? '–' : `${num1.format(value)} °C`);
export const dbm = (value) => `${value} dBm`.replace('-', '−');

const int0 = new Intl.NumberFormat('pl-PL', { maximumFractionDigits: 0 });
/** Światło (Flower Care), np. „82 386 lx”. */
export const lux = (value) => (value === null || value === undefined ? '–' : `${int0.format(value)} lx`);
/** Żyzność – przewodność gleby (Flower Care), np. „1 607 µS/cm”. */
export const microSiemens = (value) => (value === null || value === undefined ? '–' : `${int0.format(value)} µS/cm`);
/** Liczba całkowita z odstępami tysięcy (wartość w karcie, jednostka w etykiecie). */
export const whole = (value) => (value === null || value === undefined ? '–' : int0.format(value));
/** Czujnik Xiaomi Flower Care (model z chmury albo z panelu). */
export const isFlowerCare = (sensor) => Boolean(sensor) && sensor.model === 'flower_care';
/** Temperatura czujnika: Tuya mierzy ją w glebie, Flower Care – w główce nad ziemią. */
export const temperatureLabel = (sensor) => (isFlowerCare(sensor) ? 'Temp. powietrza' : 'Temp. gleby');

export function plural(n, one, few, many) {
  if (n === 1) return one;
  const lastTwo = n % 100;
  const last = n % 10;
  return last >= 2 && last <= 4 && (lastTwo < 12 || lastTwo > 14) ? few : many;
}

export function minutesText(value) {
  return Number.isInteger(value) ? `${value} min` : `${num1.format(value)} min`;
}

export const hoursText = (value) => num1.format(value).replace(',0', '');

export function badge(text, tone) {
  return h('span', `badge tone-${tone}`, text);
}

export function kv(pairs) {
  const list = h('dl', 'kv');
  for (const [key, value] of pairs) {
    if (value === null || value === undefined) continue;
    list.append(h('dt', null, key), h('dd', null, value));
  }
  return list;
}
