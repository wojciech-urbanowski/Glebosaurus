// Formularze i drobne elementy stron w chmurze: pola z etykietą, formularz z błędem i blokadą przycisku, potwierdzenie
// akcji, notki. Wszystko przez textContent/append – bez wstawiania HTML.

import { h } from './format.js';

let fieldCounter = 0;

export function errorBox(text) {
  const box = h('div', 'action-note tone-red', text);
  box.setAttribute('role', 'alert');
  box.hidden = !text;
  return box;
}

/** Notka pod formularzem (ton: green, amber, red, grey); pusty tekst – chowa. */
export function note(box, text, tone = 'grey') {
  box.className = `action-note tone-${tone}`;
  box.textContent = text;
  box.hidden = !text;
}

export function field(label, control, hint) {
  const id = `f${++fieldCounter}`;
  control.id = id;
  const caption = h('label', null, label);
  caption.htmlFor = id;
  const box = h('div', 'field', caption, control);
  if (hint) {
    const help = h('div', 'muted small', hint);
    help.id = `${id}-hint`;
    control.setAttribute('aria-describedby', help.id);
    box.append(help);
  }
  return box;
}

export function input(type, autocomplete, extra = {}) {
  const element = h('input');
  element.type = type;
  element.autocomplete = autocomplete;
  element.required = true;
  Object.assign(element, extra);
  return element;
}

/** Pole liczby (także z przecinkiem); value – liczba albo null (puste). */
export function numberInput(value, { min, max, required = true } = {}) {
  const element = h('input');
  element.type = 'text';
  element.inputMode = 'decimal';
  element.autocomplete = 'off';
  element.required = required;
  element.value = value === null || value === undefined ? '' : String(value).replace('.', ',');
  element.dataset.min = min ?? '';
  element.dataset.max = max ?? '';
  return element;
}

/** Liczba z pola (przecinek albo kropka); null – puste; NaN – nie liczba. */
export function numberValue(element) {
  const text = element.value.trim().replace(',', '.');
  if (text === '') return null;
  return /^-?\d+(\.\d+)?$/.test(text) ? Number(text) : Number.NaN;
}

export function checkbox(label, checked) {
  const box = h('input');
  box.type = 'checkbox';
  box.checked = Boolean(checked);
  const id = `f${++fieldCounter}`;
  box.id = id;
  const caption = h('label', 'check', box, ` ${label}`);
  caption.htmlFor = id;
  return { element: caption, box };
}

export function select(options, value) {
  const element = h('select');
  for (const [optionValue, label] of options) {
    const option = h('option', null, label);
    option.value = optionValue;
    if (optionValue === value) option.selected = true;
    element.append(option);
  }
  return element;
}

export function button(text, cls, onClick) {
  const element = h('button', cls, text);
  element.type = 'button';
  if (onClick) element.addEventListener('click', onClick);
  return element;
}

export function submitButton(text) {
  const element = h('button', null, text);
  element.type = 'submit';
  return element;
}

/** Formularz z obsługą błędu i blokadą przycisku na czas akcji; action może zwrócić tekst sukcesu. */
export function form(children, submit, action, cls = 'stack') {
  const error = errorBox('');
  const done = h('div', 'action-note tone-green');
  done.hidden = true;
  const element = h('form', cls, error, ...children, submit, done);
  element.addEventListener('submit', async (event) => {
    event.preventDefault();
    submit.disabled = true;
    error.hidden = true;
    done.hidden = true;
    try {
      const message = await action();
      if (typeof message === 'string' && message) note(done, message, 'green');
    } catch (failure) {
      error.textContent = failure.message;
      error.hidden = false;
    } finally {
      submit.disabled = false;
    }
  });
  return element;
}

/**
 * Potwierdzenie akcji w miejscu (bez okien przeglądarki): tekst, „Tak, …” i „Anuluj”. Zwraca element; wynik przez
 * onConfirm (async) – błąd pokazuje w tym samym miejscu.
 */
export function confirmBox(text, yesLabel, onConfirm, onCancel) {
  const box = h('div', 'confirm');
  box.setAttribute('role', 'alertdialog');
  const message = h('div', null, text);
  const error = errorBox('');
  const yes = button(yesLabel, 'small', async () => {
    yes.disabled = true;
    no.disabled = true;
    error.hidden = true;
    try {
      await onConfirm();
    } catch (failure) {
      error.textContent = failure.message;
      error.hidden = false;
      yes.disabled = false;
      no.disabled = false;
    }
  });
  const no = button('Anuluj', 'quiet small', () => onCancel());
  box.append(message, error, h('div', 'confirm-actions', yes, no));
  return box;
}

/** Tekst w kilku linijkach albo po przecinkach → lista bez pustych. */
export function listValue(textarea) {
  return textarea.value.split(/[\n,;]+/).map((item) => item.trim()).filter((item) => item !== '');
}
