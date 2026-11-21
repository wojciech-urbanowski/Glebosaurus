// Polecenia z przeglądarki (odczyt i raport na żądanie, próbny mail): zlecenie przez owner_command i śledzenie wyniku
// co 5 s przez owner_command_status – agent bierze polecenie przy synchronizacji (co ok. 90 s), wynik zgłasza sam.

const POLL_MS = 5000;

export const KIND_TEXT = { read_now: 'Odczyt', report_now: 'Raport', mail_test: 'Próbny mail', send_log: 'Dziennik' };

/** Zleca polecenie; zwraca { id, created } (created = false – takie samo zlecono w ostatniej minucie). */
export function command(cloud, kind, { sensor = null, agent = null } = {}) {
  return cloud.rpc('owner_command', { p_kind: kind, p_sensor: sensor, p_agent: agent });
}

/**
 * Śledzi polecenia, aż każde się skończy (wynik albo wygaśnięcie). onState(item) dostaje każdy nowy stan:
 * { id, kind, sensor, takenBy, doneAt, result, expired }.
 */
export async function follow(cloud, ids, onState) {
  let pending = [...ids];
  while (pending.length > 0) {
    await new Promise((resolve) => setTimeout(resolve, POLL_MS));
    const items = await cloud.rpc('owner_command_status', { p_ids: pending });
    for (const item of items) onState(item);
    pending = items.filter((i) => !i.doneAt && !i.expired).map((i) => i.id);
  }
}

/** Krótki opis stanu polecenia dla użytkownika i ton notki. */
export function describe(item) {
  const label = KIND_TEXT[item.kind] || item.kind;
  if (item.doneAt) {
    const result = item.result || {};
    if (result.ok) return { tone: 'green', text: `${label}: gotowe (agent ${item.takenBy}).` };
    return { tone: 'red', text: `${label}: nie wyszło – ${result.error || 'agent zgłosił błąd'}.` };
  }
  if (item.expired) return { tone: 'red', text: `${label}: żaden agent nie wykonał polecenia na czas (wyłączony albo ze starszą wersją programu).` };
  return { tone: 'grey', text: item.takenBy ? `${label}: agent ${item.takenBy} wykonuje…` : `${label}: czeka na agenta…` };
}
