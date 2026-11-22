// Kolejność czujników na pulpitach (kafelki i karty): od najsuchszej gleby, żeby czujniki do podlania (teraz albo
// wkrótce) były na początku. Czujnik bez odczytu – za tymi z wilgotnością, wyłączone – na końcu; przy równej wilgotności
// decyduje nazwa. Tę samą kolejność ma zestawienie w raporcie mailowym (MailTemplates). Ten sam plik ma lokalny panel
// (src/Glebosaurus.Web/wwwroot) – test pilnuje, że kopie są identyczne. Reguły: tests/Glebosaurus.Tests/TileOrderTests.cs.

/**
 * Nowa lista czujników w kolejności pulpitu. moistureOf – wilgotność z ostatniego odczytu (strona w chmurze:
 * last.moisture – domyślnie; panel: latest.moisturePercent); enabled === false – czujnik wyłączony.
 */
export function tileOrder(sensors, moistureOf = (sensor) => sensor.last?.moisture) {
  const moisture = (sensor) => {
    const value = moistureOf(sensor);
    return value === null || value === undefined ? Number.POSITIVE_INFINITY : value;
  };
  return [...sensors].sort((a, b) =>
    Number(a.enabled === false) - Number(b.enabled === false) || moisture(a) - moisture(b) || a.name.localeCompare(b.name, 'pl'));
}
