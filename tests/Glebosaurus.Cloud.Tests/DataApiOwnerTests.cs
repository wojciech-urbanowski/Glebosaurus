using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glebosaurus.Cloud.Tests;

/// <summary>
/// Funkcje strony właściciela przez PRAWDZIWE Supabase Auth i Data API próbnego projektu (<see cref="DataApiOwner"/>), obok
/// pozostałych testów <c>Owner*</c>, które wołają je przez połączenie bezpośrednie. Data API działa w innym środowisku:
/// rola <c>authenticator</c> ładuje <c>pg_safeupdate</c> (UPDATE/DELETE bez WHERE – SQLSTATE 21000, także w wyzwalaczach;
/// 2026-11-21 tak zawiodła prawdziwa strona), role mają limity czasu, argumenty dopasowuje się po nazwach (PGRST202/203),
/// transakcję z SQLSTATE 40001 PostgREST ponawia w pętli (do 2026-11-21 tak wisiał konflikt wersji), a błędy wracają jako kody HTTP z JSON-em PostgREST. Zegar prawdziwy – Data API nie pozwala klientom ustawić
/// <c>glebosaurus.test_now</c>. Wspólne ustawienia (przypomnienia, urlop, hasło Gmaila) wracają na miejsce w <c>finally</c>.
/// </summary>
public sealed class DataApiOwnerTests(CloudDatabase db)
{
    /// <summary>Każda funkcja strony co najmniej raz (zmieniające dane – każda), z kształtem odpowiedzi, z którego korzysta
    /// strona. Lista funkcji z <see cref="OwnerTests.OwnerFunctions"/> (ten test porównuje ją z bazą) – nowa funkcja
    /// <c>owner_*</c> bez wywołania tutaj kończy ten test błędem.</summary>
    [Fact]
    public async Task EveryOwnerFunction_WorksThroughSupabaseAuthAndDataApi()
    {
        var api = await db.DataApiOwnerAsync();
        var called = new HashSet<string>(StringComparer.Ordinal);
        Task<JsonElement> Rpc(string function, params (string Name, object? Value)[] args)
        {
            called.Add(function);
            return api.RpcAsync(function, args);
        }

        var now = DateTimeOffset.UtcNow;
        var s = new Scenario(db, now);
        var agent = await s.AgentAsync("api");
        var sensor = await s.SensorAsync("api");
        await s.SyncAsync(agent, now, new JsonObject
        {
            ["readings"] = new JsonArray(
                new JsonObject { ["sensor"] = sensor, ["at"] = now.AddMinutes(-40).ToString("O"), ["moisture"] = 35, ["tempTenths"] = 210 },
                new JsonObject { ["sensor"] = sensor, ["at"] = now.AddMinutes(-10).ToString("O"), ["moisture"] = 31, ["tempTenths"] = 205 })
        });

        // Odczyt: informacje, pulpit, konfiguracja, strona czujnika, odczyty, wykres.
        var info = await Rpc("owner_info");
        Assert.True(info.GetProperty("test").GetBoolean());
        Assert.Contains((await Rpc("owner_dashboard")).GetProperty("sensors").EnumerateArray(), x => x.Text("id") == sensor);
        var config = await Rpc("owner_config");
        var version = config.GetProperty("configVersion").GetInt64();
        Assert.Equal(31, (await Rpc("owner_sensor", ("p_id", sensor))).GetProperty("last").GetProperty("moisture").GetInt32());
        Assert.Equal(2, (await Rpc("owner_readings", ("p_id", sensor), ("p_count", 10))).GetArrayLength());
        var series = await Rpc("owner_series", ("p_sensor", sensor), ("p_from", now.AddDays(-1)), ("p_to", now.AddMinutes(1)),
            ("p_points", 48));                                           // p_zone pominięty – wartość domyślna funkcji
        Assert.NotEqual(0, series.GetProperty("points").GetArrayLength());

        // Ustawienia (wspólny wiersz settings – „UPDATE requires a WHERE clause” z 2026-11-21).
        var reminder = config.GetProperty("settings").GetProperty("alertReminderHours").GetDouble();
        try
        {
            var saved = await Rpc("owner_settings_update", ("p_version", version), ("p", new JsonObject { ["alertReminderHours"] = reminder + 1 }));
            Assert.True(saved.GetProperty("configVersion").GetInt64() > version);
            Assert.Equal(reminder + 1, (await api.RpcAsync("owner_config")).GetProperty("settings").GetProperty("alertReminderHours").GetDouble());
        }
        finally
        {
            await api.RpcAsync("owner_settings_update", ("p_version", await ConfigVersionAsync(api)),
                ("p", new JsonObject { ["alertReminderHours"] = reminder }));
        }

        // Czujnik: dodanie z pliku klucza, zmiana, nowy klucz, archiwum i powrót, usunięcie (wyzwalacz config_changed).
        var added = Scenario.Unique("api-nowy");
        version = (await Rpc("owner_sensor_add", ("p_version", await ConfigVersionAsync(api)), ("p_id", added), ("p_name", "Nowy " + added),
            ("p_credentials", Scenario.Credentials(Scenario.RandomMac())), ("p_options", new JsonObject()))).GetProperty("configVersion").GetInt64();
        version = (await Rpc("owner_sensor_update", ("p_version", version), ("p_id", added), ("p_name", "Zmieniony"),
            ("p_options", new JsonObject { ["wateringBelowPercent"] = 25, ["wateringClearAbovePercent"] = 35 }))).GetProperty("configVersion").GetInt64();
        var newMac = Scenario.RandomMac();
        var credentials = await Rpc("owner_sensor_credentials", ("p_version", version), ("p_id", added), ("p_credentials", Scenario.Credentials(newMac)));
        Assert.Equal(newMac, credentials.GetProperty("mac").GetString());
        version = (await Rpc("owner_sensor_archive", ("p_version", credentials.GetProperty("configVersion").GetInt64()), ("p_id", added),
            ("p_archive", true))).GetProperty("configVersion").GetInt64();
        version = (await Rpc("owner_sensor_archive", ("p_version", version), ("p_id", added), ("p_archive", false))).GetProperty("configVersion").GetInt64();
        var listed = (await api.RpcAsync("owner_config")).GetProperty("sensors").EnumerateArray().Single(x => x.Text("id") == added);
        Assert.Equal("Zmieniony", listed.Text("name"));
        Assert.Equal(newMac, listed.Text("mac"));
        await Rpc("owner_sensor_delete", ("p_version", version), ("p_id", added));
        Assert.DoesNotContain((await api.RpcAsync("owner_config")).GetProperty("sensors").EnumerateArray(), x => x.Text("id") == added);

        // Czujnik Flower Care: dodanie po samym MAC i nowy adres (bez klucza – nic w Vault), potem usunięcie.
        var flower = Scenario.Unique("api-fc");
        version = (await Rpc("owner_sensor_add_flower_care", ("p_version", await ConfigVersionAsync(api)), ("p_id", flower),
            ("p_name", "Fikus " + flower), ("p_mac", Scenario.RandomMac()), ("p_options", new JsonObject()))).GetProperty("configVersion").GetInt64();
        var flowerMac = Scenario.RandomMac();
        var moved = await Rpc("owner_sensor_mac", ("p_version", version), ("p_id", flower), ("p_mac", flowerMac));
        Assert.Equal(flowerMac, moved.Text("mac"));
        await Rpc("owner_sensor_delete", ("p_version", moved.GetProperty("configVersion").GetInt64()), ("p_id", flower));

        // Sekret (Vault): ustawienie i wyczyszczenie; hasło innych testów wraca na miejsce.
        var before = await db.AdminAsync("select coalesce(to_jsonb(glebosaurus.secret_get('glebosaurus:gmail-app-password')), 'null')::text", null);
        try
        {
            Assert.True((await Rpc("owner_secret_set", ("p_name", "gmail-app-password"), ("p_value", "abcd efgh ijkl mnop"))).GetProperty("set").GetBoolean());
        }
        finally
        {
            if (before.ValueKind == JsonValueKind.String)
                await db.AdminAsync("select glebosaurus.secret_put('glebosaurus:gmail-app-password', @v)::text", null, ("v", before.GetString()));
            else await api.RpcAsync("owner_secret_set", ("p_name", "gmail-app-password"), ("p_value", null));
        }

        // Alerty: wyciszenie czujnika i urlop (settings), potwierdzenie i zamknięcie, lista, archiwum maili.
        try
        {
            var muted = await Rpc("owner_alerts_mute", ("p_sensor", sensor), ("p_until", now.AddDays(2)));
            Assert.Equal(sensor, muted.Text("sensor"));
            await Rpc("owner_alerts_mute", ("p_sensor", null), ("p_until", now.AddDays(3)));
            Assert.Equal(JsonValueKind.String, (await api.RpcAsync("owner_dashboard")).GetProperty("alertsMutedUntil").ValueKind);
        }
        finally
        {
            await api.RpcAsync("owner_alerts_mute", ("p_sensor", null), ("p_until", null));
            await api.RpcAsync("owner_alerts_mute", ("p_sensor", sensor), ("p_until", null));
        }
        var alertId = (await db.AdminAsync("""
            insert into glebosaurus.alerts (sensor_id, kind, detail, opened_at) values (@s, 'NeedsWater', 'sucho', now() - interval '1 hour')
            returning to_jsonb(id)::text
            """, null, ("s", sensor))).GetInt64();
        var acknowledged = await Rpc("owner_alert_acknowledge", ("p_id", alertId), ("p_acknowledge", true));
        Assert.Equal(JsonValueKind.String, acknowledged.GetProperty("acknowledgedAt").ValueKind);
        Assert.True((await Rpc("owner_alert_close", ("p_id", alertId))).GetProperty("closedManually").GetBoolean());
        Assert.Contains((await Rpc("owner_alerts", ("p_days", 7))).GetProperty("closed").EnumerateArray(), a => a.GetProperty("id").GetInt64() == alertId);

        var mailKey = $"report-request|{Scenario.Unique("api")}";
        const string html = "<html><body><p>Raport z Data API</p></body></html>";
        var gzip = Gzip(html);
        await db.AdminAsync("""
            insert into glebosaurus.mail_archive (key, keys, kind, subject, recipients, agent_id, sent_at, html_gzip, size_bytes)
            select @k, array[@k], 'report-request', 'Raport', array['ktos@example.com'], id, now(), @g, @n
              from glebosaurus.agents where name = @a
            returning '{}'::text
            """, null, ("k", mailKey), ("g", gzip), ("n", gzip.Length), ("a", agent.Name));
        Assert.Contains((await Rpc("owner_mail_history", ("p_days", 7))).EnumerateArray(), m => m.Text("key") == mailKey);
        var body = await Rpc("owner_mail_body", ("p_key", mailKey));
        Assert.Equal(html, Gunzip(Convert.FromBase64String(body.Text("htmlGzip")!)));

        // Agent: nowy, kod rejestracji, zmiana roli, odcięcie.
        var name = Scenario.Unique("api-agent");
        await Rpc("owner_agent_save", ("p_name", name), ("p_can_send_mail", false), ("p_sender_priority", null), ("p_enabled", true));
        var code = (await Rpc("owner_agent_code", ("p_name", name))).Text("code");
        var token = (await db.AgentAsync("enroll", null, ("p_code", code))).Text("token");
        await api.RpcAsync("owner_agent_save", ("p_name", name), ("p_can_send_mail", true), ("p_sender_priority", 3), ("p_enabled", true));
        Assert.True((await db.AgentAsync("get_config", null, ("p_token", token))).GetProperty("agent").GetProperty("canSendMail").GetBoolean());
        Assert.True((await Rpc("owner_agent_revoke", ("p_name", name))).GetProperty("hadToken").GetBoolean());

        // Polecenia (bez p_agent – domyślny null) i ich stan.
        var read = (await Rpc("owner_command", ("p_kind", "read_now"), ("p_sensor", sensor))).GetProperty("id").GetInt64();
        var log = (await api.RpcAsync("owner_command", ("p_kind", "send_log"), ("p_agent", agent.Name))).GetProperty("id").GetInt64();
        var status = await Rpc("owner_command_status", ("p_ids", new[] { read, log }));
        Assert.Equal([read, log], status.EnumerateArray().Select(c => c.GetProperty("id").GetInt64()));

        // Parowanie zdalne: start; czujnika w trakcie parowania nie da się „zapomnieć”, innego – tak.
        var pairing = Scenario.Unique("api-para");
        Assert.True((await Rpc("owner_pair_start", ("p_agent", agent.Name), ("p_sensor", pairing), ("p_name", "Zdalny"), ("p_mac", null)))
            .GetProperty("created").GetBoolean());
        var busy = await Assert.ThrowsAsync<DataApiException>(() => Rpc("owner_pair_forget", ("p_sensor", pairing)));
        Assert.Equal("22023", busy.Code);
        Assert.False((await Rpc("owner_pair_forget", ("p_sensor", Scenario.Unique("api-inny")))).GetProperty("forgotten").GetBoolean());

        Assert.Empty(OwnerTests.OwnerFunctions.Except(called));
    }

    /// <summary>Kody HTTP i treść błędów, na których opiera się <c>Cloud.rpc</c> w <c>web/supabase.js</c>.</summary>
    [Fact]
    public async Task Errors_ReachThePageAsHttpStatusSqlStateAndMessage()
    {
        var api = await db.DataApiOwnerAsync();

        // Błąd z funkcji Glebosaurusa: 400, SQLSTATE w „code”, powód po polsku z przedrostkiem „Glebosaurus: ”.
        var invalid = await Assert.ThrowsAsync<DataApiException>(() => api.RpcAsync("owner_agent_save", ("p_name", "Zła Nazwa"),
            ("p_can_send_mail", false), ("p_sender_priority", null), ("p_enabled", true)));
        AssertError(invalid, HttpStatusCode.BadRequest, "22023");
        Assert.StartsWith("Glebosaurus: nazwa agenta", invalid.Text);

        // Samo hasło (aal1): 403 „mfa_required” – strona prosi o kod TOTP.
        var mfa = await Assert.ThrowsAsync<DataApiException>(() => api.RpcWithoutTotpAsync("owner_dashboard"));
        AssertError(mfa, HttpStatusCode.Forbidden, "42501", "Glebosaurus: mfa_required");

        // Konto z TOTP spoza listy właścicieli: 403 „not_owner”.
        await db.AdminAsync("select to_jsonb(glebosaurus.admin_owner_remove(@e))::text", null, ("e", api.Email));
        try
        {
            var notOwner = await Assert.ThrowsAsync<DataApiException>(() => api.RpcAsync("owner_info"));
            AssertError(notOwner, HttpStatusCode.Forbidden, "42501", "Glebosaurus: not_owner");
        }
        finally
        {
            await db.AdminAsync("select glebosaurus.admin_owner_add(@e)::text", null, ("e", api.Email));
        }

        // Klucz publikowalny bez tokenu (rola anon): funkcje strony zamknięte uprawnieniami – 401.
        var anonymous = await Assert.ThrowsAsync<DataApiException>(() => api.RpcAnonymousAsync("owner_info"));
        AssertError(anonymous, HttpStatusCode.Unauthorized, "42501");

        // Zepsuty token: 401 od Data API, zanim zadziała jakakolwiek funkcja.
        var badToken = await Assert.ThrowsAsync<DataApiException>(() => api.RpcWithTokenAsync("nie-token", "owner_info"));
        Assert.Equal(HttpStatusCode.Unauthorized, badToken.Status);

        // Argumenty dopasowuje się po nazwach: zła nazwa = brak takiej funkcji (404 PGRST202, strona: „brakuje migracji”).
        var wrongName = await Assert.ThrowsAsync<DataApiException>(() => api.RpcAsync("owner_sensor", ("p_sensor", "x")));
        AssertError(wrongName, HttpStatusCode.NotFound, "PGRST202");
    }

    /// <summary>
    /// Nieaktualna wersja konfiguracji (dwie karty, strona otwarta od wczoraj) wraca od razu jako 409 z przedrostkiem
    /// „Glebosaurus: conflict” – po nim strona rozpoznaje konflikt. Do migracji 20261121233000_config_conflict
    /// <c>glebosaurus.require_config_version</c> zgłaszała SQLSTATE 40001 (serialization_failure), a Data API (PostgREST
    /// 14.18) ponawia transakcję po każdym 40001: sprawdzone 2026-11-21 – ok. 1000 ponowień na sekundę przez ok. 50 s (ok. 45
    /// tys. wycofanych transakcji na jedno żądanie, dalej po zerwaniu połączenia przez klienta), klient nie dostawał
    /// odpowiedzi. Przez połączenie bezpośrednie konflikt przechodził (OwnerConfigTests), dlatego nikt tego nie widział.
    /// Pętla wraca, gdyby jakakolwiek funkcja znów zgłosiła kod z klasy 40 – pilnuje tego też SqlDataApiRulesTests.
    /// </summary>
    [Fact]
    public async Task StaleConfigVersion_ReturnsConflictAtOnce()
    {
        var api = await db.DataApiOwnerAsync();
        var started = DateTime.UtcNow;

        var conflict = await Assert.ThrowsAsync<DataApiException>(() => api.RpcAsync("owner_settings_update", ("p_version", 0L),
            ("p", new JsonObject { ["alertReminderHours"] = 24 })));

        AssertError(conflict, HttpStatusCode.Conflict, "PT409");
        Assert.StartsWith("Glebosaurus: conflict", conflict.Text);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "Konflikt wrócił dopiero po pętli ponowień Data API.");
    }

    /// <summary>
    /// Dowód, że ta droga widzi to, czego połączenie bezpośrednie nie widzi: na chwilę wraca wyzwalacz
    /// <c>glebosaurus.config_changed</c> sprzed poprawki 20261121230000_safe_update (UPDATE settings bez WHERE). Zmiana
    /// czujnika przez Data API kończy się wtedy błędem 21000, a ta sama funkcja przez połączenie bezpośrednie przechodzi.
    /// Definicja wyzwalacza wraca w <c>finally</c> (testy idą po kolei; następny przebieg i tak zakłada bazę od zera).
    /// </summary>
    [Fact]
    public async Task UpdateWithoutWhere_FailsThroughDataApi_ButPassesOnDirectConnection()
    {
        var api = await db.DataApiOwnerAsync();
        var sensor = await new Scenario(db, DateTimeOffset.UtcNow).SensorAsync("bezwhere");
        var original = (await db.AdminAsync("select to_jsonb(pg_get_functiondef('glebosaurus.config_changed()'::regprocedure))::text", null)).GetString()!;

        Task<JsonElement> UpdateThroughDataApi(long version, string name) => api.RpcAsync("owner_sensor_update", ("p_version", version),
            ("p_id", sensor), ("p_name", name), ("p_options", new JsonObject()));

        await ExecuteAsync("""
            create or replace function glebosaurus.config_changed() returns trigger
            language plpgsql set search_path = '' as $$
            begin
                update glebosaurus.settings set updated_at = now();
                return null;
            end
            $$;
            """);
        try
        {
            var error = await Assert.ThrowsAsync<DataApiException>(async () => await UpdateThroughDataApi(await ConfigVersionAsync(api), "Przez Data API"));
            AssertError(error, HttpStatusCode.BadRequest, "21000");
            Assert.Contains("WHERE clause", error.Text);

            await db.OwnerAsync("owner_sensor_update", api.Claims, null, ("p_version", await ConfigVersionAsync(api)), ("p_id", sensor),
                ("p_name", "Wprost"), ("p_options", new JsonObject()));
        }
        finally
        {
            await ExecuteAsync(original);
        }

        await UpdateThroughDataApi(await ConfigVersionAsync(api), "Po poprawce");
        Assert.Equal("Po poprawce", (await api.RpcAsync("owner_sensor", ("p_id", sensor))).GetProperty("sensor").Text("name"));
    }

    /// <summary>Środowisko, które odtwarzają te testy: Supabase ładuje safeupdate roli authenticator (każde żądanie Data API) i
    /// ogranicza czas zapytań roli authenticated – funkcja strony dłuższa niż ten limit zawiedzie (57014), choć przez
    /// połączenie bezpośrednie przejdzie.</summary>
    [Fact]
    public async Task DataApiRoles_LoadSafeUpdate_AndLimitStatementTime()
    {
        db.SkipIfUnavailable();
        var roles = await db.AdminAsync("""
            select jsonb_object_agg(rolname, to_jsonb(coalesce(rolconfig, '{}')))::text
              from pg_roles where rolname in ('authenticator', 'authenticated')
            """, null);

        Assert.Contains(roles.GetProperty("authenticator").EnumerateArray(),
            c => c.GetString()!.StartsWith("session_preload_libraries=", StringComparison.Ordinal) && c.GetString()!.Contains("safeupdate"));
        Assert.Contains(roles.GetProperty("authenticated").EnumerateArray(),
            c => c.GetString()!.StartsWith("statement_timeout=", StringComparison.Ordinal));
    }

    [Fact]
    public void Totp_MatchesRfc6238Example()
    {
        // RFC 6238, dodatek B (SHA1, sekret „12345678901234567890”): 94287082 przy T = 59 s, 07081804 przy 1111111109 s.
        Assert.Equal("287082", Totp.Code("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", DateTimeOffset.FromUnixTimeSeconds(59)));
        Assert.Equal("081804", Totp.Code("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", DateTimeOffset.FromUnixTimeSeconds(1111111109)));
    }

    private static void AssertError(DataApiException error, HttpStatusCode status, string code, string? text = null)
    {
        Assert.Equal(code, error.Code);
        Assert.Equal(status, error.Status);
        if (text is not null) Assert.Equal(text, error.Text);
    }

    private static async Task<long> ConfigVersionAsync(DataApiOwner api) =>
        (await api.RpcAsync("owner_config")).GetProperty("configVersion").GetInt64();

    private async Task ExecuteAsync(string sql)
    {
        await using var command = db.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static byte[] Gzip(string html)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(Encoding.UTF8.GetBytes(html));
        return buffer.ToArray();
    }

    private static string Gunzip(byte[] data)
    {
        using var reader = new StreamReader(new GZipStream(new MemoryStream(data), CompressionMode.Decompress), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
