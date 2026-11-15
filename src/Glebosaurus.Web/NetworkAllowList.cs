using System.Net;

namespace Glebosaurus.Web;

/// <summary>
/// Pierwsza warstwa ochrony: panel odpowiada tylko na połączenia z sieci lokalnej. Chroni też wtedy, gdy router przekieruje
/// port na zewnątrz (np. przez UPnP). Loopback jest dozwolony zawsze.
/// </summary>
public sealed class NetworkAllowList(IReadOnlyList<IPNetwork> networks)
{
    public bool IsAllowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        foreach (var network in networks)
            if (network.Contains(address)) return true;
        return false;
    }
}
