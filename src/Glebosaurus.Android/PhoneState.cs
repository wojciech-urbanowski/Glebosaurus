using Android.Content;
using Android.Net;
using Android.OS;
using Glebosaurus.Core;
using Glebosaurus.Core.Cloud;
using Microsoft.Extensions.Logging;

namespace Glebosaurus.Android;

/// <summary>
/// Sygnał życia telefonu (ARCHITEKTURA.md, zasady 3 i 12): wersja, stan Bluetooth, bateria, temperatura akumulatora, stan
/// cieplny systemu, czy jest na ładowarce, rodzaj sieci i siła Wi-Fi. Telefon ma być na Wi-Fi – te pola mówią, czy wszystko
/// w porządku (ostrzeżenia – <c>AgentDevice</c> w Core). Jedzie w każdej synchronizacji z chmurą (telefon niczego nie wystawia), a chmura oddaje
/// go innym agentom – panel WWW na komputerze pokazuje stan telefonu.
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
        // Stan cieplny całego telefonu (Android 10+): od „moderate” system ogranicza wydajność.
        if (context.GetSystemService(Context.PowerService) is PowerManager power)
            status = status with { ThermalStatus = AgentDevice.ThermalStatusName((int)power.CurrentThermalStatus) };
        if (context.GetSystemService(Context.ConnectivityService) is ConnectivityManager connectivity
            && connectivity.GetNetworkCapabilities(connectivity.ActiveNetwork) is { } network)
        {
            var wifi = network.HasTransport(TransportType.Wifi);
            status = status with
            {
                Network = wifi ? "wifi"
                    : network.HasTransport(TransportType.Cellular) ? "cellular"
                    : network.HasTransport(TransportType.Ethernet) ? "ethernet"
                    : "other",
                // SIGNAL_STRENGTH_UNSPECIFIED = Integer.MIN_VALUE.
                WifiDbm = wifi && network.SignalStrength != int.MinValue ? network.SignalStrength : null
            };
        }
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
