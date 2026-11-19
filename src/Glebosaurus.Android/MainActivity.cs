using System.Collections.Concurrent;
using System.Globalization;
using Android.Bluetooth;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Provider;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Glebosaurus.Core;
using Glebosaurus.Core.Cloud;
using Glebosaurus.Core.Monitoring;
using Glebosaurus.Tuya;
using Xamarin.Google.MLKit.Vision.CodeScanner;
using AndroidUri = Android.Net.Uri;

namespace Glebosaurus.Android;

/// <summary>
/// Jedyny ekran aplikacji (ARCHITEKTURA.md, zasada 12): rejestracja kodem QR, przygotowanie telefonu (uprawnienia, bateria,
/// Autostart MIUI), start i zatrzymanie agenta, stan (ten sam co w panelu WWW, jako tekst), podgląd zasięgu czujników do
/// wyboru miejsca na telefon i ostatnie wpisy dziennika. Konfiguracji nie zmienia – ta jest w chmurze.
/// </summary>
[Activity(Label = "Glebosaurus", MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTop,
    Theme = "@android:style/Theme.DeviceDefault.DayNight", ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataScheme = EnrollmentLink.Scheme, DataHost = "enroll")]
public sealed class MainActivity : Activity
{
    private const int PermissionRequest = 1;
    /// <summary>Podgląd zasięgu odświeża się co sekundę (telefon jest przenoszony), reszta ekranu co 3 s.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SurveyDuration = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MessageLifetime = TimeSpan.FromMinutes(1);

    private AgentHost host = null!;
    private LinearLayout buttons = null!;
    private LinearLayout content = null!;
    private TextView message = null!;
    private Timer? timer;
    private bool refreshing;
    private long lastRefresh;
    private long messageShownAt;
    private string? buttonsKey;
    private CancellationTokenSource? survey;
    private DateTimeOffset surveyStarted;
    private readonly ConcurrentDictionary<string, SurveyEntry> surveyed = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<KnownSensor> surveySensors = [];

    private sealed record SurveyEntry(string Address, int? LastRssi, int? Average, int Packets, DateTimeOffset LastSeenAt, string? Uuid, bool? Bound);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        host = AgentHost.Get(this);
        // Tytuł jest w treści – pasek akcji tematu go tylko dublował (próba na Mi 11, 2026-11-19).
        ActionBar?.Hide();
        // Jasny motyw: ciemne ikony paska stanu (system rysował białe na jasnym tle).
        var night = (Resources?.Configuration?.UiMode & global::Android.Content.Res.UiMode.NightMask) == global::Android.Content.Res.UiMode.NightYes;
        if (!night && OperatingSystem.IsAndroidVersionAtLeast(30) && Window?.InsetsController is { } bars)
        {
            var light = (int)(WindowInsetsControllerAppearance.LightStatusBars | WindowInsetsControllerAppearance.LightNavigationBars);
            bars.SetSystemBarsAppearance(light, light);
        }
        SetContentView(BuildLayout());
        HandleLink(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleLink(intent);
    }

    protected override void OnResume()
    {
        base.OnResume();
        lastRefresh = 0;
        timer = new Timer(_ => RunOnUiThread(() => _ = RefreshAsync(tick: true)), null, TimeSpan.Zero, Tick);
    }

    protected override void OnPause()
    {
        timer?.Dispose();
        timer = null;
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        survey?.Cancel();
        base.OnDestroy();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, [GeneratedEnum] Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        _ = RefreshAsync();
    }

    // ---- układ ---------------------------------------------------------------------------------------------------------

    private View BuildLayout()
    {
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var pad = Dp(16);
        root.SetPadding(pad, pad, pad, pad);

        var title = new TextView(this) { Text = "Glebosaurus" };
        title.SetTextSize(ComplexUnitType.Sp, 24);
        title.SetTypeface(null, TypefaceStyle.Bold);
        root.AddView(title);
        var subtitle = new TextView(this) { Text = $"agent zbierający odczyty · wersja {AppInfo.Version}" };
        subtitle.SetTextSize(ComplexUnitType.Sp, 13);
        root.AddView(subtitle);

        buttons = new LinearLayout(this) { Orientation = Orientation.Vertical };
        buttons.SetPadding(0, Dp(12), 0, Dp(4));
        root.AddView(buttons);

        message = new TextView(this) { Visibility = ViewStates.Gone };
        message.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
        message.SetBackgroundColor(Color.Argb(40, 120, 160, 220));
        root.AddView(message);

        content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.AddView(content);

        var scroll = new ScrollView(this);
        scroll.AddView(root);
        // Android 15+: aplikacja rysuje pod paskami systemu – odsuwamy treść o ich wysokość.
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
            scroll.SetOnApplyWindowInsetsListener(new InsetsPadding(root, pad));
        return scroll;
    }

    private sealed class InsetsPadding(View target, int pad) : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View view, WindowInsets insets)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
                target.SetPadding(pad + bars.Left, pad + bars.Top, pad + bars.Right, pad + bars.Bottom);
            }
            return insets;
        }
    }

    private int Dp(int value) => (int)Math.Round(value * (Resources?.DisplayMetrics?.Density ?? 1f));

    private void SetButtons(string key, params (string Label, Action Click)[][] rows)
    {
        if (key == buttonsKey) return;
        buttonsKey = key;
        buttons.RemoveAllViews();
        foreach (var row in rows)
        {
            var line = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            foreach (var (label, click) in row)
            {
                var button = new Button(this) { Text = label };
                button.SetAllCaps(false);
                button.Click += (_, _) => click();
                line.AddView(button, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            }
            buttons.AddView(line);
        }
    }

    private void ShowMessage(string? text)
    {
        message.Text = text;
        message.Visibility = text is null ? ViewStates.Gone : ViewStates.Visible;
        messageShownAt = TimeProvider.System.GetTimestamp();
    }

    private void AddSection(string heading, IEnumerable<string> lines, bool warning = false, bool monospace = false)
    {
        var header = new TextView(this) { Text = heading };
        header.SetTypeface(null, TypefaceStyle.Bold);
        header.SetTextSize(ComplexUnitType.Sp, 16);
        header.SetPadding(Dp(8), Dp(14), Dp(8), Dp(4));
        if (warning) header.SetBackgroundColor(Color.Argb(70, 230, 120, 60));
        content.AddView(header);
        var body = new TextView(this) { Text = string.Join("\n", lines) };
        body.SetPadding(Dp(8), 0, Dp(8), 0);
        body.SetTextIsSelectable(true);
        if (monospace)
        {
            body.Typeface = Typeface.Monospace;
            body.SetTextSize(ComplexUnitType.Sp, 11);
        }
        content.AddView(body);
    }

    // ---- odświeżanie ---------------------------------------------------------------------------------------------------

    private async Task RefreshAsync(bool tick = false)
    {
        if (refreshing || IsFinishing) return;
        if (tick && survey is null && lastRefresh != 0 && TimeProvider.System.GetElapsedTime(lastRefresh) < Refresh) return;
        lastRefresh = TimeProvider.System.GetTimestamp();
        refreshing = true;
        try
        {
            // Komunikat po akcji (np. „Agent startuje…”) znika po minucie – dalej mówi już stan agenta.
            if (message.Visibility == ViewStates.Visible && TimeProvider.System.GetElapsedTime(messageShownAt) > MessageLifetime) ShowMessage(null);
            var storage = host.Storage;
            if (survey is not null) SetButtons("survey", [("Zakończ podgląd zasięgu", StopSurvey), ("Od nowa", RestartSurvey)]);
            else if (!storage.IsEnrolled)
                SetButtons("enroll", [("Zeskanuj kod QR", ScanCode), ("Wklej kod", PasteCode)], [("Zasięg czujników", StartSurvey)]);
            else
                SetButtons(host.IsRunning ? "running" : "stopped",
                    [(host.IsRunning ? "Zatrzymaj agenta" : "Uruchom agenta", host.IsRunning ? StopAgent : StartAgent), ("Zasięg czujników", StartSurvey)],
                    [("Odczytaj teraz", ReadNow), ("Zarejestruj ponownie", ScanCode)]);

            var sections = survey is null ? await host.ScreenAsync(CancellationToken.None) : null;
            content.RemoveAllViews();
            if (survey is not null) ShowSurvey();
            else
            {
                if (sections is null) AddSection("Agent", [AgentHost.Describe(host.State), .. EnrollmentHint()], host.State.Phase is HostPhase.Revoked or HostPhase.Retrying);
                else foreach (var section in sections) AddSection(section.Title, section.Lines, section.Warning);
                if (sections is null && AgentScreenText.PhoneSection(PhoneState.Read(this, host.Radio.State)) is { } phone)
                    AddSection(phone.Title, phone.Lines, phone.Warning);
            }
            ShowSetup();
            var log = host.RecentLog.Lines();
            AddSection("Dziennik (ostatnie wpisy)", log.Count == 0 ? ["(pusto)"] : log.TakeLast(40).Reverse(), monospace: true);
        }
        catch (Exception e)
        {
            ShowMessage($"Błąd ekranu: {e.Message}");
        }
        finally { refreshing = false; }
    }

    private IEnumerable<string> EnrollmentHint()
    {
        if (host.Storage.IsEnrolled) yield break;
        yield return "";
        yield return "Właściciel na komputerze tworzy agenta i kod QR:";
        yield return "glebosaurus cloud agent --admin PLIK --name NAZWA --confirm --qr kod.png";
        yield return "Potem tutaj „Zeskanuj kod QR” (albo „Wklej kod”, jeśli inny skaner pokazał tekst glebosaurus://enroll?…).";
    }

    /// <summary>Przygotowanie telefonu (README, „Android”): uprawnienia, Bluetooth, bateria, Autostart MIUI.</summary>
    private void ShowSetup()
    {
        var radio = AgentPermissions.RadioGranted(this);
        var notifications = !OperatingSystem.IsAndroidVersionAtLeast(33) || AgentPermissions.Granted(this, global::Android.Manifest.Permission.PostNotifications);
        var bluetooth = host.Radio.State == "ok";
        var battery = AgentPermissions.IgnoringBatteryOptimizations(this);
        var lines = new List<string>
        {
            (radio ? "✓" : "✗") + (OperatingSystem.IsAndroidVersionAtLeast(31) ? " Uprawnienia Bluetooth" : " Lokalizacja (Android 10–11 wymaga jej do skanu BLE)"),
            (notifications ? "✓" : "✗") + " Powiadomienia (stałe powiadomienie agenta)",
            (bluetooth ? "✓" : "✗") + $" Bluetooth: {host.Radio.State}",
            (battery ? "✓" : "✗") + " Bez optymalizacji baterii",
            "• MIUI/HyperOS: Autostart włączony i oszczędzanie baterii „Bez ograniczeń” – sprawdź ręcznie (przycisk „Autostart”).",
            "• Zablokuj aplikację w ostatnich aplikacjach (kłódka), telefon zostaw na ładowarce, bez aplikacji Smart Life."
        };
        AddSection("Przygotowanie telefonu", lines, !(radio && bluetooth && battery));
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        foreach (var (label, click) in new (string, Action)[] { ("Uprawnienia", RequestPermissions), ("Bateria", OpenBatterySettings), ("Autostart", OpenAutostart) })
        {
            var button = new Button(this) { Text = label };
            button.SetAllCaps(false);
            button.Click += (_, _) => click();
            row.AddView(button, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        }
        content.AddView(row);
        if (!bluetooth)
        {
            var enable = new Button(this) { Text = "Ustawienia Bluetooth" };
            enable.SetAllCaps(false);
            enable.Click += (_, _) => StartActivity(new Intent(Settings.ActionBluetoothSettings));
            content.AddView(enable);
        }
    }

    // ---- agent ---------------------------------------------------------------------------------------------------------

    private void StartAgent()
    {
        if (!AgentPermissions.RadioGranted(this))
        {
            ShowMessage("Najpierw uprawnienia Bluetooth (przycisk „Uprawnienia”).");
            RequestPermissions();
            return;
        }
        host.Storage.Enabled = true;
        AgentService.Start(this);
        ShowMessage("Agent startuje – pierwsza synchronizacja w ciągu minuty, nasłuch czujników co 3 min.");
        buttonsKey = null;
    }

    private void StopAgent()
    {
        host.Storage.Enabled = false;
        AgentService.Stop(this);
        ShowMessage("Zatrzymuję agenta – kończy bieżący odczyt i oddaje czujniki innym agentom (do ok. 30 s).");
        buttonsKey = null;
    }

    private async void ReadNow()
    {
        try { ShowMessage(await host.RequestReadAllAsync(CancellationToken.None)); }
        catch (Exception e) { ShowMessage($"Nie udało się zlecić odczytu: {e.Message}"); }
    }

    // ---- rejestracja ---------------------------------------------------------------------------------------------------

    private void ScanCode()
    {
        try
        {
            GmsBarcodeScanning.GetClient(this).StartScan()
                .AddOnSuccessListener(new ScanListener(this))
                .AddOnFailureListener(new ScanListener(this))
                .AddOnCanceledListener(new ScanListener(this));
        }
        catch (Exception e)
        {
            ShowMessage($"Skaner kodów QR niedostępny ({e.Message}) – użyj „Wklej kod”.");
        }
    }

    private sealed class ScanListener(MainActivity owner) : Java.Lang.Object,
        global::Android.Gms.Tasks.IOnSuccessListener, global::Android.Gms.Tasks.IOnFailureListener, global::Android.Gms.Tasks.IOnCanceledListener
    {
        public void OnSuccess(Java.Lang.Object? result)
        {
            var text = result?.JavaCast<Xamarin.Google.MLKit.Vision.Barcode.Common.Barcode>()?.RawValue;
            owner.RunOnUiThread(() => owner.OfferEnrollment(text ?? ""));
        }

        public void OnFailure(Java.Lang.Exception e) =>
            owner.RunOnUiThread(() => owner.ShowMessage($"Skaner kodów QR nie zadziałał ({e.Message}) – użyj „Wklej kod”."));

        public void OnCanceled() { }
    }

    private void PasteCode()
    {
        var input = new EditText(this) { Hint = "glebosaurus://enroll?…" };
        input.SetMinLines(3);
        if (GetSystemService(ClipboardService) is ClipboardManager clipboard
            && clipboard.PrimaryClip is { ItemCount: > 0 } clip
            && clip.GetItemAt(0)?.Text is { } pasted && pasted.TrimStart().StartsWith(EnrollmentLink.Scheme, StringComparison.OrdinalIgnoreCase))
            input.Text = pasted;
        new AlertDialog.Builder(this)
            .SetTitle("Kod rejestracji")!
            .SetMessage("Wklej odnośnik glebosaurus://enroll?… z kodu QR.")!
            .SetView(input)!
            .SetPositiveButton("Dalej", (_, _) => OfferEnrollment(input.Text ?? ""))!
            .SetNegativeButton("Anuluj", (_, _) => { })!
            .Show();
    }

    private void HandleLink(Intent? intent)
    {
        if (intent?.Action == Intent.ActionView && intent.DataString is { } link) OfferEnrollment(link);
    }

    private void OfferEnrollment(string text)
    {
        EnrollmentLink link;
        try { link = EnrollmentLink.Parse(text); }
        catch (InvalidDataException e)
        {
            ShowMessage(e.Message);
            return;
        }
        var replacing = host.Storage.Cloud is { } cloud
            ? $"\n\nTelefon jest już zarejestrowany ({host.Storage.AgentName} w {cloud.Url.Host}) – nowy token zastąpi obecny."
            : "";
        new AlertDialog.Builder(this)
            .SetTitle("Rejestracja agenta")!
            .SetMessage($"Zarejestrować ten telefon jako agenta w projekcie {link.ProjectUrl.Host}?{replacing}")!
            .SetPositiveButton("Zarejestruj", (_, _) => _ = EnrollAsync(link))!
            .SetNegativeButton("Anuluj", (_, _) => { })!
            .Show();
    }

    private async Task EnrollAsync(EnrollmentLink link)
    {
        ShowMessage("Rejestruję…");
        try
        {
            var enrolled = await host.EnrollAsync(link, CancellationToken.None);
            var role = enrolled.CanSendMail
                ? "\nUwaga: w chmurze ten agent ma rolę nadawcy, a telefon nie wysyła maili – właściciel może ją zdjąć: cloud agent --name " + enrolled.Agent
                : "";
            ShowMessage($"Zarejestrowano agenta „{enrolled.Agent}” – zbiera odczyty.{role}");
            buttonsKey = null;
            if (AgentPermissions.RadioGranted(this)) StartAgent();
            else RequestPermissions();
        }
        catch (CloudApiException e) when (e.Kind == CloudErrorKind.Unauthorized)
        {
            ShowMessage("Kod rejestracji jest nieznany, już użyty albo wygasł – poproś o nowy (cloud agent … --confirm --qr).");
        }
        catch (Exception e)
        {
            ShowMessage($"Rejestracja nieudana: {e.Message}");
        }
    }

    // ---- ustawienia telefonu ------------------------------------------------------------------------------------------

    private void RequestPermissions()
    {
        var missing = AgentPermissions.All.Where(p => !AgentPermissions.Granted(this, p)).ToArray();
        if (missing.Length > 0) RequestPermissions(missing, PermissionRequest);
        else ShowMessage("Uprawnienia są nadane.");
    }

    private void OpenBatterySettings()
    {
        if (!AgentPermissions.IgnoringBatteryOptimizations(this))
        {
            try
            {
                StartActivity(new Intent(Settings.ActionRequestIgnoreBatteryOptimizations, AndroidUri.Parse("package:" + PackageName)));
                return;
            }
            catch (ActivityNotFoundException) { }
        }
        OpenAppSettings();
    }

    /// <summary>MIUI/HyperOS: lista Autostartu w „Zabezpieczeniach”; gdzie indziej – ustawienia aplikacji.</summary>
    private void OpenAutostart()
    {
        try
        {
            StartActivity(new Intent().SetComponent(new ComponentName("com.miui.securitycenter", "com.miui.permcenter.autostart.AutoStartManagementActivity")));
            ShowMessage("Włącz Autostart dla Glebosaurusa. Potem w ustawieniach aplikacji: Oszczędzanie baterii → Bez ograniczeń.");
        }
        catch (Exception e) when (e is ActivityNotFoundException or Java.Lang.SecurityException)
        {
            OpenAppSettings();
        }
    }

    private void OpenAppSettings() =>
        StartActivity(new Intent(Settings.ActionApplicationDetailsSettings, AndroidUri.Parse("package:" + PackageName)));

    // ---- podgląd zasięgu -------------------------------------------------------------------------------------------------

    private async void StartSurvey()
    {
        if (!AgentPermissions.RadioGranted(this))
        {
            RequestPermissions();
            return;
        }
        surveySensors = await host.KnownSensorsAsync(CancellationToken.None);
        surveyed.Clear();
        surveyStarted = DateTimeOffset.UtcNow;
        var cancel = new CancellationTokenSource();
        survey = cancel;
        buttonsKey = null;
        ShowMessage("Podgląd zasięgu: przenoś telefon i patrz na moc sygnału (bliżej zera = lepiej; odczyty udawały się przy -75…-81 dBm). " +
                    "Agent czeka w tym czasie z odczytami.");
        try
        {
            await host.Radio.SurveyAsync(SurveyDuration, device => surveyed[device.Address] = new SurveyEntry(device.Address, device.LastRssi,
                device.Signal.Average, device.Packets, device.LastSeenAt ?? DateTimeOffset.UtcNow,
                surveyed.TryGetValue(device.Address, out var known) && known.Uuid is not null ? known.Uuid : device.Identity?.Uuid,
                device.Identity?.IsBound ?? known?.Bound), cancel.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { RunOnUiThread(() => ShowMessage($"Podgląd zasięgu przerwany: {e.Message}")); }
        finally
        {
            if (ReferenceEquals(survey, cancel)) survey = null;
            buttonsKey = null;
            cancel.Dispose();
        }
    }

    private void StopSurvey()
    {
        survey?.Cancel();
        ShowMessage(null);
    }

    private void RestartSurvey()
    {
        survey?.Cancel();
        survey = null;
        StartSurvey();
    }

    private void ShowSurvey()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = surveyed.Values.ToList();
        var lines = new List<string> { $"Słucham od {(int)(now - surveyStarted).TotalSeconds} s (najwyżej {(int)SurveyDuration.TotalMinutes} min)." };
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sensor in surveySensors)
        {
            var entry = entries.FirstOrDefault(e => SameMac(e.Address, sensor.Mac) || (e.Uuid is not null && e.Uuid == sensor.Uuid));
            if (entry is null)
            {
                lines.Add($"{sensor.Name}: nie słychać");
                continue;
            }
            matched.Add(entry.Address);
            lines.Add($"{sensor.Name}: {Describe(entry, now)}");
        }
        var others = entries.Where(e => !matched.Contains(e.Address)).OrderByDescending(e => e.Average ?? -200).ToList();
        if (surveySensors.Count == 0) lines.Add("(nazwy czujników będą po pierwszym uruchomieniu agenta)");
        AddSection("Zasięg czujników", lines);
        if (others.Count > 0)
            AddSection("Inne urządzenia Tuya w pobliżu", others.Take(15).Select(e =>
                $"{e.Address}{(e.Bound == false ? " NIESPAROWANY" : "")}: {Describe(e, now)}"));
    }

    private static bool SameMac(string address, string mac) =>
        BleAddress.TryParse(address, out var a) && BleAddress.TryParse(mac, out var b) && a == b;

    private static string Describe(SurveyEntry entry, DateTimeOffset now) =>
        string.Create(CultureInfo.InvariantCulture,
            $"teraz {SignalStats.Format(entry.LastRssi)}, śr. {SignalStats.Format(entry.Average)} ({entry.Packets} pakietów), {(int)(now - entry.LastSeenAt).TotalSeconds} s temu");
}
