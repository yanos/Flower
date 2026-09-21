using System.Net;
using System.Net.Sockets;

namespace Flower.Server.Services;

// The two ways this server's advertised address can be wrong in a manner the
// server itself never sees a symptom of.
//
// Both failures land entirely on the client: NetworkDiscoveryService resolves a
// remembered address and drops it when that comes back empty, and probes one it
// cannot route to until it gives up. Either way nothing is ever sent, so the
// server logs no refusal, no timeout and no request - the operator sees a
// working server and the listener sees "unreachable", with nothing joining the
// two. Hence a warning at startup, which is the one moment somebody is reading.
//
// Predicates rather than the warnings themselves, so the wording stays in
// Program.cs next to the other startup advice and these stay testable.
public static class ServerAddressAdvice
{
    // A .local name in AdvertisedHost. It looks like the durable choice - it is
    // what survives a DHCP move - and it is the one that cannot work, because
    // .local is resolved by an mDNS responder rather than by the system
    // resolver a client asks. On iOS that means Dns.GetHostAddressesAsync
    // returns nothing and the address is discarded before it is dialled.
    //
    // Accepts a bare host, a host:port, or a full origin, since AdvertisedHost
    // takes all three (see LocalAddresses.AdvertisedOrigin).
    public static bool LooksLikeMulticastDnsName(string advertisedHost)
    {
        var host = HostOf(advertisedHost);
        return host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local.", StringComparison.OrdinalIgnoreCase);
    }

    // Docker's default bridge pool, 172.16.0.0/12. Also a legitimate private
    // LAN range, which is why the caller requires *every* address to be in it
    // and still words the warning as a suspicion.
    public static bool IsDockerBridgeAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var b = address.GetAddressBytes();
        return b[0] == 172 && b[1] is >= 16 and <= 31;
    }

    // The .NET base images set DOTNET_RUNNING_IN_CONTAINER, and /.dockerenv is
    // the older tell that survives a rebuilt image. Either is enough: this only
    // gates a warning, so a false negative costs the advice and a false
    // positive costs nothing, since the address test still has to agree.
    public static bool LooksContainerised() =>
        string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true", StringComparison.OrdinalIgnoreCase)
        || File.Exists("/.dockerenv");

    private static string HostOf(string advertisedHost)
    {
        var trimmed = advertisedHost.Trim();
        if (trimmed.Length == 0)
            return trimmed;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute.Host;
        }

        // Same bracket-aware split as LocalAddresses.AdvertisedOrigin: an IPv6
        // literal is full of colons, so the last one only names a port when it
        // falls outside the brackets.
        var afterBracket = trimmed.LastIndexOf(']');
        var colon = trimmed.LastIndexOf(':');
        var host = colon > afterBracket && colon >= 0 ? trimmed[..colon] : trimmed;
        return host.Trim('[', ']');
    }
}
