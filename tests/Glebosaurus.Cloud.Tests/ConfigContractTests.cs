using System.Text.Json;
using System.Text.Json.Nodes;
using Glebosaurus.Core.Config;
using Npgsql;

namespace Glebosaurus.Cloud.Tests;

/// <summary>
/// Kontrakt edytora konfiguracji (faza 3c): te same dane są dobre albo złe i dla pliku konfiguracji chmury
/// (<see cref="CloudSetupLoader"/>), i dla funkcji strony (<c>owner_settings_update</c>, <c>owner_sensor_add</c>,
/// <c>owner_secret_set</c>). Jeden zestaw przypadków na wzorze w kształcie <c>cloud-config.json</c>; strona dostaje go
/// rozłożony na wywołania tak, jak robi to edytor. Funkcje działają w transakcji wycofywanej na końcu.
/// </summary>
public sealed class ConfigContractTests(CloudDatabase db)
{
    private sealed record Case(string Name, bool Valid, Action<JsonObject> Patch);

    private static JsonObject Sensor(JsonObject model) => model["sensors"]![0]!.AsObject();
    private static JsonObject Tuya(JsonObject model) => Sensor(model)["tuya"]!.AsObject();

    private static readonly Case[] All =
    [
        new Case("wzór", true, _ => { }),
        new Case("granice dozwolone", true, m =>
        {
            m["scanWindowSeconds"] = 120; m["readIntervalMinutes"] = 0.5; m["logs"]!["retentionDays"] = 30;
            m["reports"]!["times"] = new JsonArray(); m["reports"]!["catchUpHours"] = 0;
            Sensor(m)["consecutiveReadings"] = 10; Sensor(m)["wateringBelowPercent"] = 0; Sensor(m)["wateringClearAbovePercent"] = 1;
            Sensor(m)["lowBatteryPercent"] = 100; Sensor(m)["readIntervalMinutes"] = 0.5;
        }),
        new Case("klucz jako tekst (localKey)", true, m => { Tuya(m).Remove("localKeyHex"); Tuya(m)["localKey"] = "abcdef"; }),
        new Case("MAC małymi literami", true, m => Tuya(m)["mac"] = Tuya(m)["mac"]!.GetValue<string>().ToLowerInvariant()),
        new Case("nieznana strefa", false, m => m["timeZone"] = "Mars/Olympus"),
        new Case("odstęp odczytów < 0,5", false, m => m["readIntervalMinutes"] = 0.4),
        new Case("okno skanu < 5", false, m => m["scanWindowSeconds"] = 4),
        new Case("okno skanu > 120", false, m => m["scanWindowSeconds"] = 121),
        new Case("pora raportu 25:00", false, m => m["reports"]!["times"] = new JsonArray("25:00")),
        new Case("pora raportu 7:00", false, m => m["reports"]!["times"] = new JsonArray("7:00")),
        new Case("nadrabianie > 24 h", false, m => m["reports"]!["catchUpHours"] = 25),
        new Case("nadrabianie < 0", false, m => m["reports"]!["catchUpHours"] = -1),
        new Case("przypomnienia co 0 h", false, m => m["alerts"]!["reminderHours"] = 0),
        new Case("logi 0 dni", false, m => m["logs"]!["retentionDays"] = 0),
        new Case("logi 31 dni", false, m => m["logs"]!["retentionDays"] = 31),
        new Case("nadawca bez adresu", false, m => m["mail"]!["gmailAddress"] = "nie-adres"),
        new Case("brak odbiorców alertów", false, m => m["mail"]!["alertRecipients"] = new JsonArray()),
        new Case("odbiorca z imieniem", false, m => m["mail"]!["alertRecipients"] = new JsonArray("Jan <jan@example.com>")),
        new Case("odbiorca bez kropki w domenie", false, m => m["mail"]!["reportRecipients"] = new JsonArray("jan@localhost")),
        new Case("puste hasło aplikacji", false, m => m["mail"]!["appPassword"] = ""),
        new Case("id z wielką literą", false, m => Sensor(m)["id"] = "Bazylia"),
        new Case("id od myślnika", false, m => Sensor(m)["id"] = "-bazylia"),
        new Case("id dłuższe niż 40", false, m => Sensor(m)["id"] = new string('a', 41)),
        new Case("nazwa dłuższa niż 80", false, m => Sensor(m)["name"] = new string('n', 81)),
        new Case("próg ponad 100", false, m => Sensor(m)["wateringBelowPercent"] = 101),
        new Case("próg zdjęcia równy progowi", false, m => { Sensor(m)["wateringBelowPercent"] = 30; Sensor(m)["wateringClearAbovePercent"] = 30; }),
        new Case("próg zdjęcia niższy", false, m => { Sensor(m)["wateringBelowPercent"] = 30; Sensor(m)["wateringClearAbovePercent"] = 20; }),
        new Case("kolejne odczyty 0", false, m => Sensor(m)["consecutiveReadings"] = 0),
        new Case("kolejne odczyty 11", false, m => Sensor(m)["consecutiveReadings"] = 11),
        new Case("słaba bateria 101", false, m => Sensor(m)["lowBatteryPercent"] = 101),
        new Case("cisza po 0 h", false, m => Sensor(m)["silentAfterHours"] = 0),
        new Case("odstęp czujnika < 0,5", false, m => Sensor(m)["readIntervalMinutes"] = 0.4),
        new Case("UUID 15 znaków", false, m => Tuya(m)["uuid"] = "0123456789abcde"),
        new Case("deviceId 23 znaki", false, m => Tuya(m)["deviceId"] = new string('d', 23)),
        new Case("klucz HEX za krótki", false, m => Tuya(m)["localKeyHex"] = "a1b2c3d4e5"),
        new Case("oba klucze", false, m => Tuya(m)["localKey"] = "abcdef"),
        new Case("nieznany productId", false, m => Tuya(m)["productId"] = "xxxxxxxx"),
        new Case("brak MAC", false, m => Tuya(m).Remove("mac")),
    ];

    public static TheoryData<string> Cases() => [.. All.Select(c => c.Name)];

    private static Case Find(string name) => All.Single(c => c.Name == name);

    private static JsonObject Model()
    {
        var mac = Scenario.RandomMac();
        return new JsonObject
        {
            ["timeZone"] = "Europe/Warsaw",
            ["readIntervalMinutes"] = 125,
            ["scanWindowSeconds"] = 60,
            ["reports"] = new JsonObject { ["times"] = new JsonArray("07:00", "19:00"), ["catchUpHours"] = 3 },
            ["alerts"] = new JsonObject { ["reminderHours"] = 24 },
            ["logs"] = new JsonObject { ["retentionDays"] = 7 },
            ["mail"] = new JsonObject
            {
                ["gmailAddress"] = "nadawca@gmail.com", ["appPassword"] = "abcdefghijklmnop",
                ["alertRecipients"] = new JsonArray("ktos@example.com"), ["reportRecipients"] = new JsonArray(),
                ["adminRecipients"] = new JsonArray("admin@example.com")
            },
            ["sensors"] = new JsonArray(new JsonObject
            {
                ["id"] = Scenario.Unique("kontrakt"), ["name"] = "Bazylia", ["tuya"] = Scenario.Credentials(mac),
                ["wateringBelowPercent"] = 25, ["wateringClearAbovePercent"] = 35, ["consecutiveReadings"] = 2,
                ["lowBatteryPercent"] = 18, ["silentAfterHours"] = 6
            })
        };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void File_AcceptsOrRejects(string name)
    {
        var c = Find(name);
        var model = Model();
        c.Patch(model);

        var file = Path.Combine(Path.GetTempPath(), $"glebosaurus-contract-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, model.ToJsonString());
        try
        {
            if (c.Valid) CloudSetupLoader.Load(file);
            else Assert.Throws<InvalidDataException>(() => CloudSetupLoader.Load(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OwnerFunctions_AcceptOrReject(string name)
    {
        var c = Find(name);
        db.SkipIfUnavailable();
        var model = Model();
        c.Patch(model);
        var claims = await new Scenario(db, DateTimeOffset.UtcNow).OwnerAsync();
        await using var owner = await OwnerSession.StartAsync(db, claims);

        var failure = await RecordAsync(() => SaveAsync(owner, model));

        if (c.Valid) Assert.Null(failure?.MessageText);
        else
        {
            Assert.NotNull(failure);
            Assert.Equal("22023", failure.SqlState);
        }
    }

    [Fact]
    public async Task DuplicateMacOrUuid_IsRejectedByBoth()
    {
        db.SkipIfUnavailable();
        foreach (var field in new[] { "mac", "uuid" })
        {
            var model = Model();
            var second = Sensor(model).DeepClone().AsObject();
            second["id"] = Scenario.Unique("kontrakt");
            second["tuya"] = Scenario.Credentials(Scenario.RandomMac());
            second["tuya"]![field] = Tuya(model)[field]!.DeepClone();
            model["sensors"]!.AsArray().Add(second);

            var file = Path.Combine(Path.GetTempPath(), $"glebosaurus-contract-{Guid.NewGuid():N}.json");
            File.WriteAllText(file, model.ToJsonString());
            try { Assert.Throws<InvalidDataException>(() => CloudSetupLoader.Load(file)); }
            finally { File.Delete(file); }

            var claims = await new Scenario(db, DateTimeOffset.UtcNow).OwnerAsync();
            await using var owner = await OwnerSession.StartAsync(db, claims);
            var failure = await RecordAsync(() => SaveAsync(owner, model));
            Assert.Equal("22023", failure?.SqlState);
        }
    }

    /// <summary>Wzór rozłożony na wywołania edytora: ustawienia z pocztą, hasło, potem każdy czujnik z plikiem klucza.</summary>
    private static async Task SaveAsync(OwnerSession owner, JsonObject model)
    {
        var reports = model["reports"]!.AsObject();
        var mail = model["mail"]!.AsObject();
        var settings = new JsonObject
        {
            ["timeZone"] = model["timeZone"]?.DeepClone(),
            ["readIntervalMinutes"] = model["readIntervalMinutes"]?.DeepClone(),
            ["scanWindowSeconds"] = model["scanWindowSeconds"]?.DeepClone(),
            ["reportTimes"] = reports["times"]?.DeepClone(),
            ["reportCatchUpHours"] = reports["catchUpHours"]?.DeepClone(),
            ["alertReminderHours"] = model["alerts"]!["reminderHours"]?.DeepClone(),
            ["logRetentionDays"] = model["logs"]!["retentionDays"]?.DeepClone(),
            ["gmailAddress"] = mail["gmailAddress"]?.DeepClone(),
            ["alertRecipients"] = mail["alertRecipients"]?.DeepClone(),
            ["reportRecipients"] = mail["reportRecipients"]?.DeepClone(),
            ["adminRecipients"] = mail["adminRecipients"]?.DeepClone()
        };
        await owner.CallAsync("owner_settings_update", ("p_version", await owner.VersionAsync()), ("p", settings));
        await owner.CallAsync("owner_secret_set", ("p_name", "gmail-app-password"), ("p_value", mail["appPassword"]!.GetValue<string>()));
        foreach (var node in model["sensors"]!.AsArray())
        {
            var sensor = node!.AsObject();
            var options = new JsonObject();
            foreach (var key in new[] { "readIntervalMinutes", "wateringBelowPercent", "wateringClearAbovePercent", "consecutiveReadings",
                                        "lowBatteryPercent", "silentAfterHours" })
                if (sensor[key] is { } value) options[key] = value.DeepClone();
            await owner.CallAsync("owner_sensor_add", ("p_version", await owner.VersionAsync()), ("p_id", sensor["id"]!.GetValue<string>()),
                ("p_name", sensor["name"]?.GetValue<string>()), ("p_credentials", sensor["tuya"]!.DeepClone()), ("p_options", options));
        }
    }

    private static async Task<PostgresException?> RecordAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (PostgresException e)
        {
            return e;
        }
    }
}
