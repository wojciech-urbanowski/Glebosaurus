using Glebosaurus.Cloud.Admin;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Config;

namespace Glebosaurus.Tests;

/// <summary>Plik konfiguracji chmury (właściciela) i plan <c>cloud config</c>, który go zapisuje w chmurze (bez bazy).</summary>
public sealed class CloudSetupTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "glebosaurus-setup-" + Guid.NewGuid().ToString("N"));

    public CloudSetupTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private const string ValidSensor = """
        {
          "id": "salon-monstera",
          "name": "Monstera",
          "tuya": { "mac": "DC:23:51:CC:03:C9", "uuid": "07aaad78f40b0a74", "deviceId": "1234567890123456789012", "localKeyHex": "a1b2c3d4e5f6", "productId": "gvygg3m8" }
        }
        """;

    private const string KeyFile = """
        {
          "mac": "DC:23:51:CC:03:C9", "uuid": "07aaad78f40b0a74", "deviceId": "1234567890123456789012",
          "localKeyHex": "a1b2c3d4e5f6", "productId": "gvygg3m8", "bindingAttempted": true, "verified": true
        }
        """;

    private string Write(string json)
    {
        var path = Path.Combine(directory, CloudSetupLoader.DefaultFile);
        File.WriteAllText(path, json);
        return path;
    }

    private string WithSensors(string sensorsJson, string extra = "") => Write($$"""{ {{extra}} "sensors": [ {{sensorsJson}} ] }""");

    private static string Rejected(string path) => Assert.Throws<InvalidDataException>(() => CloudSetupLoader.Load(path)).Message;

    // ---- Plik ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void MinimalFileGetsDefaultsAndAcceptsCommentsAndTrailingCommas()
    {
        var path = Write($$"""
            {
              // komentarz
              "sensors": [ {{ValidSensor}}, ],
            }
            """);

        var setup = CloudSetupLoader.Load(path);

        var sensor = Assert.Single(setup.Sensors);
        Assert.Equal("Monstera", sensor.Name);
        Assert.Equal(25, sensor.WateringBelowPercent);
        Assert.Equal(125, setup.ReadIntervalMinutes);
        Assert.Equal(["07:00", "19:00"], setup.Reports.Times);
        Assert.Equal(7, setup.Logs.RetentionDays);
        Assert.Null(setup.Mail);
        Assert.Null(setup.TimeZone);
    }

    [Fact]
    public void FileWithoutSensors_ChangesOnlySettings()
    {
        var setup = CloudSetupLoader.Load(Write("""{ "timeZone": "Europe/Warsaw", "readIntervalMinutes": 60 }"""));

        Assert.Empty(setup.Sensors);
        Assert.Equal(60, setup.ReadIntervalMinutes);
    }

    [Fact]
    public void AgentFile_IsNotACloudSetup() =>
        Assert.Contains("plik agenta", Rejected(Write("""{ "cloud": { "url": "https://abcdefghijklmnopqrst.supabase.co", "publishableKey": "sb_publishable_x" } }""")));

    [Fact]
    public void NameDefaultsToIdAndMacIsNormalised()
    {
        var path = WithSensors("""{ "id": "s1", "tuya": { "mac": "dc-23-51-cc-03-c9", "uuid": "07aaad78f40b0a74", "deviceId": "x", "localKeyHex": "a1b2c3d4e5f6", "productId": "gvygg3m8" } }""");

        var sensor = Assert.Single(CloudSetupLoader.Load(path).Sensors);

        Assert.Equal("s1", sensor.Name);
        Assert.Equal("DC:23:51:CC:03:C9", sensor.Tuya.Mac);
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("")]
    [InlineData("Salon.Monstera")]
    [InlineData("-monstera")]
    public void SensorIdTheCloudWouldNotAcceptIsRejected(string id) =>
        Assert.Contains("id", Rejected(WithSensors(ValidSensor.Replace("salon-monstera", id))));

    [Fact]
    public void SensorWithoutMacIsRejected_TheCloudRecognisesSensorsByMac() =>
        Assert.Contains("MAC", Rejected(WithSensors(ValidSensor.Replace("\"mac\": \"DC:23:51:CC:03:C9\", ", ""))));

    [Fact]
    public void DuplicateSensorIdUuidOrMacIsRejected()
    {
        Assert.Contains("więcej niż raz", Rejected(WithSensors($"{ValidSensor}, {ValidSensor}")));

        var otherId = ValidSensor.Replace("salon-monstera", "inny");
        Assert.Contains("ten sam UUID", Rejected(WithSensors($"{ValidSensor}, {otherId}")));

        var otherUuid = otherId.Replace("07aaad78f40b0a74", "1111111111111111");
        Assert.Contains("adres MAC", Rejected(WithSensors($"{ValidSensor}, {otherUuid}")));
    }

    [Fact]
    public void TuyaFileIsLoadedRelativeToTheFile()
    {
        Directory.CreateDirectory(Path.Combine(directory, "klucze"));
        File.WriteAllText(Path.Combine(directory, "klucze", "monstera.credentials.json"), KeyFile);
        var path = WithSensors("""{ "id": "monstera", "name": "Monstera", "tuyaFile": "klucze/monstera.credentials.json" }""");

        var sensor = Assert.Single(CloudSetupLoader.Load(path).Sensors);

        Assert.Equal("07aaad78f40b0a74", sensor.Tuya.Uuid);
        Assert.Equal("gvygg3m8", sensor.Tuya.ProductId);
        Assert.Equal([0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6], sensor.Tuya.KeyBytes);
    }

    [Fact]
    public void TuyaFileProblemsNameTheSensorAndTheFile()
    {
        Assert.Contains("nie znaleziono pliku tuyaFile", Rejected(WithSensors("""{ "id": "monstera", "tuyaFile": "nie-ma.json" }""")));

        File.WriteAllText(Path.Combine(directory, "k.json"), KeyFile);
        Assert.Contains("nie oba naraz", Rejected(WithSensors("""{ "id": "monstera", "tuyaFile": "k.json", "tuya": { "uuid": "07aaad78f40b0a74" } }""")));

        File.WriteAllText(Path.Combine(directory, "zly.json"), "{ nie json");
        var message = Rejected(WithSensors("""{ "id": "monstera", "tuyaFile": "zly.json" }"""));
        Assert.Contains("monstera", message);
        Assert.Contains("zly.json", message);
    }

    [Fact]
    public void SensorWithoutAnyTuyaDataPointsToPairing() =>
        Assert.Contains("pair", Rejected(WithSensors("""{ "id": "monstera" }""")));

    [Fact]
    public void UnknownProductAndBrokenHysteresisAreRejected()
    {
        Assert.Contains("productId", Rejected(WithSensors(ValidSensor.Replace("gvygg3m8", "zzzzzzzz"))));
        Assert.Contains("wateringClearAbovePercent", Rejected(WithSensors(ValidSensor.Replace("\"name\": \"Monstera\",",
            "\"name\": \"Monstera\", \"wateringBelowPercent\": 40, \"wateringClearAbovePercent\": 30,"))));
    }

    [Fact]
    public void ReportTimesTimeZoneAndLogRetentionMustBeSensible()
    {
        Assert.Contains("HH:mm", Rejected(WithSensors(ValidSensor, """ "reports": { "times": ["7 rano"] }, """)));
        Assert.Contains("timeZone", Rejected(WithSensors(ValidSensor, """ "timeZone": "Mars/Olympus", """)));
        foreach (var days in new[] { 0, 31 })
            Assert.Contains("retentionDays", Rejected(WithSensors(ValidSensor, $$""" "logs": { "retentionDays": {{days}} }, """)));
    }

    [Fact]
    public void MailSectionNeedsPasswordAndRecipients()
    {
        Assert.Contains("appPassword", Rejected(WithSensors(ValidSensor, """ "mail": { "gmailAddress": "a@gmail.com", "alertRecipients": ["b@example.com"] }, """)));
        Assert.Contains("alertRecipients", Rejected(WithSensors(ValidSensor, """ "mail": { "gmailAddress": "a@gmail.com", "appPassword": "x" }, """)));
    }

    [Fact]
    public void RecipientsAreTrimmedDeduplicatedAndConfigurablePerKind()
    {
        var path = WithSensors(ValidSensor, """
            "mail": { "gmailAddress": " nadawca@gmail.com ", "appPassword": "x",
                      "alertRecipients": [" ja@example.com ", "JA@Example.COM", "druga@example.com"],
                      "reportRecipients": ["raporty@example.com"], "adminRecipients": [" admin@example.com ", "ADMIN@example.com"] },
            """);

        var mail = CloudSetupLoader.Load(path).Mail!;

        Assert.Equal("nadawca@gmail.com", mail.GmailAddress);
        Assert.Equal(["ja@example.com", "druga@example.com"], mail.AlertRecipients);
        Assert.Equal(["raporty@example.com"], mail.EffectiveReportRecipients);
        Assert.Equal(["admin@example.com"], mail.AdminRecipients);
    }

    [Theory]
    [InlineData("bez-malpy")]
    [InlineData("Jan Kowalski <jan@example.com>")]
    [InlineData("a@b")]
    [InlineData("a@@example.com")]
    [InlineData("")]
    public void RecipientsThatAreNotPlainEmailAddressesAreRejected(string address)
    {
        Assert.Contains("alertRecipients", Rejected(WithSensors(ValidSensor,
            $$""" "mail": { "gmailAddress": "a@gmail.com", "appPassword": "x", "alertRecipients": ["{{address}}"] }, """)));
        Assert.Contains("adminRecipients", Rejected(WithSensors(ValidSensor,
            $$""" "mail": { "gmailAddress": "a@gmail.com", "appPassword": "x", "alertRecipients": ["ok@example.com"], "adminRecipients": ["{{address}}"] }, """)));
    }

    [Fact]
    public void BrokenJsonGivesReadableError() =>
        Assert.Contains("Nieprawidłowy JSON", Rejected(Write("{ nie json")));

    // ---- Plan zapisu w chmurze -----------------------------------------------------------------------------------------

    private static CloudSetup Setup(params SensorConfig[] sensors) => new()
    {
        TimeZone = "Europe/Warsaw",
        Sensors = sensors.Length > 0 ? [.. sensors] : [Fixtures.Sensor("cisy", "Cisy")],
        Mail = new MailConfig
        {
            GmailAddress = "nadawca@gmail.com", AppPassword = "tajne haslo aplikacji",
            AlertRecipients = ["ja@example.com"], ReportRecipients = [], AdminRecipients = ["admin@example.com"]
        },
        Reports = new() { Times = ["07:00", "19:00"], CatchUpHours = 2 },
        Logs = new() { RetentionDays = 14 }
    };

    [Fact]
    public void Plan_TakesSettingsSensorsAndSecrets_WithoutShowingSecrets()
    {
        var sensor = Fixtures.Sensor("cisy", "Cisy");
        sensor.WateringBelowPercent = 30;
        sensor.WateringClearAbovePercent = 40;

        var plan = CloudSetupWriter.Plan(Setup(sensor), "https://hc-ping.com/sekret");

        Assert.Empty(plan.Problems);
        Assert.Equal("Europe/Warsaw", (string?)plan.Settings["timeZone"]);
        Assert.Equal(2, (double)plan.Settings["reportCatchUpHours"]!);
        Assert.Equal(14, (int)plan.Settings["logRetentionDays"]!);
        Assert.Equal("ja@example.com", (string?)plan.Settings["alertRecipients"]![0]);
        var saved = Assert.Single(plan.Sensors);
        Assert.Equal("DC:23:51:CC:03:C9", saved.Mac);
        Assert.Equal(30, (int)saved.Options["wateringBelowPercent"]!);
        Assert.Equal("a1b2c3d4e5f6", (string?)saved.Credentials["localKeyHex"]);
        Assert.Equal("DC:23:51:CC:03:C9", (string?)saved.Credentials["mac"]);
        Assert.False(saved.Credentials.ContainsKey("keyBytes"));
        Assert.True(plan.SetsGmailPassword);
        Assert.True(plan.SetsHealthchecksPingUrl);
        foreach (var text in new[] { plan.ToString(), saved.ToString() })
        {
            Assert.DoesNotContain("tajne haslo", text);
            Assert.DoesNotContain("a1b2c3d4e5f6", text);
            Assert.DoesNotContain("sekret", text);
        }
    }

    [Fact]
    public void Plan_WithoutTimeZone_TakesThisComputersZone_AndRefusesBadPingAddress()
    {
        var setup = Setup();
        setup.TimeZone = null;

        var plan = CloudSetupWriter.Plan(setup, "http://hc-ping.com/bez-tls");

        Assert.Equal(TimeZones.ToIana(TimeZoneInfo.Local), (string?)plan.Settings["timeZone"]);
        Assert.Contains(plan.Problems, p => p.Contains("https"));
    }

    [Fact]
    public void Plan_WithoutMailOrPing_KeepsThemUntouched()
    {
        var setup = Setup();
        setup.Mail = null;

        var plan = CloudSetupWriter.Plan(setup, null);

        Assert.False(plan.Settings.ContainsKey("gmailAddress"));
        Assert.False(plan.SetsGmailPassword);
        Assert.False(plan.SetsHealthchecksPingUrl);
        Assert.Contains(plan.Notes, n => n.Contains("„mail”"));
        Assert.Contains(plan.Notes, n => n.Contains("healthchecks"));
    }

    [Fact]
    public void TimeZone_IsTurnedIntoIanaName()
    {
        var warsaw = TimeZones.Resolve("Europe/Warsaw", out var exact);

        Assert.True(exact);
        Assert.Equal("Europe/Warsaw", TimeZones.ToIana(warsaw));
    }
}
