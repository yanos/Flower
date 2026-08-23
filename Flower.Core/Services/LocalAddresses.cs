using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Flower.Services;

// Every address a server believes it can be reached on, for the addresses field
// of the /info handshake (SyncInfoResponseDto). Shared by both servers - the
// headless Flower.Server and the app's own SyncHttpServer - because "where am I
// reachable" is the same question on both and a second copy would be a second
// answer.
//
// This is what lets a client keep hold of a server it can no longer discover:
// mDNS is link-local, so it never crosses a tailnet or a subnet, and a client
// that only knows how to *discover* a server has no way back to one it cannot
// see. Reported here once, remembered by the client, probed in rank order
// later - see REMOTE-ACCESS-PLAN.md.
//
// Deliberately not Tailscale-aware. A tailnet address is just a unicast address
// in 100.64.0.0/10 (the range LanGuard already knows), so enumerating our own
// interfaces reports it without this ever knowing Tailscale exists - no
// shelling out to the tailscale binary, and SYNC-PLAN.md's "document, don't
// automate" decision stays intact.
public static class LocalAddresses
{
    // Noise is fine here and filtering hard would be worse. A Docker bridge on
    // 172.17.x gets reported and will never answer a probe, which costs the
    // client one failed request; guessing which of a machine's interfaces are
    // "real" would eventually drop the one interface that was.
    // Full origins - "http://192.168.1.40:4533", not "192.168.1.40:4533" -
    // because the scheme is part of how a peer is reached and only this side
    // knows it. A client that had to assume one could never dial a server
    // behind TLS, which is exactly the state this replaced.
    //
    // The scheme parameter is what a TLS-serving deployment sets; the addresses
    // enumerated from interfaces all share it, since they are all this same
    // listener seen from different networks. An AdvertisedHost that names its
    // own scheme overrides it, because that one is not this listener at all -
    // it is whatever terminates TLS in front of it.
    public static List<string> Reachable(int port, string? advertisedHost = null, string scheme = "http")
    {
        var addresses = new List<string>();

        // First, because an operator who set it knows something the interface
        // list cannot: the name that resolves from outside, through a proxy or
        // a remapped container port. See FlowerServerOptions.AdvertisedHost.
        if (!string.IsNullOrWhiteSpace(advertisedHost))
            addresses.Add(AdvertisedOrigin(advertisedHost.Trim(), port, scheme));

        foreach (var nic in SafeInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            foreach (var unicast in SafeUnicastAddresses(nic))
            {
                if (!IsReportable(unicast.Address))
                    continue;

                addresses.Add(Format(unicast.Address, port, scheme));
            }
        }

        return addresses.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Link-local is excluded rather than merely ranked last: a 169.254/fe80
    // address is only meaningful on the link it was minted for, and a client
    // that remembered one would keep probing an address that cannot work from
    // anywhere else. NetworkDiscoveryService already has to special-case
    // link-local endpoints arriving over mDNS for the same reason.
    private static bool IsReportable(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return false;

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => address.GetAddressBytes() is not [169, 254, ..],
            AddressFamily.InterNetworkV6 => !address.IsIPv6LinkLocal && !address.IsIPv6Multicast,
            _ => false,
        };
    }

    private static string Format(IPAddress address, int port, string scheme) =>
        address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"{scheme}://[{address}]:{port}"
            : $"{scheme}://{address}:{port}";

    // An AdvertisedHost is written by hand and so arrives in every shape a
    // person might reasonably write: "host", "host:8080", "[::1]:8080",
    // "https://music.example.com", or that with a path glued on.
    //
    // A scheme of its own wins outright - "https://music.example.com" is a
    // Cloudflare tunnel or a reverse proxy, and neither this listener's scheme
    // nor its port has anything to do with how the world reaches it. Anything
    // else is a bare host that stands in front of *this* listener, so it
    // inherits both, and only gains a port when it does not already state one.
    private static string AdvertisedOrigin(string host, int port, string scheme)
    {
        if (Uri.TryCreate(host, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute.GetLeftPart(UriPartial.Authority);
        }

        // Reading the last colon *after* any bracketed IPv6 literal, which is
        // full of them - otherwise "[::1]:8080" looks like it names no port and
        // becomes "[::1]:8080:4533".
        var afterBracket = host.LastIndexOf(']');
        var colon = host.LastIndexOf(':');
        var withPort = colon > afterBracket && colon >= 0 ? host : $"{host}:{port}";
        return $"{scheme}://{withPort}";
    }

    // Enumerating interfaces can throw on a locked-down or unusual host
    // (sandboxed mobile, a container with no NET_ADMIN). Reporting no addresses
    // is a degraded server - a client falls back to discovering it on the LAN -
    // whereas throwing would take down the handshake every peer needs before it
    // can do anything at all.
    private static IEnumerable<NetworkInterface> SafeInterfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
        catch (PlatformNotSupportedException)
        {
            return [];
        }
    }

    private static IEnumerable<UnicastIPAddressInformation> SafeUnicastAddresses(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().UnicastAddresses;
        }
        catch (NetworkInformationException)
        {
            return [];
        }
        catch (PlatformNotSupportedException)
        {
            return [];
        }
    }
}
