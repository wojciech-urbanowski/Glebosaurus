// Kod QR jako SVG (bez HTML w napisach): biblioteka qrcode-generator (MIT, web/vendor/qrcode.js – THIRD-PARTY-NOTICES.md)
// liczy moduły, a rysunek składa się tu z jednej ścieżki. Używany do odnośnika rejestracji telefonu (glebosaurus://enroll)
// i do dodania TOTP przy pierwszym logowaniu (otpauth://).

import qrcode from './vendor/qrcode.js';

const SVG = 'http://www.w3.org/2000/svg';

export function qrSvg(text, label) {
  const code = qrcode(0, 'M');
  code.addData(text, 'Byte');
  code.make();
  const count = code.getModuleCount();
  const margin = 4;
  const size = count + margin * 2;
  let path = '';
  for (let row = 0; row < count; row++) {
    for (let col = 0; col < count; col++) {
      if (code.isDark(row, col)) path += `M${col + margin} ${row + margin}h1v1h-1z`;
    }
  }
  const svg = document.createElementNS(SVG, 'svg');
  svg.setAttribute('viewBox', `0 0 ${size} ${size}`);
  svg.setAttribute('class', 'qr');
  svg.setAttribute('role', 'img');
  svg.setAttribute('aria-label', label);
  svg.setAttribute('shape-rendering', 'crispEdges');
  const background = document.createElementNS(SVG, 'rect');
  background.setAttribute('width', String(size));
  background.setAttribute('height', String(size));
  background.setAttribute('fill', '#ffffff');
  const modules = document.createElementNS(SVG, 'path');
  modules.setAttribute('d', path);
  modules.setAttribute('fill', '#000000');
  svg.append(background, modules);
  return svg;
}

/** Odnośnik rejestracji telefonu – ten sam format co EnrollmentLink w Core (aplikacja Android go czyta). */
export function enrollmentLink(projectUrl, publishableKey, code) {
  return `glebosaurus://enroll?url=${encodeURIComponent(projectUrl)}&key=${encodeURIComponent(publishableKey)}&code=${encodeURIComponent(code)}`;
}
