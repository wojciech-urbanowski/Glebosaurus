namespace Glebosaurus.Core.Domain;

/// <summary>Jeden udany odczyt czujnika. Brak wartości (null) oznacza, że czujnik jej nie zgłosił – nie zero.</summary>
public sealed record StoredReading(
    string SensorId, DateTimeOffset At, int? MoisturePercent, decimal? SoilTemperatureCelsius, int? BatteryPercent, int? BatteryState);

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
