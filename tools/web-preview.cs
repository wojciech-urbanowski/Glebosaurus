#:sdk Microsoft.NET.Sdk.Web
#:property TargetFramework=net10.0
#:property PublishAot=false
#:project ../src/Glebosaurus.Cloud.Admin/Glebosaurus.Cloud.Admin.csproj

// Podgląd strony w chmurze (web/) na PRAWDZIWYCH funkcjach SQL próbnego projektu Supabase – bez prawdziwego logowania.
// Tylko local/cloud-admin.test.json; odmawia, gdy wskazuje ten sam projekt co local/cloud-admin.json.
//
//   dotnet run tools/web-preview.cs -- seed    czyści próbny projekt (jak testy SQL), migracje od zera, dane pokazowe:
//                                              5 czujników, 3 agentów, 2 tygodnie odczytów, alerty, archiwum maili
//   dotnet run tools/web-preview.cs -- serve   [--port 8472] [--no-totp]
//       http://127.0.0.1:PORT/#url=http%3A%2F%2F127.0.0.1%3APORT&key=sb_publishable_podglad
//       pliki web/ prosto z repozytorium (bez pamięci podręcznej), udawane Supabase Auth (wlasciciel@example.com, hasło
//       „podglad”, kod TOTP 123456; --no-totp – konto bez TOTP, czyli dodanie TOTP przy logowaniu), a /rest/v1/rpc/* –
//       funkcje public.* w bazie jak w PostgREST: rola authenticated z żądaniami tokenu (aal z logowania), dopasowanie po
//       nazwach argumentów, transakcja tylko do odczytu dla funkcji STABLE, kody HTTP błędów jak w PostgREST. Różnica:
//       bez pg_safeupdate (Data API Supabase odrzuca UPDATE/DELETE bez WHERE; tu nie da się go załadować – regułę
//       pilnuje test SqlSafeUpdateTests).
//   dotnet run tools/web-preview.cs -- static  [--port 8473]
//       same pliki web/ na loopbacku – do prawdziwego logowania (Supabase Auth, TOTP) bez GitHub Pages; odnośnik:
//       glebosaurus cloud web-link --admin local/cloud-admin.test.json --page http://127.0.0.1:8473/
//
// Hasła, klucze i adresy w danych pokazowych są zmyślone; z pliku administratora nic nie jest wypisywane. Testy SQL
// (GLEBOSAURUS_CLOUD_TESTS=1) czyszczą próbny projekt – po nich dane pokazowe (i właściciela) trzeba wgrać od nowa.

using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Glebosaurus.Cloud.Admin;
using Glebosaurus.Core.Mail;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Npgsql;
using NpgsqlTypes;

var repository = FindRepository();
var portIndex = Array.IndexOf(args, "--port");
int? port = portIndex > 0 && portIndex + 1 < args.Length ? int.Parse(args[portIndex + 1], CultureInfo.InvariantCulture) : null;
if (args.FirstOrDefault() == "static")
{
    await Serve.RunAsync(null, Path.Combine(repository, "web"), port ?? 8473, noTotp: false);
    return 0;
}

var admin = CloudAdminSettings.Load(Path.Combine(repository, "local", "cloud-admin.test.json"));
var productionFile = Path.Combine(repository, "local", "cloud-admin.json");
if (File.Exists(productionFile) && CloudAdminSettings.Load(productionFile).ProjectRef == admin.ProjectRef)
    throw new InvalidOperationException("local/cloud-admin.test.json wskazuje prawdziwy projekt (to samo id co cloud-admin.json) – odmawiam.");
await using var data = admin.CreateDataSource("glebosaurus-web-preview");

switch (args.FirstOrDefault())
{
    case "seed":
        await Seed.RunAsync(data);
        return 0;
    case "serve":
        await Serve.RunAsync(data, Path.Combine(repository, "web"), port ?? 8472, args.Contains("--no-totp"));
        return 0;
    default:
        Console.Error.WriteLine("Użycie: dotnet run tools/web-preview.cs -- seed | serve [--port 8472] [--no-totp] | static [--port 8473]");
        return 2;
}

static string FindRepository()
{
    var start = AppContext.GetData("EntryPointFilePath") is string entry ? Path.GetDirectoryName(entry)! : Environment.CurrentDirectory;
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "Glebosaurus.slnx"))) return directory.FullName;
    throw new InvalidOperationException("Nie znaleziono katalogu repozytorium (Glebosaurus.slnx).");
}

static class Demo
{
    public static readonly Guid OwnerId = Guid.Parse("5b0e6a52-3c1f-4d8e-9a57-0d2f2c7e1a01");
    public const string OwnerEmail = "wlasciciel@example.com";
    public const string Password = "podglad";
    public const string TotpCode = "123456";
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
}

// ======================================================================================================================
// Dane pokazowe
// ======================================================================================================================
static class Seed
{
    private sealed record Plant(string Id, string Name, bool Outdoor, double Start, double DryPerDay, int Battery, bool Phone,
        int WateringBelow = 25, double DryDays = 0);

    private sealed record Agent(string Name, string Token);

    private static readonly Plant[] Plants =
    [
        new("monstera", "Monstera w salonie", false, 58, 3.5, 82, false),
        new("ziola", "Zioła na parapecie", false, 60, 8.5, 64, false, DryDays: 4.5),
        new("cisy", "Cisy przy tarasie", true, 52, 2.2, 71, true, WateringBelow: 30),
        new("hortensje", "Hortensje niskie", true, 44, 1.8, 12, false),
        new("trawa", "Trawa ozdobna", true, 40, 2.5, 55, true)
    ];

    private static readonly string[] Recipients = ["ja@example.com", "druga.osoba@example.com"];

    public static async Task RunAsync(NpgsqlDataSource data)
    {
        await ResetAsync(data);
        Console.WriteLine("Próbny projekt wyczyszczony, migracje zastosowane.");

        // Konfiguracja funkcjami administracyjnymi (jak w testach SQL; zmyślone dane Tuya i hasło).
        await ExecAsync(data, "select glebosaurus.admin_settings_update(@p::jsonb)", ("p", new JsonObject
        {
            ["timeZone"] = "Europe/Warsaw", ["readIntervalMinutes"] = 125, ["scanWindowSeconds"] = 60,
            ["reportTimes"] = new JsonArray("07:00", "19:00"), ["reportCatchUpHours"] = 3, ["alertReminderHours"] = 24,
            ["logRetentionDays"] = 7, ["gmailAddress"] = "glebosaurus.demo@gmail.com",
            ["alertRecipients"] = new JsonArray([.. Recipients.Select(r => (JsonNode)JsonValue.Create(r))]),
            ["reportRecipients"] = new JsonArray(), ["adminRecipients"] = new JsonArray("admin@example.com")
        }.ToJsonString()));
        await ExecAsync(data, "select glebosaurus.admin_mail_secret('gmail-app-password', @v)", ("v", "zmyslonehaslo00"));
        for (var i = 0; i < Plants.Length; i++)
        {
            var plant = Plants[i];
            var mac = $"AA:00:00:00:00:0{i + 1}";
            await ExecAsync(data, "select glebosaurus.admin_sensor_upsert(@id, @name, @mac, @o::jsonb)", ("id", plant.Id), ("name", plant.Name),
                ("mac", mac), ("o", new JsonObject { ["enabled"] = true, ["wateringBelowPercent"] = plant.WateringBelow }.ToJsonString()));
            await ExecAsync(data, "select glebosaurus.admin_sensor_credentials(@id, @c::jsonb)", ("id", plant.Id), ("c", new JsonObject
            {
                ["mac"] = mac, ["uuid"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
                ["deviceId"] = "demo" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(9)), ["localKeyHex"] = "a1b2c3d4e5f6",
                ["productId"] = "gvygg3m8"
            }.ToJsonString()));
        }
        Console.WriteLine($"Konfiguracja: czujników {Plants.Length}, poczta z hasłem (zmyślonym).");

        await ExecAsync(data, "insert into glebosaurus.owners (user_id, email) values (@u, @e)", ("u", Demo.OwnerId), ("e", Demo.OwnerEmail));

        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var computer = await AgentAsync(data, "komputer", sender: true, priority: 2);
        var phone = await AgentAsync(data, "telefon", sender: false, priority: null);
        var pi = await AgentAsync(data, "malinka", sender: true, priority: 1);

        // Dwa tygodnie odczytów co godzinę; ostatni odczyt w ostatnich 2 h, „trawa” milczy od 9 h, „zioła” bez podlania od 4,5 doby.
        var random = new Random(7);
        foreach (var plant in Plants)
        {
            var readings = new JsonArray();
            var end = plant.Id == "trawa" ? now.AddHours(-9) : now.AddMinutes(-random.Next(5, 110));
            var moisture = plant.Start;
            var battery = plant.Battery + 6.0;
            var lastWatering = false;
            for (var at = end.AddDays(-14); at <= end; at = at.AddHours(1))
            {
                moisture -= plant.DryPerDay / 24 * (0.7 + random.NextDouble() * 0.6);
                var forgotten = plant.DryDays > 0 && at > end.AddDays(-plant.DryDays);
                if (forgotten && !lastWatering)
                {
                    moisture = 58; // ostatnie podlanie, potem wysycha do ok. 20%
                    lastWatering = true;
                }
                if (moisture < plant.WateringBelow + 3 && !forgotten) moisture = 55 + random.NextDouble() * 10;
                moisture = Math.Clamp(moisture, 5, 95);
                var local = TimeZoneInfo.ConvertTime(at, Demo.Zone);
                var hour = local.Hour + local.Minute / 60.0;
                var temperature = plant.Outdoor
                    ? 5.5 + 3.0 * Math.Sin((hour - 9) / 24 * 2 * Math.PI) + random.NextDouble() * 0.6
                    : 21.0 + 0.8 * Math.Sin((hour - 10) / 24 * 2 * Math.PI) + random.NextDouble() * 0.3;
                battery -= 6.0 / (14 * 24);
                var shown = (int)Math.Round(battery - (random.NextDouble() < 0.1 ? 3 : 0)); // spadki pod obciążeniem radia
                readings.Add(new JsonObject
                {
                    ["sensor"] = plant.Id, ["at"] = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                    ["moisture"] = (int)Math.Round(moisture), ["tempTenths"] = (int)Math.Round(temperature * 10), ["battery"] = shown,
                    ["batteryState"] = shown < 20 ? 0 : shown < 50 ? 1 : 2
                });
            }
            var agent = plant.Phone ? phone : computer;
            var result = await AgentCallAsync(data, "agent_sync", null, ("p_token", agent.Token),
                ("p_payload", new JsonObject { ["protocol"] = 1, ["status"] = Status(agent.Name), ["readings"] = readings }));
            Console.WriteLine($"  odczyty {plant.Id}: {result["readings"]?.ToJsonString()}");
        }

        // Zdarzenia nadawcy w przeszłości: zegar bazy przesunięty (glebosaurus.test_now – projekt ma znacznik próbnego).
        async Task<long> EpochAsync(DateTimeOffset at)
        {
            var lease = await AgentCallAsync(data, "lease_claim", at, ("p_token", pi.Token), ("p_resource", "notifier"));
            if (lease["granted"]?.GetValue<bool>() != true) throw new InvalidOperationException("Brak blokady nadawcy: " + lease.ToJsonString());
            return lease["epoch"]!.GetValue<long>();
        }
        async Task<long> OpenAlertAsync(DateTimeOffset at, string sensor, string kind, string detail)
        {
            var epoch = await EpochAsync(at);
            var opened = await AgentCallAsync(data, "alert_open", at, ("p_token", pi.Token), ("p_epoch", epoch), ("p_sensor", sensor),
                ("p_kind", kind), ("p_detail", detail));
            var id = opened["alert"]!["id"]!.GetValue<long>();
            await AgentCallAsync(data, "alert_notified", at.AddMinutes(1), ("p_token", pi.Token), ("p_epoch", epoch), ("p_ids", new[] { id }));
            return id;
        }

        var closed = await OpenAlertAsync(now.AddDays(-6), "monstera", "NeedsWater", "wilgotność gleby 23% (próg 25%)");
        await AgentCallAsync(data, "alert_resolve", now.AddDays(-5.8), ("p_token", pi.Token), ("p_epoch", await EpochAsync(now.AddDays(-5.8))),
            ("p_id", closed));
        await OpenAlertAsync(now.AddDays(-3), "hortensje", "LowBattery", "bateria czujnika 15% (próg 18% lub mniej)");
        await OpenAlertAsync(now.AddHours(-14), "ziola", "NeedsWater", "wilgotność gleby 24% (próg 25%)");
        await OpenAlertAsync(now.AddHours(-3), "trawa", "SensorSilent", "brak udanego odczytu od 6 godz.");

        // Archiwum maili: raporty z ostatnich dni, alert i próbny mail – HTML z prawdziwych szablonów (MailSamples).
        var samples = MailSamples.Build(Recipients, "Glebosaurus", now, Demo.Zone);
        var muted = MailSamples.MutedReport(Recipients, "Glebosaurus", now, Demo.Zone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Demo.Zone).DateTime);
        for (var day = -3; day <= 0; day++)
        {
            foreach (var time in new[] { new TimeOnly(7, 0), new TimeOnly(19, 0) })
            {
                var date = today.AddDays(day);
                var local = date.ToDateTime(time);
                var at = new DateTimeOffset(local, Demo.Zone.GetUtcOffset(local)).AddMinutes(1);
                if (at > now) continue;
                var key = string.Create(CultureInfo.InvariantCulture, $"report|{time:HH\\:mm}|{date:yyyy-MM-dd}");
                await MailAsync(data, pi.Token, at, key, $"{(time.Hour == 7 ? "Poranny" : "Wieczorny")} raport – uwagi: {(day < -1 ? 1 : 2)}",
                    Recipients, (day == 0 ? muted : samples[2]).HtmlBody, EpochAsync);
            }
        }
        await MailAsync(data, pi.Token, now.AddHours(-14).AddMinutes(1), "alert|ziola|NeedsWater|demo|0", "Czas podlać: Zioła na parapecie",
            Recipients, samples[1].HtmlBody, EpochAsync);
        await MailAsync(data, pi.Token, now.AddDays(-1).AddHours(2), "mail-test|1", "Próbna wiadomość Glebosaurusa", ["ja@example.com"],
            samples[0].HtmlBody, EpochAsync);

        // Stan teraz: sygnał życia, słyszenia, dzierżawy czujników i blokada nadawcy.
        await AgentCallAsync(data, "agent_sync", null, ("p_token", pi.Token), ("p_payload", new JsonObject
        {
            ["protocol"] = 1, ["status"] = Status("malinka"), ["claims"] = new JsonArray("notifier")
        }));
        await AgentCallAsync(data, "agent_sync", null, ("p_token", computer.Token), ("p_payload", new JsonObject
        {
            ["protocol"] = 1, ["status"] = Status("komputer"),
            ["sightings"] = Sightings(("monstera", -71), ("ziola", -64), ("hortensje", -88), ("cisy", -93)),
            ["claims"] = new JsonArray("sensor:monstera", "sensor:ziola", "sensor:hortensje")
        }));
        await AgentCallAsync(data, "agent_sync", null, ("p_token", phone.Token), ("p_payload", new JsonObject
        {
            ["protocol"] = 1, ["status"] = Status("telefon"),
            ["sightings"] = Sightings(("cisy", -69), ("hortensje", -84), ("trawa", null)),
            ["claims"] = new JsonArray("sensor:cisy", "sensor:trawa")
        }));
        Console.WriteLine("Dane pokazowe gotowe. Podgląd: dotnet run tools/web-preview.cs -- serve");
    }

    private static JsonArray Sightings(params (string Sensor, int? Rssi)[] items) => new([.. items.Select(i => (JsonNode)new JsonObject
    {
        ["sensor"] = i.Sensor, ["rssi"] = i.Rssi, ["heardSecondsAgo"] = i.Rssi is null ? 32400 : 40, ["lastAttemptSecondsAgo"] = 600,
        ["lastSuccessSecondsAgo"] = i.Sensor == "trawa" ? 32400 : 1800, ["failures"] = i.Sensor == "trawa" ? 4 : 0
    })]);

    private static JsonObject Status(string agent) => agent switch
    {
        "telefon" => new()
        {
            ["version"] = "0.2.7", ["platform"] = "android", ["ble"] = "ok", ["batteryPercent"] = 87, ["batteryTemperatureCelsius"] = 31.5,
            ["charging"] = true, ["network"] = "wifi", ["wifiDbm"] = -58, ["thermalStatus"] = "none"
        },
        "malinka" => new() { ["version"] = "0.2.7", ["platform"] = "linux", ["ble"] = "bez Bluetooth", ["cpuTemperatureCelsius"] = 47.2 },
        _ => new() { ["version"] = "0.2.7", ["platform"] = "windows", ["ble"] = "ok" }
    };

    /// <summary>Mail tak, jak wysyła go nadawca: rezerwacja, wysłanie, archiwum (spakowany HTML).</summary>
    private static async Task MailAsync(NpgsqlDataSource data, string token, DateTimeOffset at, string key, string subject, string[] to,
        string html, Func<DateTimeOffset, Task<long>> epochAt)
    {
        var epoch = await epochAt(at);
        var claim = await AgentCallAsync(data, "mail_claim", at, ("p_token", token), ("p_key", key), ("p_epoch", epoch));
        if (claim["claimed"]?.GetValue<bool>() != true) throw new InvalidOperationException(key + ": " + claim.ToJsonString());
        await AgentCallAsync(data, "mail_sent", at.AddSeconds(5), ("p_token", token), ("p_key", key), ("p_message_id", $"demo-{Guid.NewGuid():N}@example.com"));
        using var buffer = new MemoryStream();
        await using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            await gzip.WriteAsync(Encoding.UTF8.GetBytes(html));
        await AgentCallAsync(data, "mail_archive_put", at.AddSeconds(6), ("p_token", token), ("p_epoch", epoch), ("p_keys", new[] { key }),
            ("p_subject", subject), ("p_recipients", to), ("p_html_gzip", Convert.ToBase64String(buffer.ToArray())));
        Console.WriteLine($"  mail {key}");
    }

    private static async Task<Agent> AgentAsync(NpgsqlDataSource data, string name, bool sender, int? priority)
    {
        await ExecAsync(data, "select glebosaurus.admin_agent_upsert(@n, @m, @p)", ("n", name), ("m", sender), ("p", priority));
        await using var command = data.CreateCommand("select glebosaurus.admin_enrollment_code(@n, interval '1 day')");
        command.Parameters.AddWithValue("n", name);
        var code = (string)(await command.ExecuteScalarAsync())!;
        var enrolled = await AgentCallAsync(data, "enroll", null, ("p_code", code));
        return new(name, enrolled["token"]!.GetValue<string>());
    }

    /// <summary>To samo co przed testami SQL (CloudDatabase): odmowa bez znacznika bazy próbnej, schemat od zera, znacznik.</summary>
    private static async Task ResetAsync(NpgsqlDataSource data)
    {
        await using var connection = await data.OpenConnectionAsync();
        async Task<object?> ScalarAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var value = await command.ExecuteScalarAsync();
            return value is DBNull ? null : value;
        }
        if ((bool)(await ScalarAsync("select exists (select 1 from pg_namespace where nspname = 'glebosaurus')"))!)
        {
            var kind = (bool)(await ScalarAsync("select to_regclass('glebosaurus.environment') is not null"))!
                ? (string?)await ScalarAsync("select kind from glebosaurus.environment") : null;
            var tables = (long)(await ScalarAsync("select count(*) from pg_tables where schemaname = 'glebosaurus' and tablename <> 'schema_migrations'"))!;
            if (kind != "test" && tables > 0)
                throw new InvalidOperationException("Baza ma tabele Glebosaurusa bez znacznika bazy próbnej – odmawiam czyszczenia.");
        }
        await using (var reset = new NpgsqlCommand("""
            drop schema if exists glebosaurus cascade;
            do $$
            declare r record;
            begin
                for r in select p.oid::regprocedure as signature from pg_proc p
                          where p.pronamespace = 'public'::regnamespace and obj_description(p.oid, 'pg_proc') = 'glebosaurus-api'
                loop
                    execute 'drop function ' || r.signature;
                end loop;
            end $$;
            delete from vault.secrets where name like 'glebosaurus:%';
            """, connection))
            await reset.ExecuteNonQueryAsync();
        await MigrationRunner.ApplyAsync(data, Migration.Embedded(), null, CancellationToken.None);
        await using var mark = new NpgsqlCommand("insert into glebosaurus.environment (kind) values ('test')", connection);
        await mark.ExecuteNonQueryAsync();
    }

    private static async Task ExecAsync(NpgsqlDataSource data, string sql, params (string Name, object? Value)[] args)
    {
        await using var command = data.CreateCommand(sql);
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Funkcja agenta jako rola anon (jak przez Data API), opcjonalnie z przesuniętym zegarem bazy.</summary>
    private static async Task<JsonNode> AgentCallAsync(NpgsqlDataSource data, string function, DateTimeOffset? at,
        params (string Name, object? Value)[] args)
    {
        await using var connection = await data.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (at is { } time)
        {
            await using var clock = new NpgsqlCommand("select set_config('glebosaurus.test_now', @t, true)", connection, transaction);
            clock.Parameters.AddWithValue("t", time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            await clock.ExecuteNonQueryAsync();
        }
        await using (var role = new NpgsqlCommand("set local role anon", connection, transaction)) await role.ExecuteNonQueryAsync();
        await using var command = new NpgsqlCommand(
            $"select to_jsonb(public.{function}({string.Join(", ", args.Select(a => $"{a.Name} => @{a.Name}"))}))::text", connection, transaction);
        foreach (var (name, value) in args)
            command.Parameters.Add(value switch
            {
                JsonNode node => new NpgsqlParameter(name, NpgsqlDbType.Jsonb) { Value = node.ToJsonString() },
                string[] texts => new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = texts },
                long[] ids => new NpgsqlParameter(name, NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = ids },
                _ => new NpgsqlParameter(name, value ?? DBNull.Value)
            });
        var text = await command.ExecuteScalarAsync() as string;
        await transaction.CommitAsync();
        return JsonNode.Parse(text ?? "null") ?? JsonValue.Create(false);
    }
}

// ======================================================================================================================
// Serwer podglądu: pliki strony, udawane Auth, funkcje SQL
// ======================================================================================================================
static class Serve
{
    private sealed class Factor(string id, bool verified)
    {
        public string Id { get; } = id;
        public bool Verified { get; set; } = verified;
    }

    /// <param name="data">null – same pliki strony (prawdziwe Supabase Auth i funkcje SQL projektu z odnośnika).</param>
    public static async Task RunAsync(NpgsqlDataSource? data, string web, int port, bool noTotp)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        if (data is not null) MapFakeSupabase(app, data, noTotp);

        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".webmanifest"] = "application/manifest+json";
        var files = new PhysicalFileProvider(web);
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            await next();
        });
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ContentTypeProvider = types });

        if (data is null)
        {
            Console.WriteLine($"Strona: http://127.0.0.1:{port}/ – otwórz odnośnik z: glebosaurus cloud web-link --admin PLIK --page http://127.0.0.1:{port}/");
        }
        else
        {
            Console.WriteLine($"Podgląd ({Demo.OwnerEmail}, hasło {Demo.Password}, kod {Demo.TotpCode}):");
            Console.WriteLine($"http://127.0.0.1:{port}/#url=http%3A%2F%2F127.0.0.1%3A{port}&key=sb_publishable_podglad");
        }
        await app.RunAsync();
    }

    private static void MapFakeSupabase(WebApplication app, NpgsqlDataSource data, bool noTotp)
    {

        var gate = new Lock();
        var factors = new List<Factor>();
        if (!noTotp) factors.Add(new(Guid.NewGuid().ToString(), verified: true));
        var refreshTokens = new Dictionary<string, string>(StringComparer.Ordinal); // token odświeżania → aal sesji

        IResult Session(string aal)
        {
            var expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
            static string Base64Url(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var claims = new JsonObject
            {
                ["sub"] = Demo.OwnerId.ToString(), ["email"] = Demo.OwnerEmail, ["role"] = "authenticated", ["aal"] = aal,
                ["exp"] = expiresAt, ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["session_id"] = "podglad"
            };
            var access = Base64Url("""{"alg":"none","typ":"JWT"}""") + "." + Base64Url(claims.ToJsonString()) + ".podglad";
            var refresh = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
            lock (gate) refreshTokens[refresh] = aal;
            return Results.Json(new JsonObject
            {
                ["access_token"] = access, ["token_type"] = "bearer", ["expires_in"] = 3600, ["expires_at"] = expiresAt, ["refresh_token"] = refresh,
                ["user"] = new JsonObject { ["id"] = Demo.OwnerId.ToString(), ["email"] = Demo.OwnerEmail }
            });
        }

        // Żądania tokenu podglądu (bez podpisu – to tylko ta atrapa); null – brak albo wygasły.
        static JsonObject? Claims(HttpContext context)
        {
            var header = context.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
            try
            {
                var part = header[7..].Split('.')[1].Replace('-', '+').Replace('_', '/');
                part += new string('=', (4 - part.Length % 4) % 4);
                var claims = JsonNode.Parse(Convert.FromBase64String(part))!.AsObject();
                return claims["exp"]!.GetValue<long>() < DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? null : claims;
            }
            catch (FormatException)
            {
                return null;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        static async Task<JsonObject> BodyAsync(HttpContext context)
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            return string.IsNullOrWhiteSpace(text) ? [] : JsonNode.Parse(text) as JsonObject ?? [];
        }

        static IResult AuthError(int status, string code, string message) =>
            Results.Json(new JsonObject { ["code"] = status, ["error_code"] = code, ["msg"] = message }, statusCode: status);

        app.MapPost("/auth/v1/token", async (HttpContext context) =>
        {
            var body = await BodyAsync(context);
            var grant = context.Request.Query["grant_type"].ToString();
            Console.WriteLine($"auth token {grant}");
            if (grant == "password")
            {
                return body["email"]?.GetValue<string>() == Demo.OwnerEmail && body["password"]?.GetValue<string>() == Demo.Password
                    ? Session("aal1")
                    : AuthError(400, "invalid_credentials", "Invalid login credentials");
            }
            string? aal;
            lock (gate)
            {
                if (!refreshTokens.Remove(body["refresh_token"]?.GetValue<string>() ?? "", out aal)) aal = null;
            }
            return aal is null ? AuthError(400, "refresh_token_not_found", "Invalid Refresh Token: Refresh Token Not Found") : Session(aal);
        });
        app.MapGet("/auth/v1/user", (HttpContext context) =>
        {
            if (Claims(context) is null) return AuthError(401, "bad_jwt", "invalid JWT");
            lock (gate)
            {
                return Results.Json(new JsonObject
                {
                    ["id"] = Demo.OwnerId.ToString(), ["email"] = Demo.OwnerEmail,
                    ["factors"] = new JsonArray([.. factors.Select(f => (JsonNode)new JsonObject
                    {
                        ["id"] = f.Id, ["factor_type"] = "totp", ["friendly_name"] = "Glebosaurus", ["status"] = f.Verified ? "verified" : "unverified"
                    })])
                });
            }
        });
        app.MapPost("/auth/v1/factors", () =>
        {
            var factor = new Factor(Guid.NewGuid().ToString(), verified: false);
            lock (gate) factors.Add(factor);
            const string svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'><rect width='10' height='10' fill='white'/><rect x='2' y='2' width='6' height='6'/></svg>";
            return Results.Json(new JsonObject
            {
                ["id"] = factor.Id, ["type"] = "totp",
                ["totp"] = new JsonObject { ["qr_code"] = "data:image/svg+xml;utf-8," + svg, ["secret"] = "JBSWY3DPEHPK3PXP", ["uri"] = "otpauth://totp/Glebosaurus:wlasciciel%40example.com?secret=JBSWY3DPEHPK3PXP&issuer=Glebosaurus" }
            });
        });
        app.MapDelete("/auth/v1/factors/{id}", (string id) =>
        {
            lock (gate) factors.RemoveAll(f => f.Id == id && !f.Verified);
            return Results.Json(new JsonObject { ["id"] = id });
        });
        app.MapPost("/auth/v1/factors/{id}/challenge", () => Results.Json(new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(), ["type"] = "totp", ["expires_at"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()
        }));
        app.MapPost("/auth/v1/factors/{id}/verify", async (HttpContext context, string id) =>
        {
            var body = await BodyAsync(context);
            if (body["code"]?.GetValue<string>() != Demo.TotpCode) return AuthError(422, "mfa_verification_failed", "Invalid TOTP code entered");
            lock (gate)
            {
                foreach (var factor in factors.Where(f => f.Id == id)) factor.Verified = true;
            }
            return Session("aal2");
        });
        app.MapPost("/auth/v1/logout", () => Results.NoContent());

        app.MapPost("/rest/v1/rpc/{name}", async (HttpContext context, string name) =>
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            if (Claims(context) is not { } claims)
            {
                Console.WriteLine($"rpc {name}: 401 (brak albo wygasły token)");
                return Results.Json(new JsonObject { ["code"] = "PGRST301", ["message"] = "JWT expired" }, statusCode: 401);
            }
            var body = await BodyAsync(context);
            var (status, result) = await Rpc.CallAsync(data, name, body, claims);
            Console.WriteLine($"rpc {name}({string.Join(", ", body.Select(p => p.Key))}): {status}, {stopwatch.ElapsedMilliseconds} ms" +
                              (status >= 300 ? " " + result : ""));
            return status == 204 ? Results.NoContent() : Results.Content(result ?? "null", "application/json", Encoding.UTF8, status);
        });
    }
}

/// <summary>Wywołanie funkcji public.* tak jak PostgREST: dopasowanie po nazwach argumentów (z domyślnymi), rola
/// authenticated z żądaniami tokenu, transakcja tylko do odczytu dla STABLE/IMMUTABLE, kody HTTP błędów według PostgREST.</summary>
static partial class Rpc
{
    private sealed record Candidate(string[] Names, string[] Types, string[] Categories, int Defaults, char Volatility, string Returns, bool ReturnsSet);

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex Identifier();

    public static async Task<(int Status, string? Body)> CallAsync(NpgsqlDataSource data, string name, JsonObject body, JsonObject claims)
    {
        if (!Identifier().IsMatch(name)) return (404, Error("PGRST202", $"Could not find the function public.{name} in the schema cache"));
        await using var connection = await data.OpenConnectionAsync();
        var candidates = new List<Candidate>();
        await using (var lookup = new NpgsqlCommand("""
            select coalesce(p.proargnames[1:p.pronargs], '{}'),
                   array(select format_type(t, null) from unnest(p.proargtypes::oid[]) with ordinality u(t, i) order by u.i),
                   array(select ty.typcategory::text from unnest(p.proargtypes::oid[]) with ordinality u(t, i)
                          join pg_type ty on ty.oid = u.t order by u.i),
                   p.pronargdefaults, p.provolatile, format_type(p.prorettype, null), p.proretset
              from pg_proc p where p.pronamespace = 'public'::regnamespace and p.proname = @n
            """, connection))
        {
            lookup.Parameters.AddWithValue("n", name);
            await using var reader = await lookup.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                candidates.Add(new(reader.GetFieldValue<string[]>(0), reader.GetFieldValue<string[]>(1), reader.GetFieldValue<string[]>(2),
                    reader.GetInt16(3), reader.GetChar(4), reader.GetString(5), reader.GetBoolean(6)));
        }
        var keys = body.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var matching = candidates.Where(c => keys.All(c.Names.Contains) && c.Names.Take(c.Names.Length - c.Defaults).All(keys.Contains)).ToList();
        if (matching.Count == 0)
            return (404, Error("PGRST202", $"Could not find the function public.{name}({string.Join(", ", keys.Order(StringComparer.Ordinal))}) in the schema cache"));
        if (matching.Count > 1) return (300, Error("PGRST203", $"Could not choose the best candidate function between: public.{name}"));
        var function = matching[0];

        var arguments = new List<string>();
        for (var i = 0; i < function.Names.Length; i++)
        {
            var argument = function.Names[i];
            if (!keys.Contains(argument) || !Identifier().IsMatch(argument)) continue;
            var node = $"(@body::jsonb -> '{argument}')";
            var expression = function.Types[i] switch
            {
                "jsonb" => node,
                "json" => $"{node}::json",
                _ when function.Categories[i] == "A" =>
                    $"(case when jsonb_typeof({node}) = 'array' then array(select jsonb_array_elements_text({node})) end)::{function.Types[i]}",
                _ => $"(@body::jsonb ->> '{argument}')::{function.Types[i]}"
            };
            arguments.Add($"{argument} => {expression}");
        }
        var call = $"public.{name}({string.Join(", ", arguments)})";
        var sql = function.ReturnsSet ? $"select coalesce(jsonb_agg(to_jsonb(r)), '[]'::jsonb)::text from {call} r"
            : function.Returns == "void" ? $"select {call}::text" : $"select to_jsonb({call})::text";

        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            if (function.Volatility is 's' or 'i')
            {
                await using var readOnly = new NpgsqlCommand("set transaction read only", connection, transaction);
                await readOnly.ExecuteNonQueryAsync();
            }
            await using (var jwt = new NpgsqlCommand("select set_config('request.jwt.claims', @c, true)", connection, transaction))
            {
                jwt.Parameters.AddWithValue("c", claims.ToJsonString());
                await jwt.ExecuteNonQueryAsync();
            }
            await using (var role = new NpgsqlCommand("set local role authenticated", connection, transaction)) await role.ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.Add(new NpgsqlParameter("body", NpgsqlDbType.Jsonb) { Value = body.ToJsonString() });
            var result = await command.ExecuteScalarAsync();
            await transaction.CommitAsync();
            return function.Returns == "void" ? (204, null) : (200, result as string ?? "null");
        }
        catch (PostgresException e)
        {
            return (HttpStatus(e.SqlState), new JsonObject
            {
                ["code"] = e.SqlState, ["details"] = e.Detail, ["hint"] = e.Hint, ["message"] = e.MessageText
            }.ToJsonString());
        }
    }

    /// <summary>Tabela PostgREST „HTTP status codes” dla błędów Postgresa (rola authenticated: 42501 → 403; PTxyz – status
    /// xyz). Ponawiania transakcji po 40001 podgląd nie udaje – funkcje tego kodu nie zgłaszają (SqlDataApiRulesTests).</summary>
    private static int HttpStatus(string state) => state switch
    {
        "42501" => 403,
        _ when state.StartsWith("PT", StringComparison.Ordinal)
               && int.TryParse(state.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var status) => status,
        "P0001" => 400,
        "23503" or "23505" => 409,
        "42883" or "42P01" => 404,
        "25006" => 405,
        _ when state[..2] is "08" or "53" => 503,
        _ when state[..2] is "28" or "0L" or "0P" => 403,
        _ when state[..2] is "09" or "25" or "2D" or "38" or "39" or "3B" or "40" or "54" or "55" or "57" or "58" or "F0" or "HV" or "P0" or "XX" => 500,
        _ => 400
    };

    private static string Error(string code, string message) => new JsonObject { ["code"] = code, ["message"] = message }.ToJsonString();
}
