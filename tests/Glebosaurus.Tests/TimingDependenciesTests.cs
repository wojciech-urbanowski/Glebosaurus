using System.Text.RegularExpressions;
using Glebosaurus.Core.Alerts;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Config;

namespace Glebosaurus.Tests;

/// <summary>
/// Zależności między odstępami agentów i chmury (ARCHITEKTURA.md, „Zależności czasów”): po zmianie jednego odstępu test
/// mówi, którą zależność łamie. Wartości po stronie chmury są czytane z migracji (ostatnia definicja wygrywa).
/// </summary>
public sealed partial class TimingDependenciesTests
{
    private static readonly CollectorOptions Collector = new();
    private static readonly CloudSyncOptions Sync = new();
    private static readonly ReadingMirrorOptions Mirror = new();
    private static readonly NotifierOptions Notifier = new();
    private static readonly SensorLeaseOptions SensorLease = new();
    private static readonly NotifierLockOptions NotifierLock = new();

    /// <summary>Nasłuch rusza w pierwszym obrocie pętli po upływie <c>HearEvery</c> – najdłuższy odstęp między nasłuchami.</summary>
    private static TimeSpan HearingSpacing => Collector.HearEvery + Collector.TickInterval;

    [Fact]
    public void HeardWithin_SpansTwoHearings_SoOneMissedHearingDoesNotResetTheWait()
    {
        Assert.True(2 * HearingSpacing <= SensorLease.HeardWithin, $"2 × {HearingSpacing} > {SensorLease.HeardWithin}");
    }

    [Fact]
    public void CloudSightingsWindow_CoversHeardWithin_AndTwoHearings()
    {
        var window = Minutes(LastMigrationWith("function public.agent_sync"), @"g\.heard_at > v_now - interval '(\d+) minutes'");
        Assert.True(window >= SensorLease.HeardWithin);
        Assert.True(window >= 2 * HearingSpacing);
        Assert.True(window >= SensorLease.BetterFor);   // „inny słyszy lepiej przez 10 min” wymaga jego świeżych słyszeń
    }

    [Fact]
    public void Presence_SurvivesOneMissedSync()
    {
        Assert.True(2 * Sync.Interval < NotifierLock.AliveWithin, $"2 × {Sync.Interval} >= {NotifierLock.AliveWithin}");
    }

    [Fact]
    public void NotifierLock_AfterOneMissedSync_StillLeavesTimeForAMail()
    {
        var (notifierTtl, _) = LeaseTtls();
        Assert.True(notifierTtl - 2 * Sync.Interval >= NotifierLock.MinimumForMail);
        // Kandydat wraca do obecności przy każdej synchronizacji – oddanie blokady ważniejszemu nie zależy od pojedynczej.
        Assert.True(NotifierLock.HandoverAfter >= Sync.Interval);
    }

    [Fact]
    public void SensorLease_IsRenewedManyTimesBeforeItEnds()
    {
        var (_, sensorTtl) = LeaseTtls();
        Assert.Equal(TimeSpan.FromMinutes(30), sensorTtl);   // bez zmian (decyzja 2026-11-20)
        Assert.True(sensorTtl - 2 * Sync.Interval >= SensorLease.MinimumForRead + TimeSpan.FromSeconds(new AgentConfig().ScanWindowSeconds));
    }

    [Fact]
    public void AlertEvaluation_StaysWellBelowThePauseThatStartsTheGracePeriod()
    {
        Assert.True(Notifier.AlertEvery + Notifier.TickInterval < SilenceGrace.DefaultPauseAfter);
        Assert.True(Notifier.AlertEvery < Notifier.IncrementalOverlap);
        Assert.Equal(TimeSpan.FromMinutes(30), SilenceGrace.DefaultGrace);
    }

    [Fact]
    public void Heartbeat_MatchesHealthchecks_AndSyncFitsItsFreshnessCheck()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), Notifier.HeartbeatEvery);   // Period 5 min w healthchecks.io
        Assert.True(Notifier.HeartbeatEvery + Notifier.TickInterval < TimeSpan.FromMinutes(30));   // Period + Grace
        Assert.True(Sync.Interval < Notifier.HeartbeatSyncedWithin);
    }

    [Fact]
    public void SilentAgent_IsFarBeyondMissedSyncs()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), Notifier.AgentSilentAfter);
        Assert.True(Notifier.AgentSilentAfter >= 10 * Sync.Interval);
    }

    [Fact]
    public void ReadOnRequest_WaitsLongEnoughInThePanel_ForAReadingFromAnotherAgent()
    {
        // polecenie przy synchronizacji właściciela → nasłuch i odczyt → wysłanie → wiersz „ustalony” (30 s) → kopia odczytów
        var worst = Sync.Interval + TimeSpan.FromSeconds(new AgentConfig().ScanWindowSeconds) + TimeSpan.FromSeconds(20)
                    + TimeSpan.FromSeconds(30) + Mirror.Interval;
        Assert.True(CloudPanel.ReadPending >= worst, $"{CloudPanel.ReadPending} < {worst}");
    }

    [Fact]
    public void Retries_GrowAndNeverExceedTenMinutes()
    {
        Assert.Equal([TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)], Collector.RetryDelays);
        Assert.True(Collector.IncompleteRetryDelay >= TimeSpan.FromMinutes(2));
    }

    private static (TimeSpan Notifier, TimeSpan Sensor) LeaseTtls()
    {
        var match = TtlPattern().Match(LastMigrationWith("function glebosaurus.lease_ttl"));
        Assert.True(match.Success, "Nie znaleziono lease_ttl w migracjach.");
        return (TimeSpan.FromMinutes(int.Parse(match.Groups[1].Value)), TimeSpan.FromMinutes(int.Parse(match.Groups[2].Value)));
    }

    private static TimeSpan Minutes(string sql, string pattern)
    {
        var match = Regex.Match(sql, pattern);
        Assert.True(match.Success, $"Nie znaleziono „{pattern}” w migracji.");
        return TimeSpan.FromMinutes(int.Parse(match.Groups[1].Value));
    }

    /// <summary>Treść ostatniej (w kolejności stosowania) migracji, która definiuje daną funkcję.</summary>
    private static string LastMigrationWith(string marker)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var migrations = Path.Combine(directory.FullName, "supabase", "migrations");
            if (!File.Exists(Path.Combine(directory.FullName, "Glebosaurus.slnx")) || !Directory.Exists(migrations)) continue;
            return Directory.GetFiles(migrations, "*.sql").Order(StringComparer.Ordinal).Select(File.ReadAllText)
                .Last(text => Regex.IsMatch(text, @"create (or replace )?" + Regex.Escape(marker)));
        }
        throw new DirectoryNotFoundException("Nie znaleziono katalogu repozytorium (Glebosaurus.slnx).");
    }

    [GeneratedRegex(@"when p_resource = 'notifier' then interval '(\d+) minutes' else interval '(\d+) minutes'")]
    private static partial Regex TtlPattern();
}
