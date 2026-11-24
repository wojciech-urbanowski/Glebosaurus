using Glebosaurus.Core.Config;
using Glebosaurus.Core.Domain;
using Glebosaurus.FlowerCare;

namespace Glebosaurus.Core.Monitoring;

/// <summary>Wynik sesji Flower Care jako wynik odczytu zbieracza – wspólne dla radia Shiny (Windows, Linux) i Androida.</summary>
public static class FlowerCareOutcome
{
    /// <summary>Limit historii na sesję (<see cref="FlowerCareHistoryOptions.Default"/>).</summary>
    public static FlowerCareHistoryOptions HistoryOptions => FlowerCareHistoryOptions.Default;

    /// <param name="connectedAt">Chwila połączenia – czas stanu bieżącego.</param>
    public static SensorReadOutcome From(SensorConfig sensor, FlowerCareResult result, DateTimeOffset connectedAt, int? signalDbm) =>
        new(sensor, Reading(connectedAt, result.Current, result.BatteryPercent), SensorReadFailure.None)
        {
            Earlier = result.History.Select(h => Reading(h.At, h.Values, null)).ToArray(),
            SignalDbm = signalDbm,
            Notes = result.Notes
        };

    public static SensorReading Reading(DateTimeOffset at, FlowerCareValues values, int? batteryPercent) =>
        new(at, values.MoisturePercent, values.TemperatureTenths / 10m, batteryPercent)
        {
            LightLux = values.LightLux, ConductivityMicroSiemens = values.ConductivityMicroSiemens
        };
}
