// Szkielet każdej strony w chmurze: wyjście z cudzej ramki, projekt z odnośnika, logowanie hasłem i kodem TOTP (dodanie
// TOTP przy pierwszym logowaniu), sprawdzenie bazy (owner_info), nagłówek z nawigacją i wylogowaniem. Strona dostaje
// gotowego klienta po zalogowaniu właściciela z aal2.

import { $, h, syncClock } from './format.js';
import { Cloud, CloudError, forgetProject, loadProject, projectFromHash, saveProject } from './supabase.js';
import { errorBox, field, form, input, submitButton } from './ui.js';

/** Migracja, której potrzebuje ta wersja strony (najnowsza używana funkcja owner_*). */
export const REQUIRED_MIGRATION = '20261121220000';

const PAGES = [
  ['index.html', 'Pulpit'],
  ['alerts.html', 'Alerty'],
  ['config.html', 'Konfiguracja']
];

// Wyjście z ramki: GitHub Pages nie ustawia nagłówków, a frame-ancestors nie działa w <meta>. Strona w cudzej ramce
// zostaje niewidoczna (app.css), a próbuje przejść na wierzch.
function breakOutOfFrame() {
  if (window.top === window.self) {
    document.documentElement.classList.add('gl-ok');
    return true;
  }
  try {
    window.top.location.replace(window.self.location.href);
  } catch {
    // Przeglądarka nie pozwala – strona zostaje niewidoczna.
  }
  return false;
}

function view(...children) {
  const main = $('app');
  main.className = 'narrow';
  main.replaceChildren(...children);
  return main;
}

function header(cloud, info) {
  const nav = h('nav', 'tabs');
  const current = window.location.pathname.split('/').pop() || 'index.html';
  for (const [file, label] of PAGES) {
    const link = h('a', null, label);
    link.href = file;
    if (file === current) link.setAttribute('aria-current', 'page');
    nav.append(link);
  }
  const logout = h('button', 'quiet small', 'Wyloguj');
  logout.type = 'button';
  logout.addEventListener('click', async () => {
    logout.disabled = true;
    await cloud.signOut();
    window.location.reload();
  });
  const who = h('div', 'muted small', `${info.email} · projekt ${cloud.projectRef}${info.test ? ' (próbny)' : ''}`);
  const top = $('top');
  top.replaceChildren(h('div', 'top-row', h('div', null, h('div', 'brand', 'GLEBOSAURUS'), who), logout), nav);
  top.hidden = false;
}

// ---- widoki przed zalogowaniem -------------------------------------------------------------------------------

function connectView() {
  const link = input('url', 'off', { placeholder: 'https://…#url=…&key=…' });
  view(
    h('h1', null, 'Połącz z chmurą'),
    h('p', null, 'Ta strona nie zna jeszcze Twojego projektu Supabase. Otwórz odnośnik, który wypisuje polecenie ',
      h('code', null, 'glebosaurus cloud web-link --admin PLIK'), ' (albo zeskanuj jego kod QR telefonem), albo wklej go tutaj.'),
    form([field('Odnośnik', link)], submitButton('Połącz'), async () => {
      const hash = link.value.includes('#') ? link.value.slice(link.value.indexOf('#')) : '';
      const project = projectFromHash(hash);
      if (!project) throw new CloudError('invalid', 'W odnośniku brak adresu projektu i klucza (część po „#”).');
      saveProject(project);
      window.location.reload();
    }),
    h('p', 'muted small', 'Odnośnik nie zawiera sekretów: adres projektu i klucz publikowalny. Dostęp chroni hasło i kod TOTP.'));
}

function loginView(cloud, resolve) {
  const email = input('email', 'username', { value: cloud.email || '' });
  const password = input('password', 'current-password');
  const forget = h('button', 'quiet small', 'Inny projekt');
  forget.type = 'button';
  forget.addEventListener('click', () => {
    forgetProject();
    window.location.reload();
  });
  view(
    h('h1', null, 'Zaloguj się'),
    h('p', 'muted small', `Projekt ${cloud.projectRef}. Konto zakłada się w panelu Supabase, a właścicielem czyni je `,
      h('code', null, 'glebosaurus cloud owner'), '.'),
    form([field('E-mail', email), field('Hasło', password)], submitButton('Dalej'), async () => {
      await cloud.signIn(email.value.trim(), password.value);
      password.value = '';
      resolve();
    }),
    forget);
  (email.value ? password : email).focus();
}

function codeInput() {
  return input('text', 'one-time-code', { inputMode: 'numeric', pattern: '[0-9]{6}', maxLength: 6, placeholder: '123456' });
}

function totpView(cloud, factor, resolve) {
  const code = codeInput();
  view(
    h('h1', null, 'Kod z aplikacji'),
    h('p', null, 'Wpisz 6-cyfrowy kod „Glebosaurus” z aplikacji uwierzytelniającej.'),
    form([field('Kod TOTP', code)], submitButton('Zaloguj'), async () => {
      await cloud.verifyTotp(factor.id, code.value.trim());
      resolve();
    }),
    signOutLink(cloud));
  code.focus();
}

async function enrollView(cloud, resolve) {
  const enrollment = await cloud.enrollTotp();
  const code = codeInput();
  const qr = h('img', 'qr');
  qr.src = enrollment.qrCode;
  qr.alt = 'Kod QR do aplikacji uwierzytelniającej';
  view(
    h('h1', null, 'Drugi składnik logowania'),
    h('p', null, 'Właściciel loguje się hasłem i kodem z aplikacji uwierzytelniającej (np. Aegis, Google Authenticator, ' +
      '1Password). Zeskanuj kod QR w aplikacji, potem wpisz kod, który pokaże.'),
    qr,
    h('details', null, h('summary', null, 'Nie mogę zeskanować – pokaż klucz'), h('p', null, h('code', 'secret', enrollment.secret))),
    form([field('Kod TOTP', code)], submitButton('Potwierdź i zaloguj'), async () => {
      await cloud.verifyTotp(enrollment.id, code.value.trim());
      resolve();
    }),
    signOutLink(cloud));
}

function signOutLink(cloud) {
  const button = h('button', 'quiet small', 'Wyloguj');
  button.type = 'button';
  button.addEventListener('click', async () => {
    await cloud.signOut();
    window.location.reload();
  });
  return button;
}

function refOf(project) {
  const host = new URL(project.url).host;
  return host.endsWith('.supabase.co') ? host.split('.')[0] : host;
}

/** Pyta, czy przełączyć stronę na projekt z odnośnika; true – tak (sesja obecnego projektu zostanie zakończona). */
function confirmSwitch(stored, next) {
  return new Promise((resolve) => {
    const yes = h('button', null, `Przełącz na ${refOf(next)}`);
    yes.type = 'button';
    yes.addEventListener('click', () => resolve(true));
    const no = h('button', 'quiet', `Zostań przy ${refOf(stored)}`);
    no.type = 'button';
    no.addEventListener('click', () => {
      resolve(false);
      window.location.reload();
    });
    view(h('h1', null, 'Inny projekt'),
      h('p', null, `Odnośnik prowadzi do projektu Supabase ${refOf(next)}, a ta przeglądarka pamięta ${refOf(stored)}. ` +
        'Przełączenie wyloguje z obecnego projektu. Przełączaj tylko, jeśli odnośnik pochodzi od Ciebie (glebosaurus cloud web-link).'),
      h('div', 'confirm-actions', yes, no));
  });
}

function fatalView(title, text, cloud) {
  view(h('h1', null, title), errorBox(text), cloud && cloud.hasSession ? signOutLink(cloud) : null);
}

// ---- przebieg ----------------------------------------------------------------------------------------------

/** Doprowadza do sesji aal2: hasło, potem kod TOTP (albo dodanie TOTP). */
async function signInFlow(cloud) {
  for (;;) {
    if (!cloud.hasSession) {
      await new Promise((resolve) => loginView(cloud, resolve));
      continue;
    }
    await cloud.accessToken();
    if (cloud.aal === 'aal2') return;
    const factors = await cloud.verifiedTotp();
    await new Promise((resolve, reject) => {
      if (factors.length > 0) totpView(cloud, factors[0], resolve);
      else enrollView(cloud, resolve).catch(reject);
    });
  }
}

/**
 * Uruchamia stronę: po zalogowaniu właściciela woła render(cloud, main, info) i odświeża ją co 60 s, gdy karta jest
 * widoczna (render zwraca funkcję odświeżenia albo nic).
 */
export async function start(render) {
  if (!breakOutOfFrame()) return;

  let project;
  try {
    const fromLink = projectFromHash(window.location.hash);
    if (fromLink) {
      history.replaceState(null, '', window.location.pathname + window.location.search);
      const stored = loadProject();
      // Odnośnik do innego projektu niż zapamiętany: przełączenie kończy sesję, więc nie bez pytania (obcy odnośnik
      // mógłby wylogować i podsunąć logowanie do cudzego projektu).
      if (stored && (stored.url !== fromLink.url || stored.key !== fromLink.key)) {
        if (!(await confirmSwitch(stored, fromLink))) return;
      }
      saveProject(fromLink);
    }
    project = loadProject();
  } catch (error) {
    fatalView('Zły odnośnik', error.message);
    return;
  }
  if (!project) {
    connectView();
    return;
  }

  const cloud = new Cloud(project);
  for (let mfaRetries = 0; ; ) {
    try {
      await signInFlow(cloud);
      const info = await cloud.rpc('owner_info');
      syncClock(info.serverTime);
      if (!info.migrations.includes(REQUIRED_MIGRATION)) {
        fatalView('Baza wymaga migracji', `Ta wersja strony potrzebuje migracji ${REQUIRED_MIGRATION}. Właściciel: ` +
          'glebosaurus cloud migrate --admin PLIK --confirm (nowa wersja programu).', cloud);
        return;
      }
      header(cloud, info);
      const main = $('app');
      main.className = '';
      main.replaceChildren();
      const refresh = await render(cloud, main, info);
      if (typeof refresh === 'function') schedule(refresh);
      return;
    } catch (error) {
      if (error instanceof CloudError && error.kind === 'login') continue;   // sesja wygasła – formularz logowania
      if (error instanceof CloudError && error.kind === 'mfa' && mfaRetries++ < 2) {
        await cloud.accessToken(true).catch(() => cloud.clearSession());
        continue;
      }
      if (error instanceof CloudError && error.kind === 'notOwner') fatalView('Brak dostępu', error.message, cloud);
      else if (error instanceof CloudError && error.kind === 'migration') fatalView('Baza wymaga migracji', error.message, cloud);
      else fatalView('Nie udało się połączyć', error.message, cloud);
      return;
    }
  }
}

/** Odświeżanie co 60 s, tylko przy widocznej karcie; po powrocie do karty – od razu. */
function schedule(refresh) {
  let timer = null;
  let running = false;
  const tick = async () => {
    clearTimeout(timer);
    timer = null;
    if (document.visibilityState !== 'visible' || running) return;
    running = true;
    try {
      await refresh();
      banner('');
    } catch (error) {
      if (error instanceof CloudError && (error.kind === 'login' || error.kind === 'mfa')) {
        window.location.reload();
        return;
      }
      banner(`Odświeżenie nie udało się: ${error.message}`, 'amber');
    } finally {
      running = false;
    }
    timer = setTimeout(tick, 60_000);
  };
  timer = setTimeout(tick, 60_000);
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') tick();
  });
}

/** Baner nad treścią strony (np. błąd odświeżenia); pusty tekst – chowa. */
export function banner(text, tone = 'grey') {
  const element = $('banner');
  element.className = `banner tone-${tone}`;
  element.textContent = text;
  element.hidden = !text;
}
