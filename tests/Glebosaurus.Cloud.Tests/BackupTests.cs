using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glebosaurus.Cloud.Admin;

namespace Glebosaurus.Cloud.Tests;

/// <summary>
/// cloud backup na próbnym projekcie: jedna migawka wszystkich tabel (bez dzienników agentów) i odszyfrowane sekrety z
/// Vault – bez nich kopia nie odtworzy kluczy Tuya ani hasła Gmaila, bo surowy Vault szyfruje klucz projektu.
/// </summary>
public sealed class BackupTests(CloudDatabase db) : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"glebosaurus-backup-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private static string Entry(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task Backup_HasAllTablesAndDecryptedSecrets_FromOneSnapshot()
    {
        db.SkipIfUnavailable();
        var ct = TestContext.Current.CancellationToken;
        var s = new Scenario(db, CloudDatabase.NextTime());
        var sensor = await s.SensorAsync();
        var agent = await s.AgentAsync("kopia");
        await s.SyncAsync(agent, s.T0, new JsonObject
        {
            ["readings"] = new JsonArray(new JsonObject { ["sensor"] = sensor, ["at"] = s.At(-TimeSpan.FromMinutes(1)).ToString("O"), ["moisture"] = 44 })
        });
        await db.AgentAsync("log_put", s.T0, ("p_token", agent.Token), ("p_day", DateOnly.FromDateTime(s.T0.UtcDateTime)),
            ("p_content_gzip_base64", Convert.ToBase64String(Gzip("dziennik"))));
        var readings = (await db.AdminAsync("select to_jsonb(count(*))::text from glebosaurus.readings", null)).GetInt64();

        var result = await CloudBackup.CreateAsync(db.DataSource, db.Settings.ProjectRef, directory, TimeProvider.System, ct);

        Assert.Equal([result.Path], Directory.GetFiles(directory));   // bez pliku tymczasowego
        Assert.Equal(readings, result.Tables["readings"]);
        Assert.DoesNotContain("agent_logs", result.Tables.Keys);
        using var zip = ZipFile.OpenRead(result.Path);
        Assert.Null(zip.GetEntry("tables/agent_logs.csv"));
        foreach (var table in new[] { "agents", "alerts", "commands", "enrollment_codes", "environment", "leases", "mail_outbox", "readings",
                                      "schema_migrations", "sensors", "settings", "sightings" })
            Assert.NotNull(zip.GetEntry($"tables/{table}.csv"));

        var manifest = JsonNode.Parse(Entry(zip, "manifest.json"))!;
        Assert.Equal(db.Settings.ProjectRef, (string?)manifest["project"]);
        Assert.Equal(Migration.Embedded().Select(m => m.Checksum), manifest["migrations"]!.AsArray().Select(m => (string?)m!["checksum"]));
        Assert.Equal(readings, (long)manifest["tables"]!["readings"]!);

        var csv = Entry(zip, "tables/readings.csv");
        Assert.StartsWith("sensor_id,at,moisture,temp_tenths,battery,battery_state,agent_id,seq,received_at", csv);
        Assert.Contains(sensor + ",", csv);

        var secrets = JsonDocument.Parse(Entry(zip, "secrets.json")).RootElement;
        Assert.Equal(result.Secrets, secrets.GetArrayLength());
        var tuya = secrets.EnumerateArray().Single(e => e.GetProperty("name").GetString()!.EndsWith(sensor, StringComparison.Ordinal));
        Assert.Contains("a1b2c3d4e5f6", tuya.GetProperty("value").GetString());   // odszyfrowany klucz lokalny czujnika
    }

    private static byte[] Gzip(string text)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest)) gzip.Write(System.Text.Encoding.UTF8.GetBytes(text));
        return buffer.ToArray();
    }
}
