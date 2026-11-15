using Microsoft.Extensions.DependencyInjection;
using Shiny;
using Shiny.BluetoothLE;

namespace Glebosaurus.Agent.Ble;

/// <summary>Składa Shiny.BluetoothLE bez MAUI: Windows przez WinRT, Linux przez BlueZ (D-Bus).</summary>
internal sealed class BleHost : IDisposable
{
#if WINDOWS
    public const string Backend = "Windows (WinRT)";
#else
    public const string Backend = "Linux (BlueZ)";
#endif

    private readonly ServiceProvider provider;

    public BleHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
#if WINDOWS
        services.AddBluetoothLE();
#else
        services.AddLinuxBluetoothLE();
#endif
        provider = services.BuildServiceProvider();
        Manager = provider.GetRequiredService<IBleManager>();
    }

    public IBleManager Manager { get; }

    public async Task EnsureAccessAsync()
    {
        var access = await Manager.RequestAccessAsync();
        if (access != AccessState.Available)
            throw new InvalidOperationException($"Bluetooth jest niedostępny ({access}). Sprawdź, czy adapter jest włączony i czy usługa Bluetooth działa.");
    }

    public void Dispose() => provider.Dispose();
}
