// Alerty i historia maili w chmurze (3b): otwarte alerty z potwierdzeniem (koniec przypomnień), ręcznym zamknięciem i
// wyciszeniem czujnika, wyciszenie wszystkich alertów mailowych (urlop), zamknięte alerty i wysłane maile (raporty,
// alerty, próbne) z podglądem na osobnej stronie (mail.html – izolowana ramka). Dane z owner_alerts i owner_mail_history.

import { h, when, ago, plural, badge, syncClock } from './format.js';
import { start } from './shell.js';
import { button, confirmBox, errorBox, field, input, note } from './ui.js';

const KIND_TEXT = { NeedsWater: 'Do podlania', LowBattery: 'Słaba bateria', SensorSilent: 'Brak odczytów' };
const MAIL_KIND = { report: 'raport', 'report-request': 'raport na żądanie', alert: 'alert', 'mail-test': 'próbny', 'agent-silent': 'agent milczy' };
const DURATIONS = [['1 dzień', 1], ['3 dni', 3], ['tydzień', 7], ['2 tygodnie', 14]];

let cloud;
let main;
let data;
let mails = [];
let mailDays = 30;
let flash = null;

async function load() {
  data = await cloud.rpc('owner_alerts', { p_days: 30 });
  syncClock(data.serverTime);
  try {
    mails = await cloud.rpc('owner_mail_history', { p_days: mailDays });
  } catch (error) {
    mails = { error: error.message };
  }
}

async function act(fn, args, text, key) {
  await cloud.rpc(fn, args);
  await load();
  flash = { key, text };
  render();
}

function flashBox(key) {
  const box = h('div', 'action-note');
  box.hidden = true;
  if (flash && flash.key === key) {
    note(box, flash.text, 'green');
    box.dataset.flash = '1';
  }
  return box;
}

/** Wybór czasu wyciszenia: gotowe okresy albo data i godzina (do 90 dni). onPick(until: Date). */
function mutePicker(onPick, onCancel) {
  const box = h('div', 'confirm');
  const error = errorBox('');
  const pick = async (until) => {
    error.hidden = true;
    try {
      await onPick(until);
    } catch (failure) {
      error.textContent = failure.message;
      error.hidden = false;
    }
  };
  const quick = h('div', 'confirm-actions', ...DURATIONS.map(([label, days]) => button(label, 'small', () => pick(new Date(Date.now() + days * 86400e3)))));
  const custom = input('datetime-local', 'off', { required: false });
  const own = button('Do tej chwili', 'quiet small', () => {
    if (!custom.value) {
      error.textContent = 'Wybierz datę i godzinę.';
      error.hidden = false;
      return;
    }
    pick(new Date(custom.value));
  });
  box.append(h('div', null, 'Wyciszyć alerty mailowe na:'), quick, field('albo do', custom), h('div', 'confirm-actions', own, button('Anuluj', 'quiet small', onCancel)), error);
  return box;
}

// ---- urlop -------------------------------------------------------------------------------------------------------

function vacationSection() {
  const box = h('div', 'card');
  const extra = h('div');
  if (data.alertsMutedUntil) {
    box.className = 'card accent-amber';
    box.append(h('div', null, h('strong', null, `Wszystkie alerty mailowe wyciszone do ${when(data.alertsMutedUntil)}`)),
      h('div', 'muted small', 'Alerty dalej się otwierają i zamykają (widać je tutaj i na pulpicie); maile o nich wyjdą po końcu wyciszenia, jeśli stan potrwa. Raporty przychodzą dalej.'),
      h('div', 'card-actions', button('Zakończ wyciszenie', 'small', () => act('owner_alerts_mute', { p_sensor: null, p_until: null },
        'Wyciszenie zakończone – nadawca wyśle maile o trwających alertach w ciągu kilku minut.', 'vacation'))));
  } else {
    const start = button('Wycisz wszystkie alerty (urlop)…', 'quiet small', () => {
      start.hidden = true;
      extra.replaceChildren(mutePicker(async (until) => {
        await act('owner_alerts_mute', { p_sensor: null, p_until: until.toISOString() }, `Alerty mailowe wyciszone do ${when(until.toISOString())}.`, 'vacation');
      }, () => {
        extra.replaceChildren();
        start.hidden = false;
      }));
    });
    box.append(h('div', null, 'Alerty mailowe przychodzą normalnie.'),
      h('div', 'muted small', 'Na urlop: wyciszenie wszystkich alertów mailowych do wybranej chwili (raporty idą dalej).'),
      h('div', 'card-actions', start));
  }
  box.append(extra);
  return h('section', null, h('h2', null, 'Wyciszenie'), flashBox('vacation'), box, mutedSensors());
}

function mutedSensors() {
  const muted = data.sensors.filter((s) => s.alertsMutedUntil);
  if (muted.length === 0) return null;
  const list = h('div', 'cards');
  for (const sensor of muted) {
    list.append(h('div', 'card accent-grey', h('div', null, h('strong', null, sensor.name), ` – alerty wyciszone do ${when(sensor.alertsMutedUntil)}`),
      h('div', 'card-actions', button('Zakończ', 'quiet small', () => act('owner_alerts_mute', { p_sensor: sensor.id, p_until: null },
        `${sensor.name}: wyciszenie zakończone.`, 'vacation')))));
  }
  return h('div', 'muted-list', h('div', 'muted small', 'Wyciszone czujniki:'), list);
}

// ---- otwarte alerty ----------------------------------------------------------------------------------------------

function openAlertCard(alert) {
  const key = `alert:${alert.id}`;
  const card = h('div', `card accent-${alert.kind === 'NeedsWater' ? 'red' : 'amber'}`);
  const sensor = data.sensors.find((s) => s.id === alert.sensor);
  const title = h('a', null, `${alert.sensorName} – ${KIND_TEXT[alert.kind] || alert.kind}`);
  title.href = `sensor.html?id=${encodeURIComponent(alert.sensor)}`;
  const badges = h('div', 'badges');
  if (alert.acknowledgedAt) badges.append(badge(`potwierdzony ${when(alert.acknowledgedAt)}`, 'grey'));
  if (sensor && sensor.alertsMutedUntil) badges.append(badge(`czujnik wyciszony do ${when(sensor.alertsMutedUntil)}`, 'grey'));
  card.append(flashBox(key), h('div', null, h('strong', null, title)), h('div', null, alert.detail));
  if (badges.childElementCount > 0) card.append(badges);
  card.append(h('div', 'muted small', `Otwarty ${when(alert.openedAt)} (${ago(alert.openedAt)}) · ` +
    (alert.lastNotifiedAt ? `ostatni mail ${when(alert.lastNotifiedAt)}` : 'mail jeszcze niewysłany') +
    (alert.acknowledgedAt ? ' · bez przypomnień' : ` · przypomnienie co ${data.alertReminderHours} godz.`)));

  const extra = h('div');
  const actions = h('div', 'card-actions');
  actions.append(alert.acknowledgedAt
    ? button('Cofnij potwierdzenie', 'quiet small', () => act('owner_alert_acknowledge', { p_id: alert.id, p_acknowledge: false },
      'Potwierdzenie cofnięte – przypomnienia wrócą.', key))
    : button('Potwierdź', 'small', () => act('owner_alert_acknowledge', { p_id: alert.id, p_acknowledge: true },
      'Potwierdzony – bez przypomnień; alert zamknie się sam, gdy stan minie.', key)));
  actions.append(button('Zamknij…', 'quiet small', () => {
    extra.replaceChildren(confirmBox('Zamknąć alert ręcznie? Jeśli stan trwa (np. wciąż sucho), nadawca otworzy nowy alert i wyśle o nim mail – żeby tylko przestać dostawać przypomnienia, wybierz „Potwierdź”.',
      'Tak, zamknij', () => act('owner_alert_close', { p_id: alert.id }, 'Alert zamknięty.', 'open'), () => extra.replaceChildren()));
  }));
  if (sensor && !sensor.alertsMutedUntil) {
    actions.append(button('Wycisz czujnik…', 'quiet small', () => {
      extra.replaceChildren(mutePicker(async (until) => {
        await act('owner_alerts_mute', { p_sensor: alert.sensor, p_until: until.toISOString() },
          `${alert.sensorName}: alerty mailowe wyciszone do ${when(until.toISOString())}.`, key);
      }, () => extra.replaceChildren()));
    }));
  }
  card.append(actions, extra);
  return card;
}

function openSection() {
  const list = h('div', 'cards');
  list.append(...data.open.map(openAlertCard));
  if (data.open.length === 0) list.append(h('div', 'empty', 'Brak otwartych alertów.'));
  return h('section', null, h('h2', null, `Otwarte (${data.open.length})`), flashBox('open'), list);
}

// ---- zamknięte i maile -------------------------------------------------------------------------------------------

function closedSection() {
  if (data.closed.length === 0) return h('section', null, h('h2', null, 'Zamknięte (30 dni)'), h('div', 'empty', 'Brak zamkniętych alertów w ostatnich 30 dniach.'));
  const table = h('table', 'readings history');
  table.append(h('thead', null, h('tr', null, h('th', null, 'Czujnik'), h('th', null, 'Alert'), h('th', null, 'Od'), h('th', null, 'Do'))));
  const body = h('tbody');
  for (const alert of data.closed) {
    body.append(h('tr', null, h('td', null, alert.sensorName), h('td', null, `${KIND_TEXT[alert.kind] || alert.kind}${alert.closedManually ? ' (ręcznie)' : ''}`),
      h('td', null, when(alert.openedAt)), h('td', null, when(alert.resolvedAt))));
  }
  table.append(body);
  return h('section', null, h('h2', null, `Zamknięte (30 dni): ${data.closed.length}`), h('div', 'table-wrap', table));
}

function mailSection() {
  const head = h('div', 'section-head', h('h2', null, 'Wysłane maile'));
  const range = button(mailDays === 30 ? 'Pokaż 90 dni' : 'Pokaż 30 dni', 'quiet small', async () => {
    mailDays = mailDays === 30 ? 90 : 30;
    await load();
    render();
  });
  head.append(range);
  if (mails.error) return h('section', null, head, h('div', 'empty', `Nie udało się wczytać historii: ${mails.error}`));
  if (mails.length === 0) {
    return h('section', null, head, h('div', 'empty', `Brak maili z ostatnich ${mailDays} dni w historii. Historię zapisuje nadawca od wersji z fazy 3 (raporty, alerty i próbne maile; bez dzienników administratora).`));
  }
  const table = h('table', 'readings history');
  table.append(h('thead', null, h('tr', null, h('th', null, 'Wysłany'), h('th', null, 'Rodzaj'), h('th', null, 'Temat'), h('th', null, ''))));
  const body = h('tbody');
  for (const mail of mails) {
    const view = h('a', null, 'Podgląd');
    view.href = `mail.html?key=${encodeURIComponent(mail.key)}`;
    body.append(h('tr', null, h('td', null, when(mail.sentAt)), h('td', null, MAIL_KIND[mail.kind] || mail.kind),
      h('td', 'subject', `${mail.subject} (${mail.recipients.length} ${plural(mail.recipients.length, 'odbiorca', 'odbiorców', 'odbiorców')})`), h('td', null, view)));
  }
  table.append(body);
  return h('section', null, head, h('div', 'muted small', `Ostatnie ${mailDays} dni; starsze maile znikają z historii.`), h('div', 'table-wrap tall', table));
}

function render() {
  main.replaceChildren(h('h1', null, 'Alerty'), vacationSection(), openSection(), closedSection(), mailSection());
  const target = main.querySelector('[data-flash="1"]');
  if (target) target.scrollIntoView({ block: 'nearest' });
  flash = null;
}

start(async (client, element) => {
  cloud = client;
  main = element;
  await load();
  render();
  return async () => {
    if (main.querySelector('.confirm')) return;      // nie przerywamy wyboru w trakcie
    await load();
    render();
  };
});
