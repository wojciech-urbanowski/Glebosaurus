namespace Glebosaurus.Cloud.Tests;

/// <summary>
/// Blokada nadawcy i kolejka maili: maila rezerwuje tylko obecny nadawca z aktualną epoką, każdy klucz najwyżej raz
/// (uśpiony nadawca, restart w trakcie wysyłki, przejęcie blokady). Każdy test ma własny odcinek czasu, bo blokada jest
/// jedna na całą bazę.
/// </summary>
public sealed class NotifierTests(CloudDatabase db)
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Collector_CannotTakeNotifierLock()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var collector = await s.AgentAsync("android");

        var claim = await s.ClaimAsync(collector, "notifier", s.T0);

        Assert.False(claim.Granted());
        Assert.Equal("not_sender", claim.Text("error"));
    }

    [Fact]
    public async Task NotifierLock_LastsFiveMinutesPlusGrace()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var a = await s.AgentAsync("pi", canSendMail: true, priority: 1);
        var b = await s.AgentAsync("bestia", canSendMail: true, priority: 2);

        var first = await s.ClaimAsync(a, "notifier", s.T0);
        Assert.True(first.Granted());
        Assert.Equal(300, first.GetProperty("validSeconds").GetDouble());

        Assert.False((await s.ClaimAsync(b, "notifier", s.At(6 * Minute))).Granted());
        var taken = await s.ClaimAsync(b, "notifier", s.At(7 * Minute));
        Assert.True(taken.Granted());
        Assert.Equal(first.Epoch() + 1, taken.Epoch());
    }

    [Fact]
    public async Task MailKey_IsClaimedOnce_AndMarkedSent()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var a = await s.AgentAsync("pi", canSendMail: true);
        var epoch = (await s.ClaimAsync(a, "notifier", s.T0)).Epoch();
        var key = "report|" + Scenario.Unique("r");

        var claimed = await s.MailClaimAsync(a, key, epoch, s.At(Minute));
        Assert.True(claimed.GetProperty("claimed").GetBoolean());

        var again = await s.MailClaimAsync(a, key, epoch, s.At(Minute));
        Assert.False(again.GetProperty("claimed").GetBoolean());
        Assert.Equal("already_claimed", again.Text("reason"));

        var sent = await db.AgentAsync("mail_sent", s.At(2 * Minute), ("p_token", a.Token), ("p_key", key), ("p_message_id", "<id@glebosaurus>"));
        Assert.True(sent.GetBoolean());
        var afterSend = await s.MailClaimAsync(a, key, epoch, s.At(3 * Minute));
        Assert.Equal("already_claimed", afterSend.Text("reason"));
        Assert.NotNull(afterSend.Text("sentAt"));
    }

    [Fact]
    public async Task MailClaim_NeedsCurrentEpoch()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var a = await s.AgentAsync("pi", canSendMail: true);
        var epoch = (await s.ClaimAsync(a, "notifier", s.T0)).Epoch();

        Assert.Equal("not_holder", (await s.MailClaimAsync(a, "alert|" + Scenario.Unique("x"), epoch - 1, s.At(Minute))).Text("reason"));
        Assert.Equal("not_holder", (await s.MailClaimAsync(a, "alert|" + Scenario.Unique("x"), epoch + 1, s.At(Minute))).Text("reason"));
    }

    [Fact]
    public async Task SleepingNotifier_CannotSendAfterWakingUp()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var bestia = await s.AgentAsync("bestia", canSendMail: true, priority: 2);
        var pi = await s.AgentAsync("pi", canSendMail: true, priority: 1);
        var sleeping = (await s.ClaimAsync(bestia, "notifier", s.T0)).Epoch();

        // Blokada wygasła, a nikt jej nie przejął: uśpiony nadawca i tak nic nie wyśle.
        Assert.Equal("not_holder", (await s.MailClaimAsync(bestia, "log|" + Scenario.Unique("d"), sleeping, s.At(6 * Minute))).Text("reason"));

        var current = (await s.ClaimAsync(pi, "notifier", s.At(8 * Minute))).Epoch();
        var key = "report|" + Scenario.Unique("r");
        Assert.Equal("not_holder", (await s.MailClaimAsync(bestia, key, sleeping, s.At(9 * Minute))).Text("reason"));
        Assert.True((await s.MailClaimAsync(pi, key, current, s.At(9 * Minute))).GetProperty("claimed").GetBoolean());
    }

    [Fact]
    public async Task RestartDuringSending_NeverDuplicates_UnlessSenderAbandons()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var a = await s.AgentAsync("bestia", canSendMail: true, priority: 2);
        var b = await s.AgentAsync("pi", canSendMail: true, priority: 1);
        var epochA = (await s.ClaimAsync(a, "notifier", s.T0)).Epoch();
        var key = "alert|" + Scenario.Unique("s") + "|NeedsWater|2026-11-17T07:00:00Z";

        Assert.True((await s.MailClaimAsync(a, key, epochA, s.At(Minute))).GetProperty("claimed").GetBoolean());
        // Restart przed potwierdzeniem: ten sam nadawca (blokada odnowiona) nie wyśle drugi raz…
        Assert.Equal(epochA, (await s.ClaimAsync(a, "notifier", s.At(2 * Minute))).Epoch());
        Assert.Equal("already_claimed", (await s.MailClaimAsync(a, key, epochA, s.At(2 * Minute))).Text("reason"));
        // …ani nowy nadawca po przejęciu blokady.
        var epochB = (await s.ClaimAsync(b, "notifier", s.At(10 * Minute))).Epoch();
        Assert.Equal("already_claimed", (await s.MailClaimAsync(b, key, epochB, s.At(10 * Minute))).Text("reason"));

        // Gdy SMTP na pewno nie przyjął maila, rezerwujący oddaje klucz i mail może wyjść.
        Assert.True((await db.AgentAsync("mail_abandon", s.At(11 * Minute), ("p_token", a.Token), ("p_key", key))).GetBoolean());
        Assert.True((await s.MailClaimAsync(b, key, epochB, s.At(11 * Minute))).GetProperty("claimed").GetBoolean());
    }

    [Fact]
    public async Task MailCredentials_OnlyForCurrentNotifier()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var sender = await s.AgentAsync("pi", canSendMail: true);
        var collector = await s.AgentAsync("android");
        await db.AdminAsync("select to_jsonb(null::text) from glebosaurus.admin_mail_secret('gmail-app-password', @v)", null, ("v", "abcd efgh ijkl mnop"));
        await db.AdminAsync("select to_jsonb(null::text) from glebosaurus.admin_settings_update(@p)", null,
            ("p", new System.Text.Json.Nodes.JsonObject { ["gmailAddress"] = "nadawca@gmail.com" }));
        var epoch = (await s.ClaimAsync(sender, "notifier", s.T0)).Epoch();

        var credentials = await db.AgentAsync("mail_credentials", s.At(Minute), ("p_token", sender.Token), ("p_epoch", epoch));
        Assert.Equal("abcd efgh ijkl mnop", credentials.Text("appPassword"));
        Assert.Equal("nadawca@gmail.com", credentials.Text("gmailAddress"));

        await Scenario.FailsAsync("42501", () => db.AgentAsync("mail_credentials", s.At(Minute), ("p_token", sender.Token), ("p_epoch", epoch + 1)));
        await Scenario.FailsAsync("42501", () => db.AgentAsync("mail_credentials", s.At(6 * Minute), ("p_token", sender.Token), ("p_epoch", epoch)));
        await Scenario.FailsAsync("42501", () => db.AgentAsync("mail_credentials", s.At(Minute), ("p_token", collector.Token), ("p_epoch", epoch)));
    }

    [Fact]
    public async Task InvalidMailKey_IsRejected()
    {
        db.SkipIfUnavailable();
        var s = new Scenario(db, CloudDatabase.NextTime());
        var a = await s.AgentAsync("pi", canSendMail: true);
        var epoch = (await s.ClaimAsync(a, "notifier", s.T0)).Epoch();

        await Scenario.FailsAsync("22023", () => s.MailClaimAsync(a, "spam|cokolwiek", epoch, s.T0));
        await Scenario.FailsAsync("22023", () => s.MailClaimAsync(a, "report|", epoch, s.T0));
    }
}
