// Klient Supabase bez bibliotek (ARCHITEKTURA.md, zasada 10): Supabase Auth (hasło, TOTP) i funkcje SQL owner_* przez
// zwykły fetch. Adres projektu i klucz publikowalny przychodzą z odnośnika „cloud web-link” i leżą w localStorage; token
// odświeżania – w localStorage do wylogowania; token dostępu – tylko w pamięci. Odświeżanie idzie pod blokadą wspólną dla
// kart przeglądarki (Web Locks), bo Supabase wymienia token odświeżania przy każdym użyciu.

const PROJECT_KEY = 'glebosaurus.project';
const SESSION_KEY = 'glebosaurus.session';
const REFRESH_LOCK = 'glebosaurus.refresh';
const REFRESH_MARGIN_MS = 5 * 60 * 1000;

const PROJECT_URL = /^https:\/\/[a-z0-9]{20}\.supabase\.co$/;
const PUBLISHABLE_KEY = /^sb_publishable_[A-Za-z0-9_-]{1,200}$/;

/** Błąd z rodzajem do decyzji strony: login, mfa, notOwner, migration, network, invalid. */
export class CloudError extends Error {
  constructor(kind, message, status) {
    super(message);
    this.kind = kind;
    this.status = status;
  }
}

function readJson(key) {
  try {
    const text = localStorage.getItem(key);
    return text ? JSON.parse(text) : null;
  } catch {
    return null;
  }
}

function writeJson(key, value) {
  try {
    if (value === null) localStorage.removeItem(key);
    else localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // Przeglądarka bez localStorage (tryb prywatny z blokadą): sesja żyje tylko do zamknięcia karty.
  }
}

/** Adres projektu musi być projektem Supabase; na tym komputerze (podgląd z atrapą API) – także adres samej strony. */
export function validProject(url, key) {
  if (typeof url !== 'string' || typeof key !== 'string') return false;
  const local = ['localhost', '127.0.0.1', '[::1]'].includes(window.location.hostname) && url === window.location.origin;
  return (PROJECT_URL.test(url) || local) && PUBLISHABLE_KEY.test(key);
}

/** Projekt z fragmentu odnośnika (#url=…&key=…); null, gdy fragment go nie niesie. Rzuca przy złych danych. */
export function projectFromHash(hash) {
  const params = new URLSearchParams(hash.replace(/^#/, ''));
  if (!params.has('url') && !params.has('key')) return null;
  const url = (params.get('url') || '').trim().replace(/\/+$/, '');
  const key = (params.get('key') || '').trim();
  if (!validProject(url, key)) {
    throw new CloudError('invalid', 'Odnośnik nie wskazuje projektu Supabase z kluczem publikowalnym – wygeneruj go jeszcze raz: glebosaurus cloud web-link.');
  }
  return { url, key };
}

export function loadProject() {
  const project = readJson(PROJECT_KEY);
  return project && validProject(project.url, project.key) ? project : null;
}

/** Zapamiętuje projekt; inny projekt niż dotąd kasuje sesję (należała do tamtego). */
export function saveProject(project) {
  const previous = loadProject();
  if (previous && previous.url !== project.url) writeJson(SESSION_KEY, null);
  writeJson(PROJECT_KEY, project);
}

export function forgetProject() {
  writeJson(SESSION_KEY, null);
  writeJson(PROJECT_KEY, null);
}

/** Żądania tokenu (bez sprawdzania podpisu – to robi Supabase; tu tylko aal i czas). */
export function tokenClaims(token) {
  try {
    const part = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
    const padded = part + '='.repeat((4 - (part.length % 4)) % 4);
    const bytes = Uint8Array.from(atob(padded), (c) => c.charCodeAt(0));
    return JSON.parse(new TextDecoder().decode(bytes));
  } catch {
    return {};
  }
}

function withLock(fn) {
  return navigator.locks ? navigator.locks.request(REFRESH_LOCK, fn) : fn();
}

export class Cloud {
  constructor(project) {
    this.url = project.url;
    this.key = project.key;
    this.access = null;
    this.accessExpiresAt = 0;
  }

  /** Id projektu do pokazania (pierwszy człon adresu; podgląd na tym komputerze – sam adres). */
  get projectRef() {
    const host = new URL(this.url).host;
    return host.endsWith('.supabase.co') ? host.split('.')[0] : host;
  }

  get hasSession() {
    return Boolean(readJson(SESSION_KEY)?.refreshToken);
  }

  get email() {
    return readJson(SESSION_KEY)?.email || null;
  }

  /** aal tokenu dostępu: „aal1” po samym haśle, „aal2” po kodzie TOTP. */
  get aal() {
    return this.access ? tokenClaims(this.access).aal : null;
  }

  async request(path, { method = 'GET', body, auth = false } = {}) {
    const headers = { apikey: this.key };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (auth) headers.Authorization = `Bearer ${await this.accessToken()}`;
    let response;
    try {
      response = await fetch(this.url + path, {
        method, headers, body: body === undefined ? undefined : JSON.stringify(body), cache: 'no-store', credentials: 'omit',
        referrerPolicy: 'no-referrer'
      });
    } catch {
      throw new CloudError('network', 'Brak połączenia z Supabase (sieć albo projekt uśpiony).');
    }
    const text = await response.text();
    let data = null;
    try {
      data = text ? JSON.parse(text) : null;
    } catch {
      data = { message: text };
    }
    return { response, data };
  }

  setSession(data, email) {
    this.access = data.access_token;
    this.accessExpiresAt = (data.expires_at ? data.expires_at * 1000 : Date.now() + data.expires_in * 1000);
    writeJson(SESSION_KEY, { refreshToken: data.refresh_token, email: email ?? data.user?.email ?? this.email });
  }

  clearSession() {
    this.access = null;
    this.accessExpiresAt = 0;
    writeJson(SESSION_KEY, null);
  }

  async signIn(email, password) {
    const { response, data } = await this.request('/auth/v1/token?grant_type=password', { method: 'POST', body: { email, password } });
    if (!response.ok) {
      if (response.status === 400 || response.status === 401) throw new CloudError('login', 'Zły e-mail albo hasło.', response.status);
      if (response.status === 429) throw new CloudError('login', 'Za dużo prób logowania – spróbuj za kilka minut.', 429);
      throw new CloudError('network', authMessage(data, response.status), response.status);
    }
    this.setSession(data, email);
  }

  /** Ważny token dostępu; odświeża go (pod blokadą kart) przed wygaśnięciem. Bez sesji – CloudError „login”. */
  async accessToken(force = false) {
    if (!force && this.access && Date.now() < this.accessExpiresAt - REFRESH_MARGIN_MS) return this.access;
    await withLock(async () => {
      // Inna karta mogła już wymienić token odświeżania – zawsze bierzemy ostatni zapisany.
      const stored = readJson(SESSION_KEY);
      if (!stored?.refreshToken) throw new CloudError('login', 'Zaloguj się.');
      const { response, data } = await this.request('/auth/v1/token?grant_type=refresh_token', {
        method: 'POST', body: { refresh_token: stored.refreshToken }
      });
      if (!response.ok) {
        if (response.status >= 400 && response.status < 500) {
          this.clearSession();
          throw new CloudError('login', 'Sesja wygasła albo została zakończona – zaloguj się ponownie.', response.status);
        }
        throw new CloudError('network', authMessage(data, response.status), response.status);
      }
      this.setSession(data, stored.email);
    });
    return this.access;
  }

  /** Potwierdzone składniki TOTP konta. */
  async verifiedTotp() {
    const { response, data } = await this.request('/auth/v1/user', { auth: true });
    if (!response.ok) throw new CloudError('network', authMessage(data, response.status), response.status);
    return (data.factors || []).filter((f) => f.factor_type === 'totp' && f.status === 'verified');
  }

  /**
   * Nowy składnik TOTP: najpierw usuwa niedokończone (niepotwierdzone) – inaczej Supabase odmówi drugiego o tej samej
   * nazwie. Zwraca id, kod QR (obraz SVG jako data: URL) i sekret do wpisania ręcznie.
   */
  async enrollTotp() {
    const { response: userResponse, data: user } = await this.request('/auth/v1/user', { auth: true });
    if (!userResponse.ok) throw new CloudError('network', authMessage(user, userResponse.status), userResponse.status);
    for (const factor of (user.factors || []).filter((f) => f.status !== 'verified')) {
      await this.request(`/auth/v1/factors/${encodeURIComponent(factor.id)}`, { method: 'DELETE', auth: true });
    }
    const { response, data } = await this.request('/auth/v1/factors', {
      method: 'POST', auth: true, body: { factor_type: 'totp', friendly_name: 'Glebosaurus', issuer: 'Glebosaurus' }
    });
    if (!response.ok) throw new CloudError('network', authMessage(data, response.status), response.status);
    return { id: data.id, qrCode: data.totp.qr_code, secret: data.totp.secret };
  }

  /** Kod TOTP → sesja aal2 (nowe tokeny). */
  async verifyTotp(factorId, code) {
    const factor = encodeURIComponent(factorId);
    const { response: challengeResponse, data: challenge } = await this.request(`/auth/v1/factors/${factor}/challenge`, { method: 'POST', auth: true, body: {} });
    if (!challengeResponse.ok) throw new CloudError('network', authMessage(challenge, challengeResponse.status), challengeResponse.status);
    const { response, data } = await this.request(`/auth/v1/factors/${factor}/verify`, {
      method: 'POST', auth: true, body: { challenge_id: challenge.id, code }
    });
    if (!response.ok) {
      if (response.status === 422 || response.status === 400) throw new CloudError('mfa', 'Zły albo przeterminowany kod – wpisz bieżący kod z aplikacji.', response.status);
      if (response.status === 429) throw new CloudError('mfa', 'Za dużo prób – spróbuj za chwilę.', 429);
      throw new CloudError('network', authMessage(data, response.status), response.status);
    }
    this.setSession(data, this.email);
  }

  async signOut() {
    try {
      if (this.access) await this.request('/auth/v1/logout?scope=local', { method: 'POST', auth: true });
    } catch {
      // Wylogowanie lokalne i tak następuje niżej.
    }
    this.clearSession();
  }

  /** Funkcja SQL strony (public.owner_*). Wygasły token – jedno odświeżenie i ponowienie. */
  async rpc(name, args = {}) {
    for (let attempt = 0; ; attempt++) {
      const { response, data } = await this.request(`/rest/v1/rpc/${name}`, { method: 'POST', auth: true, body: args });
      if (response.ok) return data;
      const message = (data && data.message) || '';
      if (response.status === 401 && attempt === 0 && /JWT|expired/i.test(message + (data?.code || ''))) {
        await this.accessToken(true);
        continue;
      }
      if (message.includes('mfa_required')) throw new CloudError('mfa', 'Potrzebny kod TOTP.', response.status);
      if (message.includes('not_owner')) {
        throw new CloudError('notOwner', 'To konto nie jest właścicielem Glebosaurusa. Właściciela dodaje: glebosaurus cloud owner --admin PLIK --email ADRES --confirm.', response.status);
      }
      // Błędy z funkcji Glebosaurusa niosą powód po polsku; konflikt wersji (40001) – osobny rodzaj.
      if (message.startsWith('Glebosaurus: conflict')) {
        throw new CloudError('conflict', message.replace(/^Glebosaurus: conflict: ?/, ''), response.status);
      }
      if (message.startsWith('Glebosaurus: ')) throw new CloudError('invalid', message.slice('Glebosaurus: '.length), response.status);
      if (response.status === 404 && data?.code === 'PGRST202') {
        throw new CloudError('migration', `Baza nie ma funkcji ${name} – brakuje migracji (glebosaurus cloud migrate --admin PLIK --confirm).`, 404);
      }
      if (response.status === 400 || response.status === 409) {
        throw new CloudError('invalid', message.replace(/^Glebosaurus: /, '') || `Błąd ${response.status}`, response.status);
      }
      throw new CloudError('network', message ? `Supabase: ${message}` : `Supabase odpowiedział ${response.status}.`, response.status);
    }
  }
}

function authMessage(data, status) {
  const text = data && (data.msg || data.message || data.error_description || data.error);
  return text ? `Supabase Auth: ${text}` : `Supabase Auth odpowiedział ${status}.`;
}
