using System.Globalization;

namespace Glebosaurus.Core.Reports;

/// <param name="Key">Unikalny klucz wystąpienia raportu (godzina + data), do odnotowania wysyłki.</param>
public sealed record DueReport(string Key, DateTimeOffset ScheduledAt);

public static class ReportSchedule
{
    public static bool TryParseTime(string text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    /// <summary>
    /// Wystąpienia raportów, których pora już minęła, ale nie minęło jeszcze <paramref name="catchUp"/>.
    /// Uwzględnia też wczorajsze (raport o 23:30 nadrabiany po północy).
    /// </summary>
    public static IReadOnlyList<DueReport> Due(IEnumerable<string> times, DateTimeOffset now, TimeZoneInfo zone, TimeSpan catchUp)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var due = new List<DueReport>();
        foreach (var text in times)
        {
            if (!TryParseTime(text, out var time)) continue;
            foreach (var date in new[] { today.AddDays(-1), today })
            {
                var local = date.ToDateTime(time);
                var scheduled = new DateTimeOffset(local, zone.GetUtcOffset(local));
                if (scheduled <= now && now - scheduled < catchUp)
                    due.Add(new($"{time:HH\\:mm}|{date:yyyy-MM-dd}", scheduled));
            }
        }
        return due.OrderBy(d => d.ScheduledAt).ToList();
    }

    /// <summary>Najbliższa zaplanowana pora raportu po <paramref name="now"/> (dziś albo jutro); null, gdy raporty są wyłączone.</summary>
    public static DateTimeOffset? Next(IEnumerable<string> times, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        DateTimeOffset? next = null;
        foreach (var text in times)
        {
            if (!TryParseTime(text, out var time)) continue;
            foreach (var date in new[] { today, today.AddDays(1) })
            {
                var local = date.ToDateTime(time);
                var scheduled = new DateTimeOffset(local, zone.GetUtcOffset(local));
                if (scheduled > now && (next is null || scheduled < next)) next = scheduled;
            }
        }
        return next;
    }
}
