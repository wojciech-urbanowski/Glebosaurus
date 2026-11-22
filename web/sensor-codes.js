// Krótkie kody czujników do kafelków pulpitu skróconego (telefon): trzy znaki liczone w przeglądarce z nazw, bez pola w
// bazie. Wielka litera = początek słowa nazwy (Laurowiśnia wejście → LWe, Zioła → Zio, Pomidor 2 → Po2). Kody są różne
// bez względu na wielkość liter i polskie znaki; nowy czujnik albo zmiana nazwy przestawia kod tylko czujnikom o tym samym
// kodzie podstawowym. Reguły pilnuje tests/Glebosaurus.Tests/SensorCodesTests.cs (ten plik w silniku Jint).

const LENGTH = 3;
const MAX_SUFFIX = 2;      // liczba na końcu nazwy (Pomidor 2, Pomidor 12) zostaje na końcu kodu

/** Znaki nazwy (litery i cyfry) z zaznaczonym początkiem słowa i podziałem na słowa. */
function parse(name) {
  const chars = [];
  const words = [];
  for (const word of String(name).normalize('NFC').match(/[\p{L}\p{N}]+/gu) || []) {
    const start = chars.length;
    for (const char of word) chars.push({ char, initial: chars.length === start });
    words.push({ start, length: chars.length - start, digits: /^\p{N}+$/u.test(word) });
  }
  return { chars, words, primary: primaryPicks(chars, words) };
}

function range(start, count) {
  return Array.from({ length: Math.max(0, count) }, (_, i) => start + i);
}

/**
 * Kod podstawowy (pozycje znaków): jedno słowo – trzy pierwsze znaki; dwa słowa – pierwszy znak pierwszego i dwa pierwsze
 * drugiego; więcej – inicjały. Liczba (1–2 cyfry) na końcu nazwy zostaje na końcu kodu. Krótkim słowom brakujące znaki
 * dobiera najwcześniejsze nieużyte znaki nazwy.
 */
function primaryPicks(chars, words) {
  if (words.length === 0) return [];
  const last = words[words.length - 1];
  const suffix = words.length > 1 && last.digits && last.length <= MAX_SUFFIX ? last : null;
  const base = suffix ? words.slice(0, -1) : words;
  const want = LENGTH - (suffix ? suffix.length : 0);
  let picks;
  if (base.length === 1) picks = range(base[0].start, Math.min(want, base[0].length));
  else if (base.length === 2) picks = [base[0].start, ...range(base[1].start, Math.min(want - 1, base[1].length))];
  else picks = base.slice(0, want).map((word) => word.start);
  const baseEnd = suffix ? suffix.start : chars.length;
  for (let i = 0; picks.length < want && i < baseEnd; i++) {
    if (!picks.includes(i)) picks.push(i);
  }
  picks.sort((a, b) => a - b);
  if (suffix) picks.push(...range(suffix.start, suffix.length));
  return picks;
}

function display(parsed, picks) {
  return picks.map((i) => parsed.chars[i]).map((c) => (c.initial ? c.char.toUpperCase() : c.char.toLowerCase())).join('');
}

/** Postać do porównań: bez wielkości liter i znaków diakrytycznych (Ś = S, Ł = L). */
function fold(code) {
  return code.normalize('NFD').replace(/\p{M}/gu, '').replace(/[łŁ]/g, 'l').toLowerCase();
}

/** Pierwszy wolny kod: podstawowy, potem pierwszy znak i dwa dalsze w kolejności, na końcu dwa pierwsze znaki i liczba. */
function firstFree(parsed, taken) {
  const free = (code) => !taken.has(fold(code));
  const primary = display(parsed, parsed.primary);
  if (free(primary)) return primary;
  const count = parsed.chars.length;
  for (let j = 1; j < count; j++) {
    for (let k = j + 1; k < count; k++) {
      const code = display(parsed, [0, j, k]);
      if (free(code)) return code;
    }
  }
  const head = display(parsed, range(0, Math.min(2, count)));
  for (let n = 2; ; n++) {
    if (free(head + n)) return head + n;
  }
}

/**
 * Kody wszystkich czujników z listy ({ id, name }; wyłączone też, żeby ich włączenie nic nie przestawiało) → Map id → kod.
 * Czujnik z niepowtarzalnym kodem podstawowym dostaje go zawsze. Przy powtórce kod podstawowy bierze czujnik o
 * najmniejszym identyfikatorze, pozostali – pierwszy wolny kod zapasowy (też według identyfikatora).
 */
export function sensorCodes(sensors) {
  const entries = sensors.map((sensor) => {
    let parsed = parse(sensor.name);
    if (parsed.chars.length === 0) parsed = parse(sensor.id);      // nazwa bez liter i cyfr – kod z identyfikatora
    const primary = display(parsed, parsed.primary);
    return { id: sensor.id, parsed, primary, key: fold(primary) };
  });
  const counts = new Map();
  for (const entry of entries) counts.set(entry.key, (counts.get(entry.key) || 0) + 1);

  const codes = new Map();
  const taken = new Set();
  const assign = (entry, code) => {
    codes.set(entry.id, code);
    taken.add(fold(code));
  };
  for (const entry of entries) {
    if (counts.get(entry.key) === 1) assign(entry, entry.primary);
  }
  const rest = entries.filter((entry) => !codes.has(entry.id)).sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
  for (const entry of rest) {
    if (!taken.has(entry.key)) assign(entry, entry.primary);
  }
  for (const entry of rest) {
    if (!codes.has(entry.id)) assign(entry, firstFree(entry.parsed, taken));
  }
  return codes;
}
