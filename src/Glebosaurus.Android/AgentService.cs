using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Android;

/// <summary>Uprawnienia, bez których agent nie słyszy czujników (Android 12+: Bluetooth; 10–11: lokalizacja do skanu BLE).</summary>
internal static class AgentPermissions
{
    public static string[] Radio => OperatingSystem.IsAndroidVersionAtLeast(31)
        ? [Manifest.Permission.BluetoothScan, Manifest.Permission.BluetoothConnect]
        : [Manifest.Permission.AccessFineLocation];

    /// <summary>Wszystko, o co prosi ekran: radio i (Android 13+) powiadomienia.</summary>
    public static string[] All => OperatingSystem.IsAndroidVersionAtLeast(33) ? [.. Radio, Manifest.Permission.PostNotifications] : Radio;

    public static bool Granted(Context context, string permission) => context.CheckSelfPermission(permission) == Permission.Granted;

    public static bool RadioGranted(Context context) => Radio.All(p => Granted(context, p));

    public static bool IgnoringBatteryOptimizations(Context context) =>
        context.GetSystemService(Context.PowerService) is PowerManager power && power.IsIgnoringBatteryOptimizations(context.PackageName);
}

/// <summary>
/// Usługa pierwszoplanowa typu <c>connectedDevice</c> ze stałym powiadomieniem (ARCHITEKTURA.md, zasada 12): trzyma agenta przy
/// życiu przy zgaszonym ekranie i blokadę uśpienia procesora – bez niej liczniki czasu potrafią
/// stanąć na długie minuty. Zatrzymanie z ekranu przechodzi przez
/// <see cref="ActionStop"/>: agent kończy bieżący odczyt i oddaje dzierżawy, dopiero potem usługa znika.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeConnectedDevice)]
internal sealed class AgentService : Service
{
    public const string ActionStart = "pl.glebosaurus.agent.START";
    public const string ActionStop = "pl.glebosaurus.agent.STOP";
    private const string Channel = "agent";
    private const int NotificationId = 1;
    /// <summary>
    /// Sprawdzanie tekstu powiadomienia – sam stan w pamięci (bez bazy i bez chmury), więc tanio; system dostaje tylko zmieniony
    /// tekst (przy zwykłej pracy rzadko: liczba czytanych czujników, kłopot z chmurą). Zmiana fazy agenta odświeża od razu.
    /// </summary>
    private static readonly TimeSpan NotificationRefresh = TimeSpan.FromMinutes(1);

    private readonly Lock notificationGate = new();
    private PowerManager.WakeLock? wakeLock;
    private Timer? refresh;
    private string? shownText;
    private bool stopping;

    /// <summary>Uruchamia usługę (z ekranu albo po starcie telefonu); bez uprawnień radia nic nie robi – zwraca false.</summary>
    public static bool Start(Context context)
    {
        if (!AgentPermissions.RadioGranted(context)) return false;
        context.StartForegroundService(new Intent(context, typeof(AgentService)).SetAction(ActionStart));
        return true;
    }

    public static void Stop(Context context) =>
        context.StartService(new Intent(context, typeof(AgentService)).SetAction(ActionStop));

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var host = AgentHost.Get(this);
        if (intent?.Action == ActionStop)
        {
            if (!stopping) _ = StopAgentAsync(host);
            return StartCommandResult.NotSticky;
        }
        // Android 14+: usługa connectedDevice bez uprawnień Bluetooth kończy się wyjątkiem – lepiej nie startować wcale.
        if (!AgentPermissions.RadioGranted(this))
        {
            host.Loggers.CreateLogger("Glebosaurus").LogWarning("Usługa agenta nie wystartowała: brak uprawnień Bluetooth (otwórz aplikację).");
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        EnsureChannel();
        var initial = AgentHost.Describe(host.State);
        lock (notificationGate) shownText = initial;
        StartForeground(NotificationId, Notification(initial), ForegroundService.TypeConnectedDevice);
        if (wakeLock is null && GetSystemService(PowerService) is PowerManager power)
        {
            wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "Glebosaurus:agent");
            wakeLock!.SetReferenceCounted(false);
            wakeLock.Acquire();
        }
        host.StateChanged -= OnHostStateChanged;
        host.StateChanged += OnHostStateChanged;
        refresh ??= new Timer(_ => RefreshNotification(), null, TimeSpan.FromSeconds(5), NotificationRefresh);
        host.Start();
        // Usługa ubita przez system wraca sama (bez intencji – wtedy też startujemy agenta).
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        var host = AgentHost.Get(this);
        host.StateChanged -= OnHostStateChanged;
        refresh?.Dispose();
        refresh = null;
        if (!stopping && host.IsRunning) _ = host.StopAsync();
        if (wakeLock is { IsHeld: true }) wakeLock.Release();
        wakeLock = null;
        base.OnDestroy();
    }

    private async Task StopAgentAsync(AgentHost host)
    {
        stopping = true;
        UpdateNotification("Zatrzymuję – kończę bieżący odczyt i oddaję czujniki…");
        await host.StopAsync();
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
    }

    private void OnHostStateChanged() => RefreshNotification();

    private void RefreshNotification()
    {
        if (stopping) return;
        try { UpdateNotification(AgentHost.Get(this).Summary()); }
        catch (Exception e) when (e is not OutOfMemoryException) { /* powiadomienie nie może zatrzymać agenta */ }
    }

    /// <summary>Tylko zmieniony tekst trafia do systemu (każde <c>Notify</c> to praca usługi systemowej i odrysowanie).</summary>
    private void UpdateNotification(string text)
    {
        lock (notificationGate)
        {
            if (text == shownText) return;
            shownText = text;
        }
        if (GetSystemService(NotificationService) is NotificationManager manager) manager.Notify(NotificationId, Notification(text));
    }

    private void EnsureChannel()
    {
        if (GetSystemService(NotificationService) is not NotificationManager manager) return;
        var channel = new NotificationChannel(Channel, "Praca agenta", NotificationImportance.Low)
        {
            Description = "Stałe powiadomienie, gdy telefon zbiera odczyty czujników."
        };
        channel.SetShowBadge(false);
        manager.CreateNotificationChannel(channel);
    }

    private Notification Notification(string text)
    {
        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        return new Notification.Builder(this, Channel)
            .SetSmallIcon(Resource.Drawable.ic_stat_sprout)!
            .SetContentTitle("Glebosaurus zbiera odczyty")!
            .SetContentText(text)!
            .SetStyle(new Notification.BigTextStyle().BigText(text))!
            .SetOngoing(true)!
            .SetOnlyAlertOnce(true)!
            .SetContentIntent(open)!
            .Build()!;
    }
}

/// <summary>Start agenta po restarcie telefonu i po aktualizacji aplikacji – jeśli był włączony i telefon jest zarejestrowany.</summary>
[BroadcastReceiver(Exported = true, Enabled = true)]
[IntentFilter([Intent.ActionBootCompleted, Intent.ActionMyPackageReplaced])]
internal sealed class BootReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null) return;
        var host = AgentHost.Get(context);
        var log = host.Loggers.CreateLogger("Glebosaurus");
        if (!host.Storage.Enabled || !host.Storage.IsEnrolled) return;
        if (AgentService.Start(context))
            log.LogInformation("Start agenta po zdarzeniu systemu ({Action}).", intent?.Action);
        else
            log.LogWarning("Agent nie wystartował po zdarzeniu systemu ({Action}): brak uprawnień Bluetooth.", intent?.Action);
    }
}
