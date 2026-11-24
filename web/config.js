// Edytor konfiguracji w chmurze (faza 3c): ustawienia, poczta i odbiorcy, czujniki (dodanie z plikiem klucza z „pair”,
// progi, archiwum), agenci (nowy z kodem i QR, rola, wyłączenie, odcięcie), strażnik. Sekrety tylko do ustawienia. Każdy
// zapis niesie config_version: gdy ktoś zmienił konfigurację w międzyczasie, strona mówi to zamiast nadpisać.
// Reguły pól sprawdza baza (jedyne źródło prawdy, ConfigContractTests) – tu tylko liczby z przecinkiem i czytelne komunikaty.

import { h, when, ago, plural, badge, kv, syncClock, num1, isFlowerCare } from './format.js';
import { start, banner } from './shell.js';
import { CloudError } from './supabase.js';
import { qrSvg, enrollmentLink } from './qr.js';
import { command, describe, follow } from './commands.js';
import {
  button, checkbox, confirmBox, field, form, input, listValue, note, numberInput, numberValue, PLATFORMS, select, submitButton
} from './ui.js';

const MAX_KEY_FILE = 16 * 1024;

let cloud;
let main;
let config;

// ---- zapis i odświeżenie ---------------------------------------------------------------------------------------

async function load() {
  config = await cloud.rpc('owner_config');
  syncClock(config.serverTime);
}

/** Zapis z wersją konfiguracji; potem świeży stan i ponowne narysowanie z notką przy zmienionym miejscu. */
async function write(fn, args, flash) {
  try {
    await cloud.rpc(fn, { p_version: config.configVersion, ...args });
  } catch (error) {
    if (error instanceof CloudError && error.kind === 'conflict') {
      await load();
      render({ key: flash.key, text: `${error.message}`, tone: 'amber' });
      return;
    }
    throw error;
  }
  await load();
  render(flash);
}

function requireNumber(element, label, { optional = false, integer = false } = {}) {
  const value = numberValue(element);
  if (value === null) {
    if (optional) return null;
    throw new Error(`${label}: podaj liczbę.`);
  }
  if (Number.isNaN(value)) throw new Error(`${label}: „${element.value}” to nie liczba.`);
  if (integer && !Number.isInteger(value)) throw new Error(`${label}: podaj liczbę całkowitą.`);
  return value;
}

function minutesLabel(value) {
  if (value === null || value === undefined) return '–';
  const hours = Math.floor(value / 60);
  const minutes = Math.round((value - hours * 60) * 10) / 10;
  if (hours === 0) return `${num1.format(minutes).replace(',0', '')} min`;
  return minutes === 0 ? `${hours} godz.` : `${hours} godz. ${num1.format(minutes).replace(',0', '')} min`;
}

function stamp(iso) {
  return iso ? `${when(iso)} (${ago(iso)})` : null;
}

function flashBox(flash, key) {
  const box = h('div', 'action-note');
  box.hidden = true;
  if (flash && flash.key === key) {
    note(box, flash.text, flash.tone || 'green');
    box.dataset.flash = '1';
  }
  return box;
}

function section(id, title, intro, ...children) {
  const box = h('section', 'config-section', h('h2', null, title));
  box.id = id;
  if (intro) box.append(h('p', 'muted small', intro));
  box.append(...children);
  return box;
}

// ---- ustawienia --------------------------------------------------------------------------------------------------

function zoneList() {
  const list = h('datalist');
  list.id = 'zones';
  const zones = typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : ['Europe/Warsaw', 'UTC'];
  for (const zone of zones) {
    const option = h('option');
    option.value = zone;
    list.append(option);
  }
  return list;
}

function settingsSection(flash) {
  const s = config.settings;
  const zone = input('text', 'off', { value: s.timeZone });
  zone.setAttribute('list', 'zones');
  const interval = numberInput(s.readIntervalMinutes);
  const scan = numberInput(s.scanWindowSeconds);
  const times = input('text', 'off', { value: s.reportTimes.join(', '), required: false, placeholder: '07:00, 19:00' });
  const catchUp = numberInput(s.reportCatchUpHours);
  const reminder = numberInput(s.alertReminderHours);
  const retention = numberInput(s.logRetentionDays);

  const settingsForm = form([
    field('Strefa czasowa raportów', zone, 'Nazwa IANA, np. Europe/Warsaw.'),
    field('Odstęp odczytów (min)', interval, `Teraz ${minutesLabel(s.readIntervalMinutes)}; czujnik zapisuje pomiar co godzinę, każdy odczyt kosztuje baterię (125 = 2 godz. 5 min).`),
    field('Okno nasłuchu (s)', scan, 'Ile sekund agent słucha reklam czujników w jednym cyklu (5–120).'),
    field('Pory raportów', times, 'Godziny HH:mm po przecinku; puste – bez raportów o stałych porach.'),
    field('Nadrabianie raportu (godz.)', catchUp, 'Raport z przerwy nadawcy wychodzi, jeśli minęło nie więcej niż tyle godzin (0–24).'),
    field('Przypomnienie o alercie co (godz.)', reminder),
    field('Dzienniki agentów w chmurze (dni)', retention, '1–30.')
  ], submitButton('Zapisz ustawienia'), async () => {
    const patch = {
      timeZone: zone.value.trim(),
      readIntervalMinutes: requireNumber(interval, 'Odstęp odczytów'),
      scanWindowSeconds: requireNumber(scan, 'Okno nasłuchu', { integer: true }),
      reportTimes: listValue(times),
      reportCatchUpHours: requireNumber(catchUp, 'Nadrabianie raportu'),
      alertReminderHours: requireNumber(reminder, 'Przypomnienie o alercie'),
      logRetentionDays: requireNumber(retention, 'Dzienniki agentów', { integer: true })
    };
    await write('owner_settings_update', { p: patch }, {
      key: 'settings', text: 'Zapisano. Agenci przejmą zmiany przy najbliższej synchronizacji (ok. 1–2 min).'
    });
  });
  return section('settings', 'Odczyty i raporty', null, flashBox(flash, 'settings'), zoneList(), settingsForm);
}

// ---- poczta ------------------------------------------------------------------------------------------------------

function mailSection(flash) {
  const mail = config.mail;
  const gmail = input('email', 'off', { value: mail.gmailAddress || '', required: false, placeholder: 'glebosaurus@gmail.com' });
  const alertTo = h('textarea');
  alertTo.rows = 3;
  alertTo.value = mail.alertRecipients.join('\n');
  const reportTo = h('textarea');
  reportTo.rows = 2;
  reportTo.value = mail.reportRecipients.join('\n');
  const adminTo = h('textarea');
  adminTo.rows = 2;
  adminTo.value = mail.adminRecipients.join('\n');

  const addresses = form([
    field('Konto Gmail nadawcy', gmail, 'Najlepiej osobne konto tylko do tego – hasło aplikacji daje pełny dostęp do skrzynki.'),
    field('Odbiorcy alertów', alertTo, 'Jeden adres w linii (albo po przecinku). Wymagany, gdy jest konto nadawcy.'),
    field('Odbiorcy raportów', reportTo, 'Puste – raporty idą do odbiorców alertów.'),
    field('Administrator (dzienniki agentów)', adminTo, 'Codzienny mail z dziennikiem każdego agenta; puste – bez tych maili.')
  ], submitButton('Zapisz adresy'), async () => {
    await write('owner_settings_update', {
      p: { gmailAddress: gmail.value.trim() || null, alertRecipients: listValue(alertTo), reportRecipients: listValue(reportTo), adminRecipients: listValue(adminTo) }
    }, { key: 'mail', text: 'Zapisano adresy. Nadawca przejmie je przy najbliższej synchronizacji.' });
  });

  const password = input('password', 'new-password', { placeholder: 'abcd efgh ijkl mnop' });
  const passwordStatus = mail.appPasswordSetAt ? `ustawione ${stamp(mail.appPasswordSetAt)}` : 'nie ustawione – maile nie wychodzą';
  const passwordForm = form([
    field('Hasło aplikacji Gmail', password, `Teraz: ${passwordStatus}. Nowe hasło: myaccount.google.com/apppasswords (konto z weryfikacją dwuetapową). Strona go nie pokaże – tylko zapisze.`)
  ], submitButton('Ustaw hasło'), async () => {
    await cloud.rpc('owner_secret_set', { p_name: 'gmail-app-password', p_value: password.value });
    password.value = '';
    await load();
    render({ key: 'mail', text: 'Hasło zapisane w chmurze (Vault). Sprawdź je próbnym mailem poniżej.' });
  });

  return section('mail', 'Poczta', 'Maile wysyła agent z blokadą nadawcy, z tego konta Gmail.',
    flashBox(flash, 'mail'), addresses, passwordForm, testMail());
}

function testMail() {
  const box = h('div', 'card');
  const status = h('div', 'action-note');
  status.hidden = true;
  const recipients = config.mail.alertRecipients;
  const startButton = button('Wyślij próbny mail…', 'quiet', () => {
    if (recipients.length === 0) {
      note(status, 'Najpierw zapisz odbiorców alertów – próbny mail idzie do nich.', 'amber');
      return;
    }
    startButton.hidden = true;
    const ask = confirmBox(`Wysłać PRAWDZIWĄ wiadomość próbną do: ${recipients.join(', ')}? Wyśle ją obecny nadawca ze świeżo pobranym hasłem.`,
      'Tak, wyślij', async () => {
        const created = await command(cloud, 'mail_test');
        ask.remove();
        startButton.hidden = false;
        await followTestMail(created.id, status);
      }, () => {
        ask.remove();
        startButton.hidden = false;
      });
    box.append(ask);
  });
  box.append(h('div', null, h('strong', null, 'Próbny mail')),
    h('div', 'muted small', 'Sprawdza hasło i dostarczenie: polecenie trafia do agenta, który teraz wysyła maile, zwykle w ciągu 1–2 min.'),
    h('div', 'card-actions', startButton), status);
  return box;
}

/** Próbny mail: śledzi polecenie do wyniku; przy sukcesie mówi, do ilu odbiorców poszedł. */
async function followTestMail(id, status) {
  note(status, 'Próbny mail: zlecony – czeka na nadawcę…', 'grey');
  try {
    await follow(cloud, [id], (item) => {
      const state = describe(item);
      const recipients = item.result && item.result.recipients;
      note(status, item.doneAt && item.result && item.result.ok
        ? `Próbny mail: wysłany przez ${item.takenBy} do ${recipients} ${plural(recipients, 'odbiorcy', 'odbiorców', 'odbiorców')}. Sprawdź skrzynkę.`
        : state.text, state.tone);
    });
  } catch (error) {
    note(status, `Próbny mail: nie udało się sprawdzić stanu (${error.message}).`, 'amber');
  }
}

// ---- czujniki ----------------------------------------------------------------------------------------------------

async function readKeyFile(fileInput) {
  const file = fileInput.files && fileInput.files[0];
  if (!file) throw new Error('Wybierz plik klucza (ID.credentials.json z polecenia pair).');
  if (file.size > MAX_KEY_FILE) throw new Error('To nie jest plik klucza z pair (za duży).');
  let data;
  try {
    data = JSON.parse(await file.text());
  } catch {
    throw new Error('Plik nie jest poprawnym JSON-em z polecenia pair.');
  }
  if (!data || typeof data !== 'object' || Array.isArray(data)) throw new Error('Plik nie jest obiektem JSON z polecenia pair.');
  return { data, name: file.name };
}

function keyFileInput() {
  const element = h('input');
  element.type = 'file';
  element.accept = '.json,application/json';
  element.required = true;
  return element;
}

function sensorOptionFields(sensor) {
  const enabled = checkbox('Włączony (agenci go czytają, nadawca liczy alerty)', sensor ? sensor.enabled : true);
  const below = numberInput(sensor ? sensor.wateringBelowPercent : 25);
  const clear = numberInput(sensor ? sensor.wateringClearAbovePercent : 35);
  const consecutive = numberInput(sensor ? sensor.consecutiveReadings : 2);
  const battery = numberInput(sensor ? sensor.lowBatteryPercent : 18);
  const silent = numberInput(sensor ? sensor.silentAfterHours : 6);
  const interval = numberInput(sensor ? sensor.readIntervalMinutes : null, { required: false });
  interval.placeholder = `wspólny (${minutesLabel(config.settings.readIntervalMinutes)})`;
  const fields = [
    enabled.element,
    h('div', 'field-row',
      field('Podlać poniżej (%)', below),
      field('Koniec alertu od (%)', clear)),
    h('div', 'field-row',
      field('Odczyty z rzędu', consecutive, '1–10 poniżej progu, zanim pójdzie alert.'),
      field('Słaba bateria od (%)', battery)),
    h('div', 'field-row',
      field('Cisza po (godz.)', silent, 'Alert, gdy tyle godzin bez odczytu.'),
      field('Odstęp odczytów (min)', interval, 'Puste – wspólny.'))
  ];
  const read = () => ({
    enabled: enabled.box.checked,
    wateringBelowPercent: requireNumber(below, 'Podlać poniżej', { integer: true }),
    wateringClearAbovePercent: requireNumber(clear, 'Koniec alertu od', { integer: true }),
    consecutiveReadings: requireNumber(consecutive, 'Odczyty z rzędu', { integer: true }),
    lowBatteryPercent: requireNumber(battery, 'Słaba bateria od', { integer: true }),
    silentAfterHours: requireNumber(silent, 'Cisza po'),
    readIntervalMinutes: requireNumber(interval, 'Odstęp odczytów', { optional: true })
  });
  return { fields, read, enabled };
}

function sensorSummary(sensor) {
  return `podlać < ${sensor.wateringBelowPercent}% (koniec od ${sensor.wateringClearAbovePercent}%), ${sensor.consecutiveReadings} ` +
    `${plural(sensor.consecutiveReadings, 'odczyt', 'odczyty', 'odczytów')} z rzędu · bateria ≤ ${sensor.lowBatteryPercent}% · ` +
    `cisza > ${num1.format(sensor.silentAfterHours).replace(',0', '')} godz. · odczyt co ${sensor.readIntervalMinutes ? minutesLabel(sensor.readIntervalMinutes) : `${minutesLabel(config.settings.readIntervalMinutes)} (wspólny)`}`;
}

function sensorCard(sensor, flash) {
  const key = `sensor:${sensor.id}`;
  const archived = Boolean(sensor.archivedAt);
  const card = h('div', `card accent-${archived ? 'grey' : sensor.enabled ? 'green' : 'amber'}`);
  card.id = key;
  const badges = h('div', 'badges');
  if (archived) badges.append(badge(`archiwum od ${when(sensor.archivedAt)}`, 'grey'));
  else if (!sensor.enabled) badges.append(badge('wyłączony', 'amber'));
  const flowerCare = isFlowerCare(sensor);
  if (!flowerCare && !sensor.keySetAt) badges.append(badge('brak klucza', 'red'));
  card.append(flashBox(flash, key), h('div', 'sensor-name', sensor.name), h('div', 'muted small', `${sensor.id} · MAC ${sensor.mac}`));
  if (badges.childElementCount > 0) card.append(badges);
  card.append(h('div', 'lines small', sensorSummary(sensor)),
    h('div', 'muted small', flowerCare ? 'Xiaomi Flower Care – bez klucza, agenci rozpoznają go po adresie MAC.'
      : sensor.keySetAt ? `Klucz Tuya ustawiony ${stamp(sensor.keySetAt)}.` : 'Klucz Tuya nie jest ustawiony – agenci pominą czujnik.'));

  const actions = h('div', 'card-actions');
  const extra = h('div');
  if (!archived) {
    const edit = h('details', 'editor');
    const name = input('text', 'off', { value: sensor.name, maxLength: 80 });
    const options = sensorOptionFields(sensor);
    edit.append(h('summary', null, 'Zmień'), form([field('Nazwa', name), ...options.fields], submitButton('Zapisz czujnik'), async () => {
      await write('owner_sensor_update', { p_id: sensor.id, p_name: name.value, p_options: options.read() }, {
        key, text: 'Zapisano. Agenci i nadawca przejmą zmiany przy najbliższej synchronizacji.'
      });
    }));

    const keyEdit = h('details', 'editor');
    if (flowerCare) {
      const mac = macInput(sensor.mac);
      keyEdit.append(h('summary', null, 'Nowy adres MAC'), form([
        field('Adres MAC', mac, 'Inny czujnik Flower Care przy tej samej roślinie – historia zostaje pod tym samym id.')
      ], submitButton('Zapisz adres'), async () => {
        await write('owner_sensor_mac', { p_id: sensor.id, p_mac: mac.value.trim() }, { key, text: 'Nowy adres zapisany. Agenci przejmą go przy najbliższej synchronizacji.' });
      }));
    } else {
      const file = keyFileInput();
      keyEdit.append(h('summary', null, 'Nowy klucz z pliku'), form([
        field('Plik klucza', file, `Po resecie czujnika i „glebosaurus pair --id ${sensor.id}”. MAC z pliku zastąpi obecny.`)
      ], submitButton('Wgraj klucz'), async () => {
        const { data } = await readKeyFile(file);
        await write('owner_sensor_credentials', { p_id: sensor.id, p_credentials: data }, { key, text: 'Nowy klucz zapisany w chmurze (Vault).' });
      }));
    }
    actions.append(button('Do archiwum…', 'quiet small', () => {
      extra.replaceChildren(confirmBox(`Przenieść „${sensor.name}” do archiwum? Zniknie z pulpitu, maili i agentów (otwarte alerty zostaną zamknięte); historia odczytów${flowerCare ? '' : ' i klucz'} zostają, a przywrócić go można w każdej chwili.`,
        'Tak, do archiwum', async () => {
          await write('owner_sensor_archive', { p_id: sensor.id, p_archive: true }, { key, text: 'Czujnik w archiwum.' });
        }, () => extra.replaceChildren()));
    }));
    card.append(edit, keyEdit);
  } else {
    actions.append(button('Przywróć', 'quiet small', async (event) => {
      event.target.disabled = true;
      try {
        await write('owner_sensor_archive', { p_id: sensor.id, p_archive: false }, { key, text: 'Przywrócony – jest wyłączony; włącz go w „Zmień”, gdy będzie gotowy.' });
      } catch (error) {
        event.target.disabled = false;
        extra.replaceChildren(h('div', 'action-note tone-red', error.message));
      }
    }));
    if (!sensor.hasReadings) {
      actions.append(button('Usuń na dobre…', 'quiet small', () => {
        extra.replaceChildren(confirmBox(`Usunąć „${sensor.name}” na dobre${flowerCare ? '' : ', razem z kluczem'}? (Czujnik nie ma żadnych odczytów w chmurze.)`,
          'Tak, usuń', async () => {
            await write('owner_sensor_delete', { p_id: sensor.id }, { key: 'sensors', text: `Czujnik ${sensor.id} usunięty.` });
          }, () => extra.replaceChildren()));
      }));
    }
  }
  card.append(actions, extra);
  return card;
}

function addSensorForm(flash) {
  const details = h('details', 'editor add');
  if (flash && flash.key === 'sensor-add') details.open = true;
  const file = keyFileInput();
  const id = input('text', 'off', { pattern: '[a-z0-9][a-z0-9_\\-]{0,39}', placeholder: 'np. bazylia' });
  const name = input('text', 'off', { required: false, maxLength: 80, placeholder: 'np. Bazylia na parapecie' });
  const preview = h('div', 'muted small');
  const options = sensorOptionFields(null);
  file.addEventListener('change', async () => {
    try {
      const { data, name: fileName } = await readKeyFile(file);
      const guess = fileName.replace(/\.credentials\.json$/i, '').replace(/\.json$/i, '').toLowerCase();
      if (!id.value && /^[a-z0-9][a-z0-9_-]{0,39}$/.test(guess)) id.value = guess;
      preview.textContent = `Plik: MAC ${data.mac || '–'}, produkt ${data.productId || '–'}` +
        (data.verified === false ? ' · UWAGA: parowanie nie zostało potwierdzone (pair przerwane?) – czujnik może nie przyjąć tego klucza.' : '');
    } catch (error) {
      preview.textContent = error.message;
    }
  });
  details.append(h('summary', null, 'Dodaj czujnik z pliku klucza'),
    h('p', 'muted small', 'Plik ID.credentials.json powstaje przy parowaniu lokalnym: glebosaurus pair --id ID --confirm-bind (prościej – „Sparuj czujnik zdalnie” wyżej). Czujnik ze Smart Life: plik z kluczem z chmury Tuya (README). Klucz trafia od razu do Vault.'),
    form([field('Plik klucza', file), preview, field('Id (stałe, w adresach i kluczach maili)', id, 'Małe litery, cyfry, „_” i „-”, do 40 znaków.'),
      field('Nazwa', name, 'Puste – jak id.'), ...options.fields], submitButton('Dodaj czujnik'), async () => {
      const { data } = await readKeyFile(file);
      await write('owner_sensor_add', { p_id: id.value.trim(), p_name: name.value, p_credentials: data, p_options: options.read() }, {
        key: `sensor:${id.value.trim()}`, text: 'Czujnik dodany. Agenci zaczną go słuchać przy najbliższej synchronizacji.'
      });
    }));
  return details;
}

/** Pole adresu MAC czujnika (bez klucza – Flower Care); baza ujednolica zapis (wielkie litery, dwukropki). */
function macInput(value) {
  return input('text', 'off', { value: value || '', pattern: '([0-9A-Fa-f]{2}[:\\-]?){5}[0-9A-Fa-f]{2}', placeholder: 'np. 5C:85:7E:14:78:3C', maxLength: 17 });
}

function addFlowerCareForm(flash) {
  const details = h('details', 'editor add');
  if (flash && flash.key === 'sensor-add-flower-care') details.open = true;
  const mac = macInput('');
  const id = input('text', 'off', { pattern: '[a-z0-9][a-z0-9_\\-]{0,39}', placeholder: 'np. fikus' });
  const name = input('text', 'off', { required: false, maxLength: 80, placeholder: 'np. Fikus w salonie' });
  const options = sensorOptionFields(null);
  details.append(h('summary', null, 'Dodaj czujnik Xiaomi Flower Care (po adresie MAC)'),
    h('p', 'muted small', 'Czujnik Xiaomi / HHCC Flower Care (HHCCJCY01) nie wymaga parowania ani klucza – wystarczy jego adres MAC. ' +
      'Pokaże go „glebosaurus scan” na komputerze w zasięgu (sekcja Xiaomi Flower Care) albo podgląd zasięgu w aplikacji na telefonie. ' +
      'Poza wilgotnością mierzy światło i żyzność (przewodność gleby); temperaturę – w główce nad ziemią. Jego skala wilgotności różni się ' +
      'od czujników Tuya, więc próg podlania najlepiej ustawić po kilku dniach obserwacji.'),
    form([field('Adres MAC', mac), field('Id (stałe, w adresach i kluczach maili)', id, 'Małe litery, cyfry, „_” i „-”, do 40 znaków.'),
      field('Nazwa', name, 'Puste – jak id.'), ...options.fields], submitButton('Dodaj czujnik'), async () => {
      await write('owner_sensor_add_flower_care', { p_id: id.value.trim(), p_name: name.value, p_mac: mac.value.trim(), p_options: options.read() }, {
        key: `sensor:${id.value.trim()}`, text: 'Czujnik dodany. Agenci zaczną go słuchać przy najbliższej synchronizacji.'
      });
    }));
  return details;
}

// ---- parowanie zdalne (3d) ---------------------------------------------------------------------------------------

const PAIR_POLL_MS = 2000;
const PAIR_STAGE = {
  waiting: 'Agent odebrał polecenie – kończy trwający odczyt i za chwilę uruchomi skan. Jeszcze NIE wkładaj baterii.',
  binding: 'Przypisuję klucz…',
  verifying: 'Sprawdzam klucz odczytem (do 1,5 min)…'
};

/** Agenci, którzy parują zdalnie: zarejestrowani, włączeni, na Windows (Linux po fazie 2.3b, Android później). */
function pairingAgents() {
  return config.agents.filter((a) => a.enabled && a.enrolled && a.status && a.status.platform === 'windows');
}

function remotePairForm() {
  const details = h('details', 'editor add');
  const agents = pairingAgents();
  details.append(h('summary', null, 'Sparuj czujnik zdalnie (agent w zasięgu)'),
    h('p', 'muted small', 'Bez pliku klucza: agent na komputerze z Windows w zasięgu czujnika paruje go sam, a klucz trafia od razu do chmury. ' +
      'Id istniejącego czujnika = nowy klucz po resecie przyciskiem (ten sam klucz, jeśli był).'));
  if (agents.length === 0) {
    details.append(h('div', 'empty', 'Żaden agent nie paruje teraz zdalnie – potrzebny zarejestrowany agent na Windows z Bluetooth (wersja z fazy 3).'));
    return details;
  }
  const agent = select(agents.map((a) => [a.name, `${a.name}${a.lastSeenAt ? ` (ostatnio ${ago(a.lastSeenAt)})` : ''}`]), agents[0].name);
  const id = input('text', 'off', { pattern: '[a-z0-9][a-z0-9_\\-]{0,39}', placeholder: 'np. bazylia' });
  const name = input('text', 'off', { required: false, maxLength: 80, placeholder: 'np. Bazylia na parapecie' });
  const mac = input('text', 'off', { required: false, placeholder: 'tylko przy kilku czujnikach w zasięgu' });
  const status = h('div', 'pair-status');
  status.hidden = true;
  const startForm = form([
    field('Agent', agent, 'Ten, który stoi najbliżej czujnika.'),
    field('Id czujnika', id, 'Małe litery, cyfry, „_” i „-”, do 40 znaków; zostaje na stałe.'),
    field('Nazwa', name, 'Puste – jak id.'),
    field('MAC (opcjonalnie)', mac, 'Gdy w zasięgu jest kilka niesparowanych czujników – strona poda ich adresy.')
  ], submitButton('Zacznij parowanie'), async () => {
    const created = await cloud.rpc('owner_pair_start', {
      p_agent: agent.value, p_sensor: id.value.trim(), p_name: name.value.trim() || null, p_mac: mac.value.trim() || null
    });
    startForm.hidden = true;
    status.hidden = false;
    if (!created.created) note(status, 'To parowanie już trwa – pokazuję jego postęp.', 'grey');
    await followPairing(created.id, agent.value, id.value.trim(), status, (candidates) => {
      if (candidates.length > 0) mac.placeholder = candidates.join(' albo ');
      startForm.hidden = false;
    });
  });
  details.append(startForm, status);
  return details;
}

/** Postęp parowania co 2 s: „włóż baterie” dopiero, gdy agent zgłosi działający skan (z odliczaniem). */
async function followPairing(commandId, agentName, sensorId, status, onFinished) {
  const show = (tone, ...children) => {
    status.className = `pair-status action-note tone-${tone}`;
    status.replaceChildren(...children);
  };
  show('grey', `Czekam, aż agent ${agentName} odbierze polecenie (synchronizuje się co ok. 1,5 min). Jeszcze NIE wkładaj baterii.`);
  let countdown = 0;
  const stopCountdown = () => window.clearInterval(countdown);
  try {
    await follow(cloud, [commandId], (item) => {
      const progress = item.progress || {};
      if (item.doneAt) {
        stopCountdown();
        const result = item.result || {};
        if (result.ok) {
          show('green', h('strong', null, `Sparowano czujnik ${sensorId}`), ` (MAC ${result.mac}). ` +
            (result.moisture !== undefined && result.moisture !== null ? `Wilgotność ${result.moisture}%. ` : '') +
            (result.created ? 'Czujnik jest już w konfiguracji – agenci zaczną go czytać przy najbliższej synchronizacji.' : 'Czujnik ma nowy klucz.'));
          const text = `Czujnik ${sensorId} sparowany zdalnie (MAC ${result.mac}` +
            (result.moisture !== undefined && result.moisture !== null ? `, wilgotność ${result.moisture}%` : '') + ').';
          load().then(() => render({ key: config.sensors.some((s) => s.id === sensorId) ? `sensor:${sensorId}` : 'sensors', text }))
            .catch(() => {});
        } else {
          const candidates = result.candidates || [];
          // Zapisany klucz wznawia parowanie tego samego czujnika; gdy pod tym id ma być inny – trzeba go porzucić.
          const forget = button('Zapomnij zapisany klucz (to inny czujnik)', 'quiet small', async () => {
            forget.disabled = true;
            try {
              const forgotten = await cloud.rpc('owner_pair_forget', { p_sensor: sensorId });
              forget.replaceWith(h('div', 'small', forgotten.forgotten ? 'Klucz porzucony – kolejna próba wymyśli nowy.' : 'Nie było zapisanego klucza.'));
            } catch (error) {
              forget.replaceWith(h('div', 'small', `Nie udało się: ${error.message}`));
            }
          });
          show('red', `Parowanie nie wyszło: ${result.error || 'agent zgłosił błąd'}.`,
            candidates.length > 0 ? ` Czujniki w zasięgu: ${candidates.join(', ')} – wpisz MAC właściwego i spróbuj jeszcze raz.` : '',
            h('div', 'card-actions', forget));
          onFinished(candidates);
        }
        return;
      }
      if (item.expired) {
        stopCountdown();
        show('red', `Agent ${agentName} nie dokończył parowania w 15 min (wyłączony, bez Bluetooth albo ze starszą wersją programu).`);
        onFinished([]);
        return;
      }
      if (progress.stage === 'scanning' && progress.until) {
        const until = new Date(progress.until);
        const left = h('div', 'small');
        const tick = () => {
          const seconds = Math.max(0, Math.round((until - Date.now()) / 1000));
          left.textContent = `Skan działa jeszcze ok. ${seconds} s. Czujnik nadaje jako niesparowany tylko ok. 20 s po włożeniu baterii.`;
        };
        tick();
        stopCountdown();
        countdown = window.setInterval(tick, 1000);
        show('amber', h('div', 'pair-now', 'Włóż teraz baterie do czujnika'), left);
        return;
      }
      if (progress.stage) {
        stopCountdown();
        show('grey', PAIR_STAGE[progress.stage] || progress.text || progress.stage);
        return;
      }
      if (item.takenBy) show('grey', `Agent ${item.takenBy} odebrał polecenie – za chwilę uruchomi skan. Jeszcze NIE wkładaj baterii.`);
    }, PAIR_POLL_MS);
  } catch (error) {
    stopCountdown();
    show('amber', `Nie udało się sprawdzić stanu parowania (${error.message}) – odśwież stronę.`);
    onFinished([]);
  }
}

function sensorsSection(flash) {
  const active = config.sensors.filter((s) => !s.archivedAt);
  const archived = config.sensors.filter((s) => s.archivedAt);
  const list = h('div', 'cards');
  list.append(...active.map((s) => sensorCard(s, flash)));
  if (active.length === 0) list.append(h('div', 'empty', 'Nie ma jeszcze czujników.'));
  const children = [flashBox(flash, 'sensors'), list, remotePairForm(), addSensorForm(flash), addFlowerCareForm(flash)];
  if (archived.length > 0) {
    const box = h('details', 'archive');
    if (flash && archived.some((s) => flash.key === `sensor:${s.id}`)) box.open = true;
    const archivedList = h('div', 'cards');
    archivedList.append(...archived.map((s) => sensorCard(s, flash)));
    box.append(h('summary', null, `Archiwum (${archived.length})`), archivedList);
    children.push(box);
  }
  return section('sensors', 'Czujniki', 'Id czujnika się nie zmienia; „usunięcie” to archiwum – historia odczytów zostaje.', ...children);
}

// ---- agenci ------------------------------------------------------------------------------------------------------

const ROLE_OPTIONS = [['collector', 'tylko zbiera odczyty'], ...[1, 2, 3, 4, 5, 6, 7, 8, 9].map((n) => [String(n),
  `zbiera i wysyła maile – priorytet ${n}${n === 1 ? ' (najważniejszy)' : ''}`])];

function roleValue(agent) {
  return agent && agent.canSendMail ? String(agent.senderPriority) : 'collector';
}

function readRole(element) {
  return element.value === 'collector' ? { canSendMail: false, priority: null } : { canSendMail: true, priority: Number(element.value) };
}

function codePanel(code, agentName) {
  const link = enrollmentLink(cloud.url, cloud.key, code.code);
  const panel = h('div', 'code-panel');
  panel.append(
    h('div', null, h('strong', null, `Kod rejestracji agenta ${agentName}`), ` – jednorazowy, ważny do ${when(code.expiresAt)}. Strona pokazuje go tylko teraz.`),
    h('div', 'code', code.code),
    h('div', 'small', 'Komputer albo Raspberry Pi: ', h('code', null, `glebosaurus enroll --code ${code.code}`)),
    h('div', 'small', 'Telefon (Android): w aplikacji Glebosaurus „Zeskanuj kod QR”:'),
    qrSvg(link, `Kod QR rejestracji agenta ${agentName}`),
    button('Schowaj kod', 'quiet small', () => panel.remove()));
  return panel;
}

function agentCard(agent, flash) {
  const key = `agent:${agent.name}`;
  const card = h('div', `card accent-${!agent.enabled ? 'grey' : agent.enrolled ? 'green' : 'amber'}`);
  card.id = key;
  const badges = h('div', 'badges');
  if (!agent.enabled) badges.append(badge('wyłączony', 'grey'));
  if (!agent.enrolled) badges.append(badge('bez rejestracji', 'amber'));
  if (agent.codeValidUntil) badges.append(badge(`kod ważny do ${when(agent.codeValidUntil)}`, 'grey'));
  card.append(flashBox(flash, key), h('div', 'sensor-name', agent.name));
  if (badges.childElementCount > 0) card.append(badges);
  card.append(kv([
    ['Rola', agent.canSendMail ? `zbiera i wysyła maile (priorytet ${agent.senderPriority})` : 'tylko zbiera odczyty'],
    ['Ostatnio', agent.lastSeenAt ? stamp(agent.lastSeenAt) : 'jeszcze nigdy'],
    ['Zarejestrowany', agent.enrolledAt ? when(agent.enrolledAt) : null],
    ['Program', agent.status ? [PLATFORMS[agent.status.platform] || agent.status.platform, agent.status.version].filter(Boolean).join(' ') : null]
  ]));
  if (flash && flash.key === key && flash.code) card.append(codePanel(flash.code, agent.name));

  const edit = h('details', 'editor');
  const role = select(ROLE_OPTIONS, roleValue(agent));
  const enabled = checkbox('Włączony (wyłączony agent nie może się synchronizować)', agent.enabled);
  edit.append(h('summary', null, 'Zmień rolę'), form([field('Rola', role, 'Maile wysyła jeden agent naraz: ważniejszy kandydat (niższy numer) przejmuje blokadę. Telefon nie wysyła maili.'), enabled.element],
    submitButton('Zapisz agenta'), async () => {
      const { canSendMail, priority } = readRole(role);
      await cloud.rpc('owner_agent_save', { p_name: agent.name, p_can_send_mail: canSendMail, p_sender_priority: priority, p_enabled: enabled.box.checked });
      await load();
      render({ key, text: 'Zapisano. Agent przejmie rolę przy najbliższej synchronizacji (ok. 1–2 min), bez ponownej rejestracji.' });
    }));

  const extra = h('div');
  const actions = h('div', 'card-actions',
    button('Kod rejestracji…', 'quiet small', () => {
      extra.replaceChildren(confirmBox(agent.enrolled
        ? `Nowy kod przyda się tylko przy przenosinach na inne urządzenie – rejestracja nim zastąpi obecny token agenta ${agent.name}. Utworzyć?`
        : `Utworzyć jednorazowy kod rejestracji (24 godz.) dla agenta ${agent.name}?`, 'Tak, utwórz kod', async () => {
        const code = await cloud.rpc('owner_agent_code', { p_name: agent.name });
        await load();
        render({ key, text: 'Kod utworzony.', code });
      }, () => extra.replaceChildren()));
    }),
    button('Odetnij…', 'quiet small', () => {
      extra.replaceChildren(confirmBox(`Odciąć agenta ${agent.name}? Token przestanie działać od razu, niewykorzystane kody przepadną. Jego czujniki przejmą inni po wygaśnięciu dzierżaw (do ok. 32 min), blokadę nadawcy – po ok. 7 min. Z powrotem: nowy kod i enroll.`,
        'Tak, odetnij', async () => {
          await cloud.rpc('owner_agent_revoke', { p_name: agent.name });
          await load();
          render({ key, text: 'Agent odcięty.' });
        }, () => extra.replaceChildren()));
    }));
  card.append(edit, actions, extra);
  return card;
}

function addAgentForm() {
  const details = h('details', 'editor add');
  const name = input('text', 'off', { pattern: '[a-z0-9][a-z0-9_\\-]{0,39}', placeholder: 'np. telefon-kuchnia' });
  const role = select(ROLE_OPTIONS, 'collector');
  details.append(h('summary', null, 'Nowy agent'), form([
    field('Nazwa', name, 'Małe litery, cyfry, „_” i „-”, do 40 znaków.'),
    field('Rola', role)
  ], submitButton('Utwórz i pokaż kod'), async () => {
    const agentName = name.value.trim();
    if (config.agents.some((a) => a.name === agentName)) throw new Error(`Agent ${agentName} już jest – kod utworzysz w jego karcie.`);
    const { canSendMail, priority } = readRole(role);
    await cloud.rpc('owner_agent_save', { p_name: agentName, p_can_send_mail: canSendMail, p_sender_priority: priority, p_enabled: true });
    const code = await cloud.rpc('owner_agent_code', { p_name: agentName });
    await load();
    render({ key: `agent:${agentName}`, text: 'Agent utworzony.', code });
  }));
  return details;
}

function agentsSection(flash) {
  const list = h('div', 'cards');
  list.append(...config.agents.map((a) => agentCard(a, flash)));
  if (config.agents.length === 0) list.append(h('div', 'empty', 'Nie ma jeszcze agentów.'));
  return section('agents', 'Agenci', 'Urządzenia, które czytają czujniki i wysyłają maile. Kod rejestracji jest jednorazowy.', list, addAgentForm());
}

// ---- strażnik ----------------------------------------------------------------------------------------------------

function watchdogSection(flash) {
  const url = input('url', 'off', { placeholder: 'https://hc-ping.com/…' });
  const status = config.healthchecksSetAt ? `ustawiony ${stamp(config.healthchecksSetAt)}` : 'nie ustawiony – nikt nie zauważy, że nadawca zamilkł';
  const remove = button('Usuń adres', 'quiet small', async () => {
    remove.disabled = true;
    await cloud.rpc('owner_secret_set', { p_name: 'healthchecks-ping-url', p_value: null });
    await load();
    render({ key: 'watchdog', text: 'Adres usunięty – strażnik nie dostaje już sygnału.' });
  });
  remove.hidden = !config.healthchecksSetAt;
  return section('watchdog', 'Strażnik (healthchecks.io)', 'Nadawca co 5 min wysyła sygnał, gdy wszystko działa; cisza ponad 30 min = mail od healthchecks.io (sprawdzenie „Simple”: Period 5 min, Grace 25 min).',
    flashBox(flash, 'watchdog'), form([field('Adres sygnału (Ping URL)', url, `Teraz: ${status}. Strona go nie pokaże – tylko zapisze.`)],
      submitButton('Ustaw adres'), async () => {
        await cloud.rpc('owner_secret_set', { p_name: 'healthchecks-ping-url', p_value: url.value });
        await load();
        render({ key: 'watchdog', text: 'Adres zapisany w chmurze (Vault). Nadawca wyśle sygnał w ciągu kilku minut.' });
      }), remove);
}

// ---- strona ------------------------------------------------------------------------------------------------------

function render(flash) {
  const toc = h('nav', 'toc small', ...[['settings', 'Odczyty i raporty'], ['mail', 'Poczta'], ['sensors', 'Czujniki'], ['agents', 'Agenci'], ['watchdog', 'Strażnik']]
    .map(([id, label]) => {
      const link = h('a', null, label);
      link.href = `#${id}`;
      return link;
    }));
  main.replaceChildren(
    h('h1', null, 'Konfiguracja'),
    h('p', 'muted small', `Wersja konfiguracji ${config.configVersion} · wczytana ${when(config.serverTime, true)}. Zmiany trafiają do agentów same.`),
    toc,
    settingsSection(flash), mailSection(flash), sensorsSection(flash), agentsSection(flash), watchdogSection(flash));
  banner('');
  const target = main.querySelector('[data-flash="1"]');
  if (target) target.scrollIntoView({ block: 'nearest' });
}

start(async (client, element) => {
  cloud = client;
  main = element;
  await load();
  render(null);
});
