using Glebosaurus.Core.Config;

namespace Glebosaurus.Tests;

public class ConfigTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "glebosaurus-config-" + Guid.NewGuid().ToString("N"));

    public ConfigTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private const string ValidSensor = """
        {
          "id": "salon-monstera",
          "name": "Monstera",
          "tuya": { "mac": "DC:23:51:CC:03:C9", "uuid": "07aaad78f40b0a74", "deviceId": "1234567890123456789012", "localKeyHex": "a1b2c3d4e5f6", "productId": "gvygg3m8" }
        }
        """;

    private string Write(string json)
    {
        var path = Path.Combine(directory, "glebosaurus.config.json");
        File.WriteAllText(path, json);
        return path;
    }

    private string Load(string sensorsJson, string extra = "") => Write($$"""{ {{extra}} "sensors": [ {{sensorsJson}} ] }""");

    [Fact]
    public void MinimalConfigGetsDefaultsAndAcceptsCommentsAndTrailingCommas()
    {
        var path = Write($$"""
            {
              // komentarz
              "sensors": [ {{ValidSensor}}, ],
            }
            """);

        var config = AgentConfigLoader.Load(path);

        var sensor = Assert.Single(config.Sensors);
        Assert.Equal("Monstera", sensor.Name);
        Assert.Equal(25, sensor.WateringBelowPercent);
        Assert.Equal(125, config.ReadIntervalMinutes);
        Assert.Equal(["07:00", "19:00"], config.Reports.Times);
        Assert.Null(config.Mail);
    }

    [Fact]
    public void NameDefaultsToIdAndDatabasePathIsRelativeToConfigFile()
    {
        var path = Load("""{ "id": "s1", "tuya": { "uuid": "07aaad78f40b0a74", "deviceId": "x", "localKeyHex": "a1b2c3d4e5f6", "productId": "gvygg3m8" } }""",
            extra: """ "database": "dane/baza.db", """);

        var config = AgentConfigLoader.Load(path);

        Assert.Equal("s1", config.Sensors[0].Name);
        Assert.Equal(Path.Combine(directory, "dane", "baza.db"), AgentConfigLoader.ResolveDatabasePath(config, path));
    }

    [Theory]
    [InlineData("""{ "id": "a b", "tuya": {} }""", "id")]
    [InlineData("""{ "id": "", "tuya": {} }""", "id")]
    public void InvalidSensorIdIsRejected(string sensor, string expected) =>
        Assert.Contains(expected, Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(Load(sensor))).Message);

    [Fact]
    public void DuplicateSensorIdOrUuidIsRejected()
    {
        var path = Load($"{ValidSensor}, {ValidSensor}");

        Assert.Contains("więcej niż raz", Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(path)).Message);

        var otherId = ValidSensor.Replace("salon-monstera", "inny");
        Assert.Contains("ten sam UUID", Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(Load($"{ValidSensor}, {otherId}"))).Message);
    }

    private const string KeyFile = """
        {
          "mac": "DC:23:51:CC:03:C9", "uuid": "07aaad78f40b0a74", "deviceId": "1234567890123456789012",
          "localKeyHex": "a1b2c3d4e5f6", "productId": "gvygg3m8", "bindingAttempted": true, "verified": true
        }
        """;

    [Fact]
    public void TuyaFileIsLoadedRelativeToTheConfigDirectory()
    {
        Directory.CreateDirectory(Path.Combine(directory, "klucze"));
        File.WriteAllText(Path.Combine(directory, "klucze", "monstera.credentials.json"), KeyFile);
        var path = Load("""{ "id": "monstera", "name": "Monstera", "tuyaFile": "klucze/monstera.credentials.json" }""");

        var sensor = Assert.Single(AgentConfigLoader.Load(path).Sensors);

        Assert.Equal("07aaad78f40b0a74", sensor.Tuya.Uuid);
        Assert.Equal("gvygg3m8", sensor.Tuya.ProductId);
        Assert.Equal([0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6], sensor.Tuya.KeyBytes);
    }

    [Fact]
    public void MissingTuyaFileIsReported()
    {
        var path = Load("""{ "id": "monstera", "tuyaFile": "nie-ma.json" }""");

        Assert.Contains("nie znaleziono pliku tuyaFile", Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(path)).Message);
    }

    [Fact]
    public void TuyaFileAndInlineTuyaCannotBeCombined()
    {
        File.WriteAllText(Path.Combine(directory, "k.json"), KeyFile);
        var path = Load("""{ "id": "monstera", "tuyaFile": "k.json", "tuya": { "uuid": "07aaad78f40b0a74" } }""");

        Assert.Contains("nie oba naraz", Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(path)).Message);
    }

    [Fact]
    public void BrokenTuyaFileNamesTheSensorAndTheFile()
    {
        File.WriteAllText(Path.Combine(directory, "k.json"), "{ nie json");
        var path = Load("""{ "id": "monstera", "tuyaFile": "k.json" }""");

        var message = Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(path)).Message;

        Assert.Contains("monstera", message);
        Assert.Contains("k.json", message);
    }

    [Fact]
    public void SensorWithoutAnyTuyaDataPointsToPairing()
    {
        var path = Load("""{ "id": "monstera" }""");

        Assert.Contains("pair", Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(path)).Message);
    }

    [Fact]
    public void DuplicateMacIsRejected()
    {
        var other = ValidSensor.Replace("salon-monstera", "inny").Replace("07aaad78f40b0a74", "1111111111111111");

        Assert.Contains("adres MAC", Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(Load($"{ValidSensor}, {other}"))).Message);
    }

    [Fact]
    public void UnknownProductIsRejected()
    {
        var path = Load(ValidSensor.Replace("gvygg3m8", "zzzzzzzz"));

        Assert.Contains("productId", Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(path)).Message);
    }

    [Fact]
    public void ThresholdsMustFormAHysteresis()
    {
        var path = Load(ValidSensor.Replace("\"name\": \"Monstera\",", "\"name\": \"Monstera\", \"wateringBelowPercent\": 40, \"wateringClearAbovePercent\": 30,"));

        Assert.Contains("wateringClearAbovePercent", Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(path)).Message);
    }

    [Fact]
    public void ReportTimesMustBeHhMm()
    {
        var path = Load(ValidSensor, extra: """ "reports": { "times": ["7 rano"] }, """);

        Assert.Contains("HH:mm", Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(path)).Message);
    }

    [Fact]
    public void MailSectionNeedsPasswordAndRecipients()
    {
        var noPassword = Load(ValidSensor, extra: """ "mail": { "gmailAddress": "a@gmail.com", "alertRecipients": ["b@example.com"] }, """);
        Assert.Contains("appPassword", Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(noPassword)).Message);

        var noRecipients = Load(ValidSensor, extra: """ "mail": { "gmailAddress": "a@gmail.com", "appPassword": "x" }, """);
        Assert.Contains("alertRecipients", Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(noRecipients)).Message);
    }

    [Fact]
    public void ReportRecipientsFallBackToAlertRecipients()
    {
        var path = Load(ValidSensor, extra: """ "mail": { "gmailAddress": "a@gmail.com", "appPassword": "x", "alertRecipients": ["b@example.com"] }, """);

        var mail = AgentConfigLoader.Load(path).Mail!;

        Assert.Equal(["b@example.com"], mail.EffectiveReportRecipients);
    }

    [Fact]
    public void RecipientsAreTrimmedDeduplicatedAndConfigurablePerKind()
    {
        var path = Load(ValidSensor, extra: """
            "mail": { "gmailAddress": " nadawca@gmail.com ", "appPassword": "x",
                      "alertRecipients": [" ja@example.com ", "JA@Example.COM", "druga@example.com"],
                      "reportRecipients": ["raporty@example.com"] },
            """);

        var mail = AgentConfigLoader.Load(path).Mail!;

        Assert.Equal("nadawca@gmail.com", mail.GmailAddress);
        Assert.Equal(["ja@example.com", "druga@example.com"], mail.AlertRecipients);
        Assert.Equal(["raporty@example.com"], mail.EffectiveReportRecipients);
    }

    [Theory]
    [InlineData("bez-malpy")]
    [InlineData("Jan Kowalski <jan@example.com>")]
    [InlineData("a@b")]
    [InlineData("a@@example.com")]
    [InlineData("")]
    public void RecipientsThatAreNotPlainEmailAddressesAreRejected(string address)
    {
        var alerts = Load(ValidSensor, extra: $$""" "mail": { "gmailAddress": "a@gmail.com", "appPassword": "x", "alertRecipients": ["{{address}}"] }, """);
        Assert.Contains("alertRecipients", Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(alerts)).Message);

        var reports = Load(ValidSensor, extra: $$""" "mail": { "gmailAddress": "a@gmail.com", "appPassword": "x", "alertRecipients": ["ok@example.com"], "reportRecipients": ["{{address}}"] }, """);
        Assert.Contains("reportRecipients", Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(reports)).Message);
    }

    [Fact]
    public void AdminRecipientsAreSeparateOptionalAndNormalised()
    {
        const string baseMail = """ "mail": { "gmailAddress": "a@gmail.com", "appPassword": "x", "alertRecipients": ["ktos@example.com"], %ADMIN% }, """;

        var withAdmin = AgentConfigLoader.Load(Load(ValidSensor, extra: baseMail.Replace("%ADMIN%", """ "adminRecipients": [" ja@example.com ", "JA@Example.COM"] """))).Mail!;
        Assert.Equal(["ja@example.com"], withAdmin.AdminRecipients);
        Assert.Equal(["ktos@example.com"], withAdmin.AlertRecipients);       // admin nie miesza się z odbiorcami alertów i raportów
        Assert.Equal(["ktos@example.com"], withAdmin.EffectiveReportRecipients);

        var without = AgentConfigLoader.Load(Load(ValidSensor, extra: baseMail.Replace("%ADMIN%", """ "reportRecipients": [] """))).Mail!;
        Assert.Empty(without.AdminRecipients);                                // pusta lista = log nie jest wysyłany

        var broken = Load(ValidSensor, extra: baseMail.Replace("%ADMIN%", """ "adminRecipients": ["bez-malpy"] """));
        Assert.Contains("adminRecipients", Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(broken)).Message);
    }

    [Fact]
    public void LogRetentionDefaultsToSevenDaysAndMustBeSensible()
    {
        Assert.Equal(7, AgentConfigLoader.Load(Load(ValidSensor)).Logs.RetentionDays);
        Assert.Equal(14, AgentConfigLoader.Load(Load(ValidSensor, extra: """ "logs": { "retentionDays": 14 }, """)).Logs.RetentionDays);

        foreach (var days in new[] { 0, -3, 366 })
        {
            var path = Load(ValidSensor, extra: $$""" "logs": { "retentionDays": {{days}} }, """);
            Assert.Contains("retentionDays", Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(path)).Message);
        }
    }

    [Fact]
    public void BrokenJsonGivesReadableError() =>
        Assert.Contains("Nieprawidłowy JSON", Assert.Throws<InvalidDataException>(() => AgentConfigLoader.Load(Write("{ nie json"))).Message);
}
