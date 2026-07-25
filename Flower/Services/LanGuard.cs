using System.Net;
using System.Net.Sockets;

namespace Flower.Services;

// Restricts SyncHttpServer to LAN-originating callers only. SyncHttpServer.
// Start() binds a wildcard "http://+:{port}/" (needed since the actual
// interface a peer will reach this device on isn't known ahead of time), so
// this check is the only thing standing between "LAN-only" and "reachable
// from the internet if the port is ever forwarded/exposed" - see
// SyncHttpServer's own call site, which treats a failure here as a hard
// reject, not a warning.
public static class LanGuard
{
    public static bool IsPrivateOrLoopback(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;
        if (address.IsIPv6LinkLocal)
            return true;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254); // IPv4 link-local
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (address.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7 (ULA)

        return false;
    }
}
