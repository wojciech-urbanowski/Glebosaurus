using Glebosaurus.Cloud.Admin;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Config;

namespace Glebosaurus.Tests;

/// <summary>Plan <c>cloud import</c> – przeniesienie konfiguracji trybu samodzielnego do chmury (bez bazy).</summary>
public sealed class CloudImportTests
{
    private static AgentConfig Standalone(params SensorConfig[] sensors)
    {
        var config = Fixtures.Config(sensors.Length > 0 ? sensors : [Fixtures.Sensor("cisy", "Cisy")]);
        config.Mail = new MailConfig
        {
            GmailAddress = "nadawca@gmail.com", AppPassword = "tajne haslo aplikacji",
            AlertRecipients = ["ja@example.com"], ReportRecipients = [], AdminRecipients = ["admin@example.com"]
        };
        config.Reports = new() { Times = ["07:00", "19:00"], CatchUpHours = 2 };
        return config;
    }

    [Fact]
    public void Plan_TakesSettingsSensorsAndSecrets_WithoutShowingSecrets()
    {
        var sensor = Fixtures.Sensor("cisy", "Cisy");
        sensor.Tuya.Mac = "dc-23-51-cc-03-c9";
        sensor.WateringBelowPercent = 30;
        sensor.WateringClearAbovePercent = 40;

        var plan = CloudImporter.Plan(Standalone(sensor), "https://hc-ping.com/sekret", "Europe/Warsaw");

        Assert.Empty(plan.Problems);
        Assert.Equal("Europe/Warsaw", (string?)plan.Settings["timeZone"]);
        Assert.Equal(2, (double)plan.Settings["reportCatchUpHours"]!);
        Assert.Equal("ja@example.com", (string?)plan.Settings["alertRecipients"]![0]);
        var imported = Assert.Single(plan.Sensors);
        Assert.Equal("DC:23:51:CC:03:C9", imported.Mac);
        Assert.Equal(30, (int)imported.Options["wateringBelowPercent"]!);
        Assert.Equal("a1b2c3d4e5f6", (string?)imported.Credentials["localKeyHex"]);
        Assert.Equal("DC:23:51:CC:03:C9", (string?)imported.Credentials["mac"]);
        Assert.False(imported.Credentials.ContainsKey("keyBytes"));
        Assert.True(plan.SetsGmailPassword);
        Assert.True(plan.SetsHealthchecksPingUrl);
        foreach (var text in new[] { plan.ToString(), imported.ToString() })
        {
            Assert.DoesNotContain("tajne haslo", text);
            Assert.DoesNotContain("a1b2c3d4e5f6", text);
            Assert.DoesNotContain("sekret", text);
        }
    }

    [Fact]
    public void Plan_RefusesWhatCloudWouldNotAccept()
    {
        var badId = Fixtures.Sensor("Salon.Monstera", "Monstera", "07aaad78f40b0a75");
        var noMac = Fixtures.Sensor("fikus", "Fikus", "07aaad78f40b0a76");
        noMac.Tuya.Mac = "";

        var plan = CloudImporter.Plan(Standalone(badId, noMac), null, "Mars/Olympus");

        Assert.Equal(3, plan.Problems.Count);
        Assert.Empty(plan.Sensors);
        Assert.Contains(plan.Notes, n => n.Contains("healthchecks"));
    }

    [Fact]
    public void Plan_OfCloudModeConfig_IsRefused_AndLongLogRetentionIsCapped()
    {
        var config = Standalone();
        config.Logs.RetentionDays = 90;
        config.Cloud = new CloudAccessConfig { Url = "https://abcdefghijklmnopqrst.supabase.co", PublishableKey = "sb_publishable_x" };

        var plan = CloudImporter.Plan(config, null, "UTC");

        Assert.Single(plan.Problems);
        Assert.Equal(30, (int)plan.Settings["logRetentionDays"]!);
    }

    [Fact]
    public void Plan_WithoutMail_KeepsMailSettingsUntouched()
    {
        var config = Standalone();
        config.Mail = null;

        var plan = CloudImporter.Plan(config, null, "Europe/Warsaw");

        Assert.False(plan.Settings.ContainsKey("gmailAddress"));
        Assert.False(plan.SetsGmailPassword);
        Assert.Contains(plan.Notes, n => n.Contains("„mail”"));
    }

    [Fact]
    public void TimeZone_IsTurnedIntoIanaName()
    {
        var warsaw = TimeZones.Resolve("Europe/Warsaw", out var exact);

        Assert.True(exact);
        Assert.Equal("Europe/Warsaw", TimeZones.ToIana(warsaw));
    }
}
