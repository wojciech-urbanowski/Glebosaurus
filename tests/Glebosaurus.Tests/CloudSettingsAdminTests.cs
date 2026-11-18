using Glebosaurus.Cloud.Admin;

namespace Glebosaurus.Tests;

/// <summary><c>cloud settings</c> bez bazy: uwagi przed zmianą odstępu odczytów i zapis odstępu.</summary>
public sealed class CloudSettingsAdminTests
{
    private static CloudSettingsInfo Settings(params CloudSensorTiming[] sensors) =>
        new("Europe/Warsaw", 15, 60, ["07:00", "19:00"], 24, 7, 42, sensors);

    [Fact]
    public void Notes_ForTypicalSensors_AreEmpty()
    {
        var current = Settings(new("cisy", "Cisy", null, 6), new("hortensje", "Hortensje", null, 6));

        Assert.Empty(CloudSettingsAdmin.ReadIntervalNotes(current, 125));
    }

    [Fact]
    public void Notes_NameSensorsWithOwnInterval_AsUnchanged()
    {
        var current = Settings(new("cisy", "Cisy", 30, 6), new("hortensje", "Hortensje", null, 6));

        var note = Assert.Single(CloudSettingsAdmin.ReadIntervalNotes(current, 125));
        Assert.Contains("cisy", note);
        Assert.Contains("własny odstęp 30 min", note);
    }

    [Fact]
    public void Notes_WarnWhenSilentAlertWouldComeBeforeNextRead()
    {
        // Próg 2 h przy odstępie 2 h 5 min: alert „brak odczytów” między każdymi dwoma udanymi odczytami.
        var current = Settings(new("cisy", "Cisy", null, 2), new("hortensje", "Hortensje", null, 6));

        var note = Assert.Single(CloudSettingsAdmin.ReadIntervalNotes(current, 125));
        Assert.Contains("cisy", note);
        Assert.Contains("brak odczytów", note);
    }

    [Fact]
    public void Notes_WarnAboutBatteryBelowFifteenMinutes()
    {
        var note = Assert.Single(CloudSettingsAdmin.ReadIntervalNotes(Settings(new CloudSensorTiming("cisy", "Cisy", null, 6)), 5));

        Assert.Contains("baterii", note);
    }

    [Theory]
    [InlineData(15, "15 min")]
    [InlineData(0.5, "0.5 min")]
    [InlineData(60, "1 h")]
    [InlineData(125, "2 h 5 min")]
    [InlineData(1440, "24 h")]
    public void Minutes_AreReadable(double minutes, string expected) => Assert.Equal(expected, CloudSettingsAdmin.Minutes(minutes));

    [Theory]
    [InlineData(0.4)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public async Task SetReadInterval_RejectsValuesTheCloudWouldRefuse(double minutes)
    {
        // Sprawdzenie przed połączeniem – źródło danych nie jest otwierane.
        await using var data = Npgsql.NpgsqlDataSource.Create("Host=nie-ma-takiego-hosta.invalid");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            CloudSettingsAdmin.SetReadIntervalAsync(data, minutes, TestContext.Current.CancellationToken));
    }
}
