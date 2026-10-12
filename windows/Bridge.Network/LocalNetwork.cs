using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Bridge.Network;

public static class LocalNetwork
{
    private static readonly string[] VirtualAdapterHints =
        ["virtual", "hyper-v", "vethernet", "vmware", "virtualbox", "wsl", "docker", "loopback", "tap-", "vpn", "bluetooth"];

    /// <summary>
    /// Best-effort guess at this machine's LAN IPv4 address, for embedding in
    /// the pairing QR code. Prefers a physical Wi-Fi/Ethernet adapter with a
    /// default gateway over virtual (Hyper-V/WSL/VPN) adapters, which a phone on
    /// the same Wi-Fi can't reach.
    /// </summary>
    public static string GetLikelyLanIPv4()
    {
        var best = Candidates().OrderByDescending(c => c.Score).FirstOrDefault();
        return best.Address?.ToString() ?? "127.0.0.1";
    }

    private static IEnumerable<(IPAddress Address, int Score)> Candidates()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); } catch (NetworkInformationException) { continue; }

            var label = (nic.Name + " " + nic.Description).ToLowerInvariant();
            var isVirtual = VirtualAdapterHints.Any(label.Contains);
            var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            var isPhysical = nic.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet;

            foreach (var addr in props.UnicastAddresses)
            {
                var ip = addr.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) continue;
                var bytes = ip.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue; // link-local: no DHCP lease

                var score = 0;
                if (hasGateway) score += 4;
                if (!isVirtual) score += 2;
                if (isPhysical) score += 1;
                yield return (ip, score);
            }
        }
    }
}
