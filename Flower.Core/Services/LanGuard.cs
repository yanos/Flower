using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace Flower.Services;

// Restricts Flower.Server (with extraAllowedCidrs for a reverse proxy or a
// tailnet) to LAN-originating callers only. It binds a wildcard interface
// ("0.0.0.0:4533"), so this check is the
// only thing standing between "LAN-only" and "reachable from the internet if
// the port is ever forwarded/exposed" - see each call site, which treats a
// failure here as a hard reject, not a warning.
public static class LanGuard
{
    // allowCarrierGradeNat covers 100.64.0.0/10, which is where Tailscale
    // hands out its addresses (the documented remote-access path for
    // Flower.Server) but is also a real carrier-grade-NAT range: on a mobile
    // connection sitting behind CGNAT, "some other subscriber on the same
    // carrier" lands inside it too. Left on by default so the Tailscale path
    // keeps working untouched, but Flower.Server exposes it as
    // TrustTailscaleRange so a deployment that doesn't use a tailnet can drop
    // the range instead of trusting it for nothing.
    public static bool IsPrivateOrLoopback(
        IPAddress address, IEnumerable<string>? extraAllowedCidrs = null, bool allowCarrierGradeNat = true)
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
            if (b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)) // IPv4 link-local
                return true;
            if (allowCarrierGradeNat && b[0] == 100 && b[1] is >= 64 and <= 127) // Tailscale CGNAT range, 100.64.0.0/10
                return true;
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if ((address.GetAddressBytes()[0] & 0xFE) == 0xFC) // fc00::/7 (ULA)
                return true;
        }

        return extraAllowedCidrs != null && extraAllowedCidrs.Any(cidr => IsInCidr(address, cidr));
    }

    // A user-configured widening of the allow-list (Flower.Server's
    // FlowerServerOptions.AllowedCidrs) for a trusted tunnel/proxy whose
    // source range doesn't fall into any of the built-in private ranges
    // above - e.g. a reverse proxy on its own subnet. Malformed entries are
    // skipped rather than throwing, since a typo in config should degrade to
    // "that entry doesn't match," not crash request handling.
    private static bool IsInCidr(IPAddress address, string cidr)
    {
        var parts = cidr.Split('/', 2);
        if (parts.Length != 2)
            return false;
        if (!IPAddress.TryParse(parts[0].Trim(), out var network))
            return false;
        if (!int.TryParse(parts[1].Trim(), out var prefixLength))
            return false;
        if (address.AddressFamily != network.AddressFamily)
            return false;

        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();
        if (prefixLength < 0 || prefixLength > addressBytes.Length * 8)
            return false;

        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (addressBytes[i] != networkBytes[i])
                return false;
        }

        if (remainingBits == 0)
            return true;

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (addressBytes[fullBytes] & mask) == (networkBytes[fullBytes] & mask);
    }
}
