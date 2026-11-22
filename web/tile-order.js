// Kolejność kafelków pulpitu skróconego: od najsuchszej gleby, żeby czujniki do podlania (teraz albo wkrótce) były na
// początku. Czujnik bez odczytu – za tymi z wilgotnością, wyłączone – na końcu; przy równej wilgotności decyduje nazwa.
// Tę samą kolejność ma zestawienie w raporcie mailowym (MailTemplates). Pilnuje tests/Glebosaurus.Tests/TileOrderTests.cs.

function moisture(sensor) {
  const value = sensor.last?.moisture;
  return value === null || value === undefined ? Number.POSITIVE_INFINITY : value;
}

/** Nowa lista czujników ({ name, enabled, last: { moisture } }) w kolejności kafelków. */
export function tileOrder(sensors) {
  return [...sensors].sort((a, b) =>
    Number(!a.enabled) - Number(!b.enabled) || moisture(a) - moisture(b) || a.name.localeCompare(b.name, 'pl'));
}
