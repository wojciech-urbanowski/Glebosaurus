using Android.Content;
using Android.Net;
using Android.OS;
using Glebosaurus.Core;
using Glebosaurus.Core.Cloud;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Android;

/// <summary>
/// Sygnał życia telefonu (ARCHITEKTURA.md, zasady 3 i 12): wersja, stan Bluetooth, bateria, temperatura akumulatora, czy jest
/// na ładowarce i siła Wi-Fi. Telefon pracuje stale na ładowarce – temperatura i ładowanie mówią, czy wszystko w porządku.
/// </summary>
internal static class PhoneState
{
    public static AgentStatus Read(Context context, string ble)
    {
        var status = new AgentStatus(AppInfo.Version, "android", ble);
        // Rozgłoszenie „lepkie” – bez odbiornika zwraca ostatni stan baterii.
        using var battery = context.RegisterReceiver(null, new IntentFilter(Intent.ActionBatteryChanged));
        if (battery is not null)
        {
            var level = battery.GetIntExtra(BatteryManager.ExtraLevel, -1);
            var scale = battery.GetIntExtra(BatteryManager.ExtraScale, -1);
            var temperature = battery.GetIntExtra(BatteryManager.ExtraTemperature, int.MinValue);
            // „Na ładowarce” = podłączony; ochrona akumulatora (np. HyperOS) potrafi wstrzymać ładowanie przy 80%, to nie błąd.
            var plugged = battery.GetIntExtra(BatteryManager.ExtraPlugged, 0) != 0;
            status = status with
            {
                BatteryPercent = level >= 0 && scale > 0 ? (int)Math.Round(100.0 * level / scale) : null,
                BatteryTemperatureCelsius = temperature != int.MinValue ? temperature / 10.0 : null,
                Charging = plugged
            };
        }
        if (context.GetSystemService(Context.ConnectivityService) is ConnectivityManager connectivity
            && connectivity.GetNetworkCapabilities(connectivity.ActiveNetwork) is { } network
            && network.HasTransport(TransportType.Wifi) && network.SignalStrength != int.MinValue)
            status = status with { WifiDbm = network.SignalStrength };
        return status;
    }
}

/// <summary>Dziennik do logcat (adb logcat -s Glebosaurus) – obok plików dziennych i ostatnich wpisów na ekranie.</summary>
internal sealed class LogcatLoggerProvider : ILoggerProvider
{
    private const string Tag = "Glebosaurus";

    public ILogger CreateLogger(string categoryName) => new LogcatLogger();

    public void Dispose() { }

    private sealed class LogcatLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (exception is not null) message += System.Environment.NewLine + exception;
            switch (logLevel)
            {
                case LogLevel.Information: global::Android.Util.Log.Info(Tag, message); break;
                case LogLevel.Warning: global::Android.Util.Log.Warn(Tag, message); break;
                default: global::Android.Util.Log.Error(Tag, message); break;
            }
        }
    }
}
