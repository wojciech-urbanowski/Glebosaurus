namespace Glebosaurus.Core.Updates;

/// <summary>
/// Kiedy usługa sprawdza nowe wydania: przy starcie, potem codziennie o stałej porze czasu lokalnego (domyślnie 03:30 –
/// z dala od raportów 07:00 i 19:00), a po nieudanym sprawdzeniu (np. brak sieci zaraz po zalogowaniu) ponownie po
/// <paramref name="retryAfterFailure"/>, nie później jednak niż o stałej porze.
/// </summary>
public sealed class UpdateCheckSchedule(TimeOnly dailyAt, TimeSpan retryAfterFailure, TimeZoneInfo zone)
{
    public static readonly TimeOnly DefaultDailyAt = new(3, 30);
    public static readonly TimeSpan DefaultRetryAfterFailure = TimeSpan.FromMinutes(30);

    public TimeOnly DailyAt => dailyAt;

    /// <summary>Najbliższa stała pora ściśle po <paramref name="now"/>.</summary>
    public DateTimeOffset NextDaily(DateTimeOffset now)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var day = DateOnly.FromDateTime(localNow.DateTime);
        for (var i = 0; i < 3; i++, day = day.AddDays(1))
        {
            var candidate = At(day);
            if (candidate > now) return candidate;
        }
        return At(day);
    }

    public DateTimeOffset AfterFailure(DateTimeOffset now)
    {
        var retry = now + retryAfterFailure;
        var daily = NextDaily(now);
        return retry < daily ? retry : daily;
    }

    /// <summary>Stała pora danego dnia; gdy wypada w „dziurze” zmiany czasu na letni – godzinę później.</summary>
    private DateTimeOffset At(DateOnly day)
    {
        var local = day.ToDateTime(dailyAt, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }
}
