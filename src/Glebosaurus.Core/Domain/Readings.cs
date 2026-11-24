using Glebosaurus.Tuya;

namespace Glebosaurus.Core.Domain;

/// <summary>Jeden udany odczyt czujnika. Brak wartości (null) oznacza, że czujnik jej nie zgłosił – nie zero.</summary>
/// <param name="SoilTemperatureCelsius">Temperatura z czujnika: gleby (Tuya) albo przy roślinie – w główce nad ziemią (Flower Care).</param>
public sealed record StoredReading(
    string SensorId, DateTimeOffset At, int? MoisturePercent, decimal? SoilTemperatureCelsius, int? BatteryPercent, int? BatteryState)
{
    /// <summary>Natężenie światła w luksach (Flower Care).</summary>
    public int? LightLux { get; init; }

    /// <summary>Przewodność gleby – „żyzność” – w µS/cm (Flower Care).</summary>
    public int? ConductivityMicroSiemens { get; init; }
}

/// <summary>
/// Pomiar z sesji z czujnikiem, niezależny od jego rodzaju (Tuya – <see cref="SoilReading"/>, Flower Care). Czas to chwila
/// pomiaru według czujnika (zaległe pomiary) albo chwila połączenia (stan bieżący).
/// </summary>
/// <param name="TemperatureCelsius">Temperatura gleby (Tuya) albo przy roślinie (Flower Care).</param>
/// <param name="BatteryState">Stan baterii z DP14 czujnika Tuya (0 niski, 1 średni, 2 wysoki).</param>
public sealed record SensorReading(DateTimeOffset At, int? MoisturePercent, decimal? TemperatureCelsius, int? BatteryPercent, int? BatteryState = null)
{
    /// <summary>Natężenie światła w luksach (Flower Care).</summary>
    public int? LightLux { get; init; }

    /// <summary>Przewodność gleby – „żyzność” – w µS/cm (Flower Care).</summary>
    public int? ConductivityMicroSiemens { get; init; }

    public bool HasSoilData => MoisturePercent is not null || TemperatureCelsius is not null;

    public static SensorReading From(SoilReading reading) =>
        new(reading.At, reading.MoisturePercent, reading.SoilTemperatureCelsius, reading.BatteryPercent, reading.BatteryState);

    public StoredReading ToStored(string sensorId, DateTimeOffset at) =>
        new(sensorId, at, MoisturePercent, TemperatureCelsius, BatteryPercent, BatteryState)
        {
            LightLux = LightLux, ConductivityMicroSiemens = ConductivityMicroSiemens
        };
}

public enum AlertKind
{
    /// <summary>Wilgotność gleby poniżej progu przez kilka kolejnych odczytów.</summary>
    NeedsWater,

    /// <summary>Niski poziom baterii czujnika.</summary>
    LowBattery,

    /// <summary>Od dawna brak udanego odczytu (zasięg, bateria, czujnik wymaga ponownego sparowania).</summary>
    SensorSilent
}

/// <param name="ResolvedAt">null = alert jest otwarty.</param>
/// <param name="LastNotifiedAt">null = nikt jeszcze nie dostał maila o tym alercie.</param>
public sealed record AlertRecord(
    long Id, string SensorId, AlertKind Kind, string Detail, DateTimeOffset OpenedAt, DateTimeOffset? LastNotifiedAt, DateTimeOffset? ResolvedAt);
