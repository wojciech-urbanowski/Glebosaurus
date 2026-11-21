// Podgląd wysłanego maila (mail.html?key=…): treść z archiwum (owner_mail_body, HTML spakowany gzipem) rozpakowana w
// przeglądarce i pokazana w ramce z atrybutem sandbox (bez skryptów, osobne źródło – mail nie sięga do sesji strony).
// Ta strona ma własną CSP z 'unsafe-inline' tylko dla stylów, bo maile mają style w atrybutach (pod klientów poczty).

import { h, when, syncClock } from './format.js';
import { start } from './shell.js';

const key = new URLSearchParams(window.location.search).get('key');

async function gunzip(base64) {
  const bytes = Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));
  const stream = new Blob([bytes]).stream().pipeThrough(new DecompressionStream('gzip'));
  return new Response(stream).text();
}

start(async (cloud, main) => {
  const back = h('a', 'button-link small', '← Alerty i maile');
  back.href = 'alerts.html';
  if (!key) {
    main.append(back, h('div', 'empty', 'Brak wskazania maila.'));
    return undefined;
  }
  const mail = await cloud.rpc('owner_mail_body', { p_key: key });
  syncClock(mail.sentAt);
  const html = await gunzip(mail.htmlGzip);
  const frame = h('iframe', 'mail-frame');
  frame.setAttribute('sandbox', '');
  frame.setAttribute('referrerpolicy', 'no-referrer');
  frame.title = `Treść maila: ${mail.subject}`;
  frame.srcdoc = html;
  document.title = `${mail.subject} – Glebosaurus`;
  main.append(back, h('h1', null, mail.subject),
    h('div', 'muted small', `Wysłany ${when(mail.sentAt)} do: ${mail.recipients.join(', ')}`), frame);
  return undefined;
});
