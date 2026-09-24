using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Bridge.Network;

public static class LocalNetwork
{
    /// <summary>Best-effort guess at this machine's LAN IPv4 address, for embedding in the pairing QR code.</summary>
    public static string GetLikelyLanIPv4()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(addr.Address))
                {
                    return addr.Address.ToString();
                }
            }
        }
        return "127.0.0.1";
    }
}
