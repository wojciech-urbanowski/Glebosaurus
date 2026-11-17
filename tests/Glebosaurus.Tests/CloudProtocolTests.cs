using System.Text.Json;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Domain;

namespace Glebosaurus.Tests;

/// <summary>Protokół agent ↔ chmura bez sieci: nazwy pól JSON muszą zgadzać się z funkcjami SQL (supabase/migrations).</summary>
public sealed class CloudProtocolTests
{
    [Fact]
    public void SyncRequest_UsesFieldNamesOfSqlFunction()
    {
        var request = new SyncRequest(new AgentStatus("0.2.0", "windows") { Charging = true })
        {
            Sightings = [new("cisy", -78, 12.5, 60, null, 1)],
            Readings = [ReadingReport.From(new("cisy", Fixtures.T0.AddTicks(1234), 41, 21.45m, 80, 2))],
            Releases = [new("sensor:cisy", 3)],
            Claims = ["notifier"]
        };

        var json = JsonSerializer.SerializeToNode(request, CloudJsonContext.Default.SyncRequest)!.AsObject();

        Assert.Equal(1, (int)json["protocol"]!);
        Assert.Equal("windows", (string?)json["status"]!["platform"]);
        Assert.True((bool)json["status"]!["charging"]!);
        Assert.Equal(12.5, (double)json["sightings"]![0]!["heardSecondsAgo"]!);
        Assert.False(json["sightings"]![0]!.AsObject().ContainsKey("lastSuccessSecondsAgo"));
        var reading = json["readings"]![0]!;
        Assert.Equal(215, (int)reading["tempTenths"]!);
        Assert.Equal(2, (int)reading["batteryState"]!);
        Assert.Equal(Fixtures.T0, DateTimeOffset.Parse((string)reading["at"]!));   // pełne milisekundy, jak w SQLite
        Assert.Equal(3, (long)json["releases"]![0]!["epoch"]!);
        Assert.Equal("notifier", (string?)json["claims"]![0]);
    }

    [Fact]
    public void SyncResponse_ReadsJsonOfSqlFunction()
    {
        const string json = """
            {"protocol": 1, "serverTime": "2026-11-16T19:00:00.123456+00:00", "configVersion": 7,
             "agent": {"name": "bestia5", "canSendMail": true, "senderPriority": 2},
             "readings": {"received": 2, "inserted": 1, "rejected": [{"sensor": "x", "at": "2026-11-16T18:59:00+00:00", "reason": "unknown_sensor"}]},
             "released": [{"resource": "sensor:cisy", "released": true}],
             "leases": [{"resource": "sensor:trawa", "granted": true, "epoch": 4, "validSeconds": 1800},
                        {"resource": "notifier", "granted": false, "holder": "pi", "retryInSeconds": 420.5}],
             "commands": [{"id": 12, "kind": "read_now", "sensor": "trawa", "params": {}, "createdAt": "2026-11-16T18:59:30+00:00", "expiresInSeconds": 570}],
             "sensors": [{"sensor": "trawa", "holder": "bestia5", "holderExpiresInSeconds": 1800, "bestOther": {"agent": "android", "rssi": -70, "heardSecondsAgo": 20}},
                         {"sensor": "cisy", "holder": null, "holderExpiresInSeconds": null, "bestOther": null}],
             "notifier": {"holder": "pi", "epoch": 9, "expiresInSeconds": 120},
             "agents": [{"name": "pi", "canSendMail": true, "senderPriority": 1, "lastSeenSecondsAgo": 30}]}
            """;

        var response = JsonSerializer.Deserialize(json, CloudJsonContext.Default.SyncResponse)!;

        Assert.Equal(7, response.ConfigVersion);
        Assert.Equal(new AgentIdentity("bestia5", true, 2), response.Agent);
        Assert.Equal("unknown_sensor", Assert.Single(response.Readings.Rejected).Reason);
        Assert.Equal(4, response.Leases[0].Epoch);
        Assert.Equal("pi", response.Leases[1].Holder);
        Assert.Equal("read_now", Assert.Single(response.Commands).Kind);
        Assert.Equal(new OtherHearing("android", -70, 20), response.Sensors[0].BestOther);
        Assert.Null(response.Sensors[1].Holder);
        Assert.Equal(9, response.Notifier!.Epoch);
        Assert.Equal(30, Assert.Single(response.Agents).LastSeenSecondsAgo);
    }

    [Fact]
    public void Config_AndAlerts_ReadJsonOfSqlFunctions()
    {
        const string config = """
            {"protocol": 1, "configVersion": 3, "agent": {"name": "android", "canSendMail": false, "senderPriority": null},
             "settings": {"timeZone": "Europe/Warsaw", "readIntervalMinutes": 15, "scanWindowSeconds": 60, "reportTimes": ["07:00", "19:00"],
                          "reportCatchUpHours": 3, "alertReminderHours": 24, "logRetentionDays": 7},
             "mail": null,
             "sensors": [{"id": "cisy", "name": "Cisy", "mac": "DC:23:51:CC:03:C9", "enabled": true, "readIntervalMinutes": null,
                          "wateringBelowPercent": 25, "wateringClearAbovePercent": 35, "consecutiveReadings": 2, "lowBatteryPercent": 18,
                          "silentAfterHours": 6, "tuya": {"mac": "DC:23:51:CC:03:C9", "uuid": "07aaad78f40b0a74", "deviceId": "abc", "localKeyHex": "a1b2c3d4e5f6"}}]}
            """;
        var parsed = JsonSerializer.Deserialize(config, CloudJsonContext.Default.CloudConfig)!;
        var sensor = Assert.Single(parsed.Sensors);
        Assert.Equal(["07:00", "19:00"], parsed.Settings.ReportTimes);
        Assert.Null(sensor.ReadIntervalMinutes);
        Assert.Equal("a1b2c3d4e5f6", sensor.Tuya!.LocalKeyHex);
        Assert.DoesNotContain("a1b2", sensor.ToString());

        const string alerts = """
            [{"id": 5, "sensor": "cisy", "kind": "NeedsWater", "detail": "wilgotność 18%", "openedAt": "2026-11-16T07:00:00+00:00",
              "lastNotifiedAt": null, "resolvedAt": null}]
            """;
        var alert = Assert.Single(JsonSerializer.Deserialize(alerts, CloudJsonContext.Default.ListCloudAlert)!);
        Assert.Equal(AlertKind.NeedsWater, alert.Kind);
        Assert.Equal(new AlertRecord(5, "cisy", AlertKind.NeedsWater, "wilgotność 18%", new(2026, 11, 16, 7, 0, 0, TimeSpan.Zero), null, null),
            alert.ToRecord());
    }

    [Fact]
    public void Secrets_StayOutOfToString()
    {
        Assert.DoesNotContain("tajne-haslo", new MailCredentials("a@b.pl", "tajne-haslo", "https://hc-ping.com/x").ToString());
        Assert.DoesNotContain("glb_tajny", new EnrollResult(1, "pi", "glb_tajny", true, 1).ToString());
    }

    [Fact]
    public void ReadingsFeed_ReadsJsonOfSqlFunction()
    {
        const string json = """
            {"readings": [{"sensor": "cisy", "at": "2026-11-17T10:00:00.123+00:00", "moisture": 41, "tempTenths": -35,
                           "battery": null, "batteryState": 0}], "last": 4711, "more": true}
            """;

        var feed = JsonSerializer.Deserialize(json, CloudJsonContext.Default.ReadingsFeed)!;

        var reading = Assert.Single(feed.Readings).ToStored();
        Assert.Equal(new StoredReading("cisy", new DateTimeOffset(2026, 11, 17, 10, 0, 0, 123, TimeSpan.Zero), 41, -3.5m, null, 0), reading);
        Assert.Equal(4711, feed.Last);
        Assert.True(feed.More);
    }

    [Fact]
    public void Reading_RoundTripsThroughCloudForm()
    {
        var stored = new StoredReading("cisy", Fixtures.T0, 41, -3.5m, null, 0);

        Assert.Equal(stored, ReadingReport.From(stored).ToStored());
        Assert.Equal(-35, ReadingReport.From(stored).TempTenths);
        Assert.True(CloudProtocol.TryParseSensorResource("sensor:cisy", out var id) && id == "cisy");
        Assert.False(CloudProtocol.TryParseSensorResource("notifier", out _));
    }
}
