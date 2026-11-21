// Wykres wilgotności i temperatury gleby w czasie – czysty SVG, bez bibliotek i bez grafiki rastrowej.
//
// Dwie serie na jednej osi czasu i dwie osie pionowe: wilgotność po lewej, zawsze 0–100%, temperatura po prawej,
// wyskalowana do zakresu wartości z widocznego okna; „okrągłe” podziałki i wspólna siatka. Dziury w danych (gapBefore z API)
// przerywają linię; przy danych uśrednionych za linią leży blady pas min–max. Najechanie, dotknięcie albo strzałki
// pokazują pionowy znacznik i dymek ze wszystkimi wartościami w tym miejscu. Wersja „interactive” (strona czujnika)
// pozwala przesuwać okno przeciąganiem, powiększać Ctrl+kółkiem, gestem dwóch palców i klawiszami +/−.
//
// Kolory i grubości są w app.css (klasy), elementy powstają przez createElementNS, a teksty idą przez textContent –
// strona ma CSP bez stylów i skryptów inline.

import { h, dateFmt, num1 } from './format.js';

const SVG_NS = 'http://www.w3.org/2000/svg';
const HOUR = 3600e3;
const DAY = 24 * HOUR;

/** Oś wilgotności zawsze od 0 do 100% (życzenie użytkownika z 2026-11-15) – wykresy różnych czujników i okresów są porównywalne. */
const MOISTURE_LO = 0;
const MOISTURE_HI = 100;
/** Najmniejsza rozpiętość osi temperatury – przy prawie płaskim przebiegu szum nie powinien wyglądać jak skoki. */
const TEMPERATURE_MIN_SPAN = 2;
const TEMPERATURE_STEPS = [0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50];

const X_STEPS = [
  { unit: 'h', n: 1, ms: HOUR }, { unit: 'h', n: 2, ms: 2 * HOUR }, { unit: 'h', n: 3, ms: 3 * HOUR },
  { unit: 'h', n: 6, ms: 6 * HOUR }, { unit: 'h', n: 12, ms: 12 * HOUR },
  { unit: 'd', n: 1, ms: DAY }, { unit: 'd', n: 2, ms: 2 * DAY }, { unit: 'w', n: 1, ms: 7 * DAY },
  { unit: 'M', n: 1, ms: 30.44 * DAY }, { unit: 'M', n: 3, ms: 91.3 * DAY }, { unit: 'M', n: 6, ms: 182.6 * DAY },
  { unit: 'y', n: 1, ms: 365.25 * DAY }
];

const fixed = [0, 1, 2].map((d) => new Intl.NumberFormat('pl-PL', { minimumFractionDigits: d, maximumFractionDigits: d }));
const hhmm = new Intl.DateTimeFormat('pl-PL', { hour: '2-digit', minute: '2-digit' });
const weekday = new Intl.DateTimeFormat('pl-PL', { weekday: 'short' });
const monthShort = new Intl.DateTimeFormat('pl-PL', { month: 'short' });
const fullDate = new Intl.DateTimeFormat('pl-PL', { day: 'numeric', month: 'numeric', year: 'numeric' });

let chartCount = 0;

// ---- narzędzia ----------------------------------------------------------------------------------------------

function svg(tag, attrs, cls) {
  const element = document.createElementNS(SVG_NS, tag);
  if (attrs) for (const [name, value] of Object.entries(attrs)) element.setAttribute(name, String(value));
  if (cls) element.setAttribute('class', cls);
  return element;
}

function text(x, y, content, cls, anchor) {
  const element = svg('text', { x: round(x), y: round(y), 'text-anchor': anchor || 'middle' }, cls);
  element.textContent = content;
  return element;
}

const round = (v) => Math.round(v * 10) / 10;

/** Pierwszy indeks punktu o czasie ≥ t (punkty posortowane po czasie). */
function lowerBound(points, t) {
  let lo = 0;
  let hi = points.length;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (points[mid].t < t) lo = mid + 1; else hi = mid;
  }
  return lo;
}

/** Pierwszy indeks punktu o czasie > t. */
function upperBound(points, t) {
  let lo = 0;
  let hi = points.length;
  while (lo < hi) {
    const mid = (lo + hi) >> 1;
    if (points[mid].t <= t) lo = mid + 1; else hi = mid;
  }
  return lo;
}

const dayMonth = (d) => dateFmt.format(d);

function decimalsFor(step) {
  if (step >= 1) return 0;
  return step >= 0.1 - 1e-9 ? 1 : 2;
}

/** Ciągłe odcinki serii: przerwa przy braku wartości i przed punktem z gapBefore. */
function runs(points, key) {
  const result = [];
  let run = null;
  for (const p of points) {
    const v = p[key];
    if (v === null || v === undefined || p.gapBefore) {
      if (run) result.push(run);
      run = null;
    }
    if (v === null || v === undefined) continue;
    if (!run) run = [];
    run.push(p);
  }
  if (run) result.push(run);
  return result;
}

// ---- skale --------------------------------------------------------------------------------------------------

/**
 * Oś o n przedziałach z „okrągłym” krokiem, obejmująca [min, max] rozszerzone do minSpan. „waste” – ile razy oś jest
 * szersza od danych (mniej = lepiej wykorzystana wysokość).
 */
function fitAxis(min, max, n, steps, minSpan) {
  if (max - min < minSpan) {
    const center = (min + max) / 2;
    min = center - minSpan / 2;
    max = center + minSpan / 2;
  }
  for (const step of steps) {
    if (step * n < max - min - 1e-9) continue;
    const lo = Math.floor(min / step + 1e-9) * step;
    const hi = lo + n * step;
    if (hi >= max - 1e-9) return { lo, hi, step, n, waste: (hi - lo) / (max - min) };
  }
  // Żaden okrągły krok nie pasuje przy tej liczbie przedziałów – podziałki byłyby „krzywe”, więc to wybór ostateczny.
  const step = (max - min) / n;
  return { lo: min, hi: max, step, n, waste: 100 };
}

/**
 * Wspólna liczba przedziałów dla obu osi (jedna siatka). Oś wilgotności jest stała (0–100%), więc przy niej w grę wchodzą
 * tylko liczby przedziałów o całkowitym kroku (4 → co 25, 5 → co 20); spośród nich wygrywa ta, przy której temperatura
 * najlepiej wypełnia wysokość.
 */
function chooseScales(moisture, temperature, counts) {
  const span = MOISTURE_HI - MOISTURE_LO;
  const allowed = moisture ? counts.filter((n) => span % n === 0) : counts;
  let best = null;
  for (const n of allowed.length ? allowed : [4]) {
    const m = moisture ? { lo: MOISTURE_LO, hi: MOISTURE_HI, step: span / n, n } : null;
    const t = temperature ? fitAxis(temperature.min, temperature.max, n, TEMPERATURE_STEPS, TEMPERATURE_MIN_SPAN) : null;
    const score = (t ? t.waste : 0) - (n === 4 ? 0.05 : 0);
    if (!best || score < best.score) best = { score, n, m, t };
  }
  return best;
}

function extent(points, key, minKey, maxKey, useBand) {
  let min = Infinity;
  let max = -Infinity;
  for (const p of points) {
    if (p[key] === null || p[key] === undefined) continue;
    const lo = useBand && p[minKey] !== null && p[minKey] !== undefined ? p[minKey] : p[key];
    const hi = useBand && p[maxKey] !== null && p[maxKey] !== undefined ? p[maxKey] : p[key];
    if (lo < min) min = lo;
    if (hi > max) max = hi;
  }
  return min === Infinity ? null : { min, max };
}

// ---- oś czasu -----------------------------------------------------------------------------------------------

function timeTicks(from, to, width, minPx) {
  const span = to - from;
  const step = X_STEPS.find((s) => (width * s.ms) / span >= minPx) || X_STEPS[X_STEPS.length - 1];
  const d = new Date(from);
  if (step.unit === 'h') {
    d.setMinutes(0, 0, 0);
    d.setHours(Math.floor(d.getHours() / step.n) * step.n);
  } else {
    d.setHours(0, 0, 0, 0);
    if (step.unit === 'w') d.setDate(d.getDate() - ((d.getDay() + 6) % 7));
    if (step.unit === 'M') { d.setDate(1); d.setMonth(Math.floor(d.getMonth() / step.n) * step.n); }
    if (step.unit === 'y') d.setMonth(0, 1);
  }
  const ticks = [];
  for (let guard = 0; d.getTime() <= to && guard < 400; guard++) {
    if (d.getTime() >= from) ticks.push(new Date(d));
    if (step.unit === 'h') d.setHours(d.getHours() + step.n);
    else if (step.unit === 'd') d.setDate(d.getDate() + step.n);
    else if (step.unit === 'w') d.setDate(d.getDate() + 7);
    else if (step.unit === 'M') d.setMonth(d.getMonth() + step.n);
    else d.setFullYear(d.getFullYear() + 1);
  }
  return { step, ticks, pxPerTick: (width * step.ms) / span };
}

function tickLabel(date, step, wide) {
  if (step.unit === 'h') return date.getHours() === 0 && date.getMinutes() === 0 ? dayMonth(date) : hhmm.format(date);
  if (step.unit === 'd' || step.unit === 'w') return wide ? `${weekday.format(date)} ${dayMonth(date)}` : dayMonth(date);
  if (step.unit === 'M') return date.getMonth() === 0 ? String(date.getFullYear()) : monthShort.format(date);
  return String(date.getFullYear());
}

/** Numer dnia od 0001-01-01 (poniedziałek) dla daty lokalnej – jak takty czasu lokalnego po stronie agenta. */
function dayNumber(date) {
  return 719162 + Math.round(Date.UTC(date.getFullYear(), date.getMonth(), date.getDate()) / DAY);
}

function fromDayNumber(n) {
  const utc = new Date((n - 719162) * DAY);
  return new Date(utc.getUTCFullYear(), utc.getUTCMonth(), utc.getUTCDate());
}

function minutesLabel(minutes) {
  if (minutes < 60) return `${minutes} min`;
  if (minutes < 1440) return `${minutes / 60} godz.`;
  const days = minutes / 1440;
  return days === 1 ? 'doby' : days % 7 === 0 ? `${days / 7} tyg.` : `${days} dni`;
}

/** Opis przedziału uśredniania, w którym leży punkt (granice jak po stronie agenta: od północy, tygodnie od poniedziałku). */
function bucketText(t, minutes) {
  const date = new Date(t);
  if (minutes < 1440) {
    const start = new Date(date);
    const ofDay = date.getHours() * 60 + date.getMinutes();
    start.setHours(0, Math.floor(ofDay / minutes) * minutes, 0, 0);
    const end = new Date(start.getTime() + minutes * 60e3);
    return `${weekday.format(start)} ${dayMonth(start)}, ${hhmm.format(start)}–${hhmm.format(end)}`;
  }
  const days = minutes / 1440;
  const n = dayNumber(date);
  const first = fromDayNumber(n - (n % days));
  if (days === 1) return `${weekday.format(first)} ${fullDate.format(first)}`;
  const last = fromDayNumber(n - (n % days) + days - 1);
  return `${dayMonth(first)}–${fullDate.format(last)}`;
}

export function resolutionText(bucketMinutes) {
  return bucketMinutes > 0 ? `średnie z ${minutesLabel(bucketMinutes)}` : 'pojedyncze odczyty';
}

// ---- wykres -------------------------------------------------------------------------------------------------

/**
 * Wykres czasowy. Użycie: new TimeChart({ compact | interactive, onView }), wstawić .element do strony, potem setView(od, do)
 * i setData(odpowiedź /api/sensors/{id}/series). W trybie interactive gesty wołają onView({ from, to }, settled) –
 * właściciel przycina okno, woła setView i po zakończeniu gestu (settled) dociąga dane w nowej rozdzielczości.
 */
export class TimeChart {
  constructor(options = {}) {
    this.compact = Boolean(options.compact);
    this.interactive = Boolean(options.interactive);
    this.onView = options.onView || null;
    this.emptyText = options.emptyText || 'Brak odczytów w tym okresie.';
    this.id = ++chartCount;
    this.points = [];
    this.bucketMinutes = 0;
    this.view = null;
    this.threshold = null;
    this.hoverT = null;
    this.geom = null;
    this.pointers = new Map();
    this.gesture = null;
    this.wheelTimer = 0;

    this.element = h('div', `chart${this.compact ? ' compact' : ''}${this.interactive ? ' interactive' : ''}`);
    this.legend = h('div', 'chart-legend');
    this.plot = h('div', 'plot');
    this.plot.tabIndex = 0;
    this.svg = svg('svg', { role: 'img', focusable: 'false' });
    this.tooltip = h('div', 'tooltip');
    this.tooltip.hidden = true;
    this.plot.append(this.svg, this.tooltip);
    this.element.append(this.legend, this.plot);

    this.renderedWidth = 0;
    this.resizeObserver = new ResizeObserver(() => {
      if (this.plot.clientWidth !== this.renderedWidth) this.render();
    });
    this.resizeObserver.observe(this.plot);
    this.attachEvents();
  }

  get height() {
    if (this.compact) return 140;
    return this.plot.clientWidth < 520 ? 240 : 300;
  }

  margins() {
    return this.compact ? { top: 20, right: 36, bottom: 20, left: 34 } : { top: 22, right: 44, bottom: 24, left: 42 };
  }

  /** Szerokość obszaru danych w pikselach – do doboru liczby punktów przy pobieraniu. */
  get plotWidth() {
    const m = this.margins();
    return Math.max(60, (this.plot.clientWidth || 320) - m.left - m.right);
  }

  setView(from, to) {
    this.view = { from, to };
    this.render();
  }

  setData(data) {
    this.points = (data && data.points ? data.points : []).map((p) => ({ ...p, t: Date.parse(p.at) }));
    this.bucketMinutes = data ? data.bucketMinutes : 0;
    this.render();
  }

  /** Próg podlania czujnika (linia na osi wilgotności, gdy mieści się w jej zakresie). */
  setThreshold(percentValue) {
    this.threshold = percentValue;
  }

  /** Dane się wczytują – poprzedni obraz zostaje, przygaszony. */
  setLoading(loading) {
    this.element.classList.toggle('loading', loading);
  }

  // ---- rysowanie ----

  render() {
    const width = this.plot.clientWidth;
    this.renderedWidth = width;
    if (!width || !this.view) return;
    const height = this.height;
    const m = this.margins();
    const pw = Math.max(10, width - m.left - m.right);
    const ph = height - m.top - m.bottom;
    const { from, to } = this.view;
    const x = (t) => m.left + ((t - from) / (to - from)) * pw;

    const points = this.points;
    const i0 = lowerBound(points, from);
    const i1 = upperBound(points, to);
    const visible = points.slice(i0, i1);
    // Po jednym punkcie spoza okna z każdej strony – linia dochodzi do krawędzi zamiast urywać się przed nią.
    const drawn = points.slice(Math.max(0, i0 - 1), Math.min(points.length, i1 + 1));
    const banded = this.bucketMinutes > 0;
    const moistureRange = extent(visible, 'moisture', 'moistureMin', 'moistureMax', banded);
    const temperatureRange = extent(visible, 'temperature', 'temperatureMin', 'temperatureMax', banded);
    const scales = moistureRange || temperatureRange
      ? chooseScales(moistureRange, temperatureRange, this.compact ? [3, 4] : [3, 4, 5])
      : null;
    const yScale = (axis) => (axis ? (v) => m.top + ((axis.hi - v) / (axis.hi - axis.lo)) * ph : null);
    const yM = scales ? yScale(scales.m) : null;
    const yT = scales ? yScale(scales.t) : null;

    const root = this.svg;
    root.replaceChildren();
    root.setAttribute('width', width);
    root.setAttribute('height', height);
    root.setAttribute('viewBox', `0 0 ${width} ${height}`);

    const clipId = `chart-clip-${this.id}`;
    const clip = svg('clipPath', { id: clipId });
    clip.append(svg('rect', { x: m.left, y: m.top - 3, width: pw, height: ph + 6 }));
    const defs = svg('defs');
    defs.append(clip);
    root.append(defs);

    // Siatka pozioma (wspólna dla obu osi) i podziałki osi.
    const grid = svg('g', null, 'grid');
    const labels = svg('g', null, 'ticks');
    const n = scales ? scales.n : 4;
    for (let k = 0; k <= n; k++) {
      const y = m.top + (k / n) * ph;
      grid.append(svg('line', { x1: m.left, x2: m.left + pw, y1: round(y) + 0.5, y2: round(y) + 0.5 }));
      if (scales && scales.m) {
        const value = scales.m.hi - k * scales.m.step;
        labels.append(text(m.left - 6, y + 4, fixed[0].format(value), 'tick', 'end'));
      }
      if (scales && scales.t) {
        const value = scales.t.hi - k * scales.t.step;
        labels.append(text(m.left + pw + 6, y + 4, fixed[decimalsFor(scales.t.step)].format(value), 'tick', 'start'));
      }
    }

    // Oś czasu: pionowe linie siatki i podpisy (dni od lewej krawędzi swojej linii, godziny na środku).
    const { step, ticks, pxPerTick } = timeTicks(from, to, pw, this.compact ? 56 : 72);
    const wide = pxPerTick >= 78;
    for (const tick of ticks) {
      const tx = x(tick.getTime());
      grid.append(svg('line', { x1: Math.round(tx) + 0.5, x2: Math.round(tx) + 0.5, y1: m.top, y2: m.top + ph }));
      const label = tickLabel(tick, step, wide);
      const startAnchored = step.unit !== 'h';
      const estimated = label.length * 6.3;
      const lx = startAnchored ? tx + 3 : tx;
      if (startAnchored ? lx + estimated > width - 2 : lx - estimated / 2 < 2 || lx + estimated / 2 > width - 2) continue;
      labels.append(text(lx, height - 6, label, 'tick', startAnchored ? 'start' : 'middle'));
    }
    root.append(grid);

    // Jednostki osi z kluczem serii (tekst w kolorze tekstu – serię rozpoznaje kreska obok).
    if (scales && scales.m) {
      root.append(svg('line', { x1: 2, x2: 14, y1: 8, y2: 8 }, 'key-line moisture'));
      root.append(text(18, 12, '%', 'tick unit', 'start'));
    }
    if (scales && scales.t) {
      root.append(svg('line', { x1: width - 14, x2: width - 2, y1: 8, y2: 8 }, 'key-line temperature'));
      root.append(text(width - 18, 12, '°C', 'tick unit', 'end'));
    }
    root.append(labels);

    const data = svg('g', { 'clip-path': `url(#${clipId})` });
    let thresholdVisible = false;
    if (yM && this.threshold !== null && this.threshold !== undefined && this.threshold >= scales.m.lo && this.threshold <= scales.m.hi) {
      thresholdVisible = true;
      const ty = round(yM(this.threshold)) + 0.5;
      data.append(svg('line', { x1: m.left, x2: m.left + pw, y1: ty, y2: ty }, 'threshold'));
    }
    if (yT) this.drawSeries(data, drawn, 'temperature', x, yT, banded);
    if (yM) this.drawSeries(data, drawn, 'moisture', x, yM, banded);
    root.append(data);

    // Kropka na końcu linii – tylko gdy w oknie jest najnowszy punkt serii.
    for (const [key, y] of [['temperature', yT], ['moisture', yM]]) {
      if (!y) continue;
      let last = points.length - 1;
      while (last >= 0 && (points[last][key] === null || points[last][key] === undefined)) last--;
      if (last >= i0 && last < i1) root.append(svg('circle', { cx: round(x(points[last].t)), cy: round(y(points[last][key])), r: 4 }, `dot ${key}`));
    }

    if (!scales) root.append(text(m.left + pw / 2, m.top + ph / 2 + 4, this.emptyText, 'empty-text'));

    this.hoverGroup = svg('g', null, 'hover');
    root.append(this.hoverGroup);
    this.geom = { x, yM, yT, m, pw, ph, from, to, i0, i1, width };
    this.renderLegend(thresholdVisible);
    root.setAttribute('aria-label', this.summary(moistureRange, temperatureRange));
    if (this.hoverT !== null) this.showHover(this.hoverT);
  }

  drawSeries(group, points, key, x, y, banded) {
    for (const run of runs(points, key)) {
      if (banded) {
        const top = run.map((p) => `${round(x(p.t))},${round(y(p[`${key}Max`] ?? p[key]))}`);
        const bottom = run.slice().reverse().map((p) => `${round(x(p.t))},${round(y(p[`${key}Min`] ?? p[key]))}`);
        if (run.length > 1) group.append(svg('path', { d: `M${top.join('L')}L${bottom.join('L')}Z` }, `band ${key}`));
      }
      if (run.length === 1) {
        group.append(svg('circle', { cx: round(x(run[0].t)), cy: round(y(run[0][key])), r: 2.5 }, `point ${key}`));
      } else {
        group.append(svg('path', { d: `M${run.map((p) => `${round(x(p.t))},${round(y(p[key]))}`).join('L')}` }, `line ${key}`));
      }
    }
  }

  renderLegend(thresholdVisible) {
    const item = (cls, label) => h('span', 'legend-item', h('span', `key ${cls}`), label);
    const items = [
      item('moisture', this.compact ? 'wilgotność, %' : 'Wilgotność gleby, % (lewa oś)'),
      item('temperature', this.compact ? 'temperatura, °C' : 'Temperatura gleby, °C (prawa oś)')
    ];
    if (thresholdVisible) items.push(item('threshold', this.compact ? 'próg podlania' : `Próg podlania (${this.threshold}%)`));
    this.legend.replaceChildren(...items);
  }

  summary(moistureRange, temperatureRange) {
    const parts = ['Wykres wilgotności i temperatury gleby'];
    if (moistureRange) parts.push(`wilgotność ${fixed[0].format(moistureRange.min)}–${fixed[0].format(moistureRange.max)}%`);
    if (temperatureRange) parts.push(`temperatura ${num1.format(temperatureRange.min)}–${num1.format(temperatureRange.max)} °C`);
    if (!moistureRange && !temperatureRange) parts.push(this.emptyText);
    return parts.join(', ');
  }

  // ---- znacznik i dymek ----

  timeAt(clientX) {
    const g = this.geom;
    const left = this.plot.getBoundingClientRect().left;
    return g.from + ((clientX - left - g.m.left) / g.pw) * (g.to - g.from);
  }

  /** Punkt w oknie najbliższy chwili t; null, gdy okno jest puste. */
  nearestIndex(t) {
    const g = this.geom;
    if (!g || g.i1 <= g.i0) return null;
    const i = Math.min(Math.max(lowerBound(this.points, t), g.i0), g.i1 - 1);
    if (i > g.i0 && Math.abs(this.points[i - 1].t - t) <= Math.abs(this.points[i].t - t)) return i - 1;
    return i;
  }

  showHover(t) {
    const g = this.geom;
    const index = this.nearestIndex(t);
    if (index === null) {
      this.hideHover();
      return;
    }
    const p = this.points[index];
    const px = g.x(p.t);
    // Wskaźnik daleko od danych (np. nad dziurą) – nie przypisujemy mu odległego punktu.
    if (Math.abs(px - g.x(t)) > Math.max(24, g.pw / 12)) {
      this.hideHover(true);
      return;
    }
    this.hoverT = t;
    const top = g.m.top;
    const bottom = g.m.top + g.ph;
    const nodes = [svg('line', { x1: round(px), x2: round(px), y1: top, y2: bottom }, 'crosshair')];
    if (g.yT && p.temperature !== null && p.temperature !== undefined) nodes.push(svg('circle', { cx: round(px), cy: round(g.yT(p.temperature)), r: 4 }, 'dot temperature'));
    if (g.yM && p.moisture !== null && p.moisture !== undefined) nodes.push(svg('circle', { cx: round(px), cy: round(g.yM(p.moisture)), r: 4 }, 'dot moisture'));
    this.hoverGroup.replaceChildren(...nodes);

    const averaged = this.bucketMinutes > 0 && p.count > 1;
    // Wartość pogrubiona, nazwa serii po niej; przy średniej w nawiasie najmniejsza i największa wartość z przedziału.
    const row = (cls, value, low, high, digits, unit, name) => {
      if (value === null || value === undefined) return h('div', 'tip-row', h('span', `key ${cls}`), h('strong', null, '–'), ` ${name}`);
      const format = (v) => fixed[digits(v)].format(v);
      const range = averaged && low !== high ? ` (${format(low)}–${format(high)})` : '';
      return h('div', 'tip-row', h('span', `key ${cls}`), h('strong', null, `${format(value)}${unit}`), `${range} ${name}`);
    };
    this.tooltip.replaceChildren(
      row('moisture', p.moisture, p.moistureMin, p.moistureMax, (v) => (Number.isInteger(v) ? 0 : 1), '%', 'wilgotność'),
      row('temperature', p.temperature, p.temperatureMin, p.temperatureMax, () => 1, ' °C', 'temperatura'),
      h('div', 'tip-time', averaged
        ? `${bucketText(p.t, this.bucketMinutes)} · średnia z ${p.count} ${p.count === 1 ? 'odczytu' : 'odczytów'}`
        : `${weekday.format(new Date(p.t))} ${dayMonth(new Date(p.t))}, ${hhmm.format(new Date(p.t))}`));
    this.tooltip.hidden = false;
    const tipWidth = this.tooltip.offsetWidth;
    let left = px + 12;
    if (left + tipWidth > g.width - 2) left = px - 12 - tipWidth;
    this.tooltip.style.left = `${Math.max(2, left)}px`;
    this.tooltip.style.top = `${top}px`;
  }

  /** keepPosition – chowa sam obraz, ale pamięta pozycję (po odświeżeniu danych znacznik wróci, jeśli będzie przy czym). */
  hideHover(keepPosition) {
    if (!keepPosition) this.hoverT = null;
    if (this.hoverGroup) this.hoverGroup.replaceChildren();
    this.tooltip.hidden = true;
  }

  /** Znacznik o jeden punkt w lewo/prawo (klawiatura). */
  stepHover(direction) {
    const g = this.geom;
    if (!g || g.i1 <= g.i0) return;
    let index = this.hoverT === null ? null : this.nearestIndex(this.hoverT);
    index = index === null ? g.i1 - 1 : Math.min(g.i1 - 1, Math.max(g.i0, index + direction));
    this.showHover(this.points[index].t);
  }

  // ---- gesty ----

  requestView(from, to, settled) {
    if (this.onView) this.onView({ from, to }, settled);
  }

  zoom(factor, center) {
    const { from, to } = this.view;
    const c = center ?? (from + to) / 2;
    this.requestView(c - (c - from) * factor, c + (to - c) * factor, true);
  }

  pan(fraction) {
    const { from, to } = this.view;
    const shift = (to - from) * fraction;
    this.requestView(from + shift, to + shift, true);
  }

  attachEvents() {
    const plot = this.plot;
    plot.addEventListener('pointerdown', (e) => this.onPointerDown(e));
    plot.addEventListener('pointermove', (e) => this.onPointerMove(e));
    plot.addEventListener('pointerup', (e) => this.onPointerUp(e));
    plot.addEventListener('pointercancel', (e) => this.onPointerUp(e, true));
    plot.addEventListener('pointerleave', (e) => {
      if (e.pointerType === 'mouse' && !this.pointers.size) this.hideHover();
    });
    plot.addEventListener('blur', () => this.hideHover());
    plot.addEventListener('keydown', (e) => this.onKey(e));
    if (this.interactive) {
      plot.addEventListener('wheel', (e) => {
        if (!(e.ctrlKey || e.metaKey) || !this.geom) return;
        e.preventDefault();
        const t = this.timeAt(e.clientX);
        const factor = Math.exp(Math.max(-60, Math.min(60, e.deltaY)) * 0.004);
        const { from, to } = this.view;
        this.requestView(t - (t - from) * factor, t + (to - t) * factor, false);
        window.clearTimeout(this.wheelTimer);
        this.wheelTimer = window.setTimeout(() => this.requestView(this.view.from, this.view.to, true), 300);
      }, { passive: false });
    }
  }

  onPointerDown(e) {
    if (!this.geom || (e.pointerType === 'mouse' && e.button !== 0)) return;
    plotFocus(this.plot);
    if (!this.interactive) {
      this.showHover(this.timeAt(e.clientX));
      return;
    }
    this.plot.setPointerCapture(e.pointerId);
    this.pointers.set(e.pointerId, e.clientX);
    if (this.pointers.size === 1) {
      this.gesture = { kind: 'pan', x0: e.clientX, view0: { ...this.view }, moved: false };
    } else if (this.pointers.size === 2) {
      const [a, b] = [...this.pointers.values()];
      this.gesture = { kind: 'pinch', d0: Math.max(10, Math.abs(a - b)), view0: { ...this.view }, center: this.timeAt((a + b) / 2) };
      this.hideHover();
    }
  }

  onPointerMove(e) {
    if (this.pointers.has(e.pointerId)) {
      this.pointers.set(e.pointerId, e.clientX);
      const g = this.gesture;
      if (g && g.kind === 'pan') {
        const dx = e.clientX - g.x0;
        if (!g.moved && Math.abs(dx) < 5) return;
        g.moved = true;
        this.plot.classList.add('dragging');
        this.hideHover();
        const shift = (dx / this.geom.pw) * (g.view0.to - g.view0.from);
        this.requestView(g.view0.from - shift, g.view0.to - shift, false);
      } else if (g && g.kind === 'pinch' && this.pointers.size === 2) {
        const [a, b] = [...this.pointers.values()];
        const span0 = g.view0.to - g.view0.from;
        const span = span0 * (g.d0 / Math.max(10, Math.abs(a - b)));
        const share = (g.center - g.view0.from) / span0;
        this.requestView(g.center - share * span, g.center + (1 - share) * span, false);
      }
      return;
    }
    if (this.geom && (e.pointerType === 'mouse' || (!this.interactive && e.pressure > 0))) this.showHover(this.timeAt(e.clientX));
  }

  onPointerUp(e, cancelled) {
    if (!this.pointers.has(e.pointerId)) return;
    this.pointers.delete(e.pointerId);
    const g = this.gesture;
    if (this.pointers.size === 0) {
      this.plot.classList.remove('dragging');
      this.gesture = null;
      if (g && (g.kind === 'pinch' || g.moved)) this.requestView(this.view.from, this.view.to, true);
      else if (!cancelled) this.showHover(this.timeAt(e.clientX));      // stuknięcie pokazuje dymek
    } else if (this.pointers.size === 1) {
      const [remaining] = [...this.pointers.values()];
      this.gesture = { kind: 'pan', x0: remaining, view0: { ...this.view }, moved: true };
    }
  }

  onKey(e) {
    if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') {
      const direction = e.key === 'ArrowLeft' ? -1 : 1;
      if (this.interactive && e.shiftKey) this.pan(direction * 0.25);
      else this.stepHover(direction);
    } else if (e.key === 'Escape') {
      this.hideHover();
    } else if (this.interactive && (e.key === '+' || e.key === '=')) {
      this.zoom(0.5);
    } else if (this.interactive && (e.key === '-' || e.key === '_')) {
      this.zoom(2);
    } else {
      return;
    }
    e.preventDefault();
  }
}

function plotFocus(element) {
  if (document.activeElement !== element) element.focus({ preventScroll: true });
}

// ---- pasek przeglądu ----------------------------------------------------------------------------------------

/**
 * Cała historia w miniaturze z prostokątem widocznego okna. Przeciąganie prostokąta przesuwa okno, stuknięcie obok
 * przenosi je w to miejsce. Woła onView({ from, to }, settled) jak TimeChart.
 */
export class OverviewStrip {
  constructor(options = {}) {
    this.onView = options.onView || null;
    this.points = [];
    this.range = null;
    this.view = null;
    this.drag = null;
    this.element = h('div', 'overview');
    this.svg = svg('svg', { 'aria-hidden': 'true', focusable: 'false' });
    this.element.append(this.svg);
    this.renderedWidth = 0;
    new ResizeObserver(() => {
      if (this.element.clientWidth !== this.renderedWidth) this.render();
    }).observe(this.element);
    this.svg.addEventListener('pointerdown', (e) => this.onPointerDown(e));
    this.svg.addEventListener('pointermove', (e) => this.onPointerMove(e));
    this.svg.addEventListener('pointerup', (e) => this.onPointerUp(e));
    this.svg.addEventListener('pointercancel', (e) => this.onPointerUp(e));
  }

  /** Pełna historia (od, do) i jej punkty (zwykle uśrednione). */
  setData(range, data) {
    this.range = range;
    this.points = (data && data.points ? data.points : []).map((p) => ({ ...p, t: Date.parse(p.at) }));
    this.render();
  }

  setView(from, to) {
    this.view = { from, to };
    this.drawWindow();
  }

  render() {
    const width = this.element.clientWidth;
    this.renderedWidth = width;
    const root = this.svg;
    root.replaceChildren();
    if (!width || !this.range) return;
    const height = 44;
    root.setAttribute('width', width);
    root.setAttribute('height', height);
    root.setAttribute('viewBox', `0 0 ${width} ${height}`);
    const { from, to } = this.range;
    const x = (t) => 1 + ((t - from) / Math.max(1, to - from)) * (width - 2);
    root.append(svg('rect', { x: 0.5, y: 0.5, width: width - 1, height: height - 1 }, 'frame'));
    for (const key of ['temperature', 'moisture']) {
      const range = extent(this.points, key);
      if (!range) continue;
      // Wilgotność jak na wykresie: zawsze 0–100%; temperatura od najniższej do najwyższej, rozpiętość co najmniej 2 °C.
      const moisture = key === 'moisture';
      const lo = moisture ? MOISTURE_LO : Math.min(range.min, (range.min + range.max - TEMPERATURE_MIN_SPAN) / 2);
      const hi = moisture ? MOISTURE_HI : Math.max(range.max, (range.min + range.max + TEMPERATURE_MIN_SPAN) / 2);
      const y = (v) => 4 + ((hi - v) / (hi - lo)) * (height - 8);
      for (const run of runs(this.points, key)) {
        if (run.length < 2) continue;
        root.append(svg('path', { d: `M${run.map((p) => `${round(x(p.t))},${round(y(p[key]))}`).join('L')}` }, `line thin ${key}`));
      }
    }
    this.x = x;
    this.shadeLeft = svg('rect', { y: 1, height: height - 2 }, 'shade');
    this.shadeRight = svg('rect', { y: 1, height: height - 2 }, 'shade');
    this.window = svg('rect', { y: 1, height: height - 2 }, 'window');
    root.append(this.shadeLeft, this.shadeRight, this.window);
    this.drawWindow();
  }

  drawWindow() {
    if (!this.window || !this.view || !this.range) return;
    const width = this.renderedWidth;
    const left = Math.max(1, Math.min(width - 1, this.x(this.view.from)));
    const right = Math.max(left + 3, Math.min(width - 1, this.x(this.view.to)));
    this.shadeLeft.setAttribute('x', 1);
    this.shadeLeft.setAttribute('width', Math.max(0, left - 1));
    this.shadeRight.setAttribute('x', right);
    this.shadeRight.setAttribute('width', Math.max(0, width - 1 - right));
    this.window.setAttribute('x', left);
    this.window.setAttribute('width', right - left);
  }

  timeAt(clientX) {
    const rect = this.svg.getBoundingClientRect();
    const { from, to } = this.range;
    return from + ((clientX - rect.left - 1) / Math.max(1, rect.width - 2)) * (to - from);
  }

  onPointerDown(e) {
    if (!this.range || !this.view || (e.pointerType === 'mouse' && e.button !== 0)) return;
    this.svg.setPointerCapture(e.pointerId);
    const t = this.timeAt(e.clientX);
    const span = this.view.to - this.view.from;
    if (t < this.view.from || t > this.view.to) {
      // Stuknięcie obok okna – przenosimy je tak, by jego środek wypadł w tym miejscu.
      this.emit(t - span / 2, t + span / 2, false);
    }
    this.drag = { x0: e.clientX, view0: { ...this.view } };
    this.svg.classList.add('dragging');
  }

  onPointerMove(e) {
    if (!this.drag) return;
    const rect = this.svg.getBoundingClientRect();
    const shift = ((e.clientX - this.drag.x0) / Math.max(1, rect.width - 2)) * (this.range.to - this.range.from);
    this.emit(this.drag.view0.from + shift, this.drag.view0.to + shift, false);
  }

  onPointerUp() {
    if (!this.drag) return;
    this.drag = null;
    this.svg.classList.remove('dragging');
    this.emit(this.view.from, this.view.to, true);
  }

  emit(from, to, settled) {
    if (this.onView) this.onView({ from, to }, settled);
  }
}
