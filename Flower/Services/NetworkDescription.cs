using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

namespace Flower.Services;

// One line saying which network this device is on, in the terms that decide
// whether a server address it knows is still reachable: each interface that
// is up, the addresses it has and the gateways it goes out through. Two
// moments with the same line are on the same network, so a report of a
// change between them changed nothing worth rechecking - see
// NetworkChangeFilter. Each platform gathers the pieces its own way
// (INetworkChangeSource.DescribeNetwork); the line is written here, once.
public static class NetworkDescription
{
    public sealed record Interface(string Name, IEnumerable<string> Addresses, IEnumerable<string> Gateways);

    // Order-blind, since nothing promises the OS lists interfaces or addresses
    // the same way twice. An interface with no address left is not a way out
    // and is dropped, which is what keeps a Mac's AirDrop and low-latency
    // interfaces (awdl0, llw0: link-local only, coming and going all day)
    // out of it.
    public static string Of(IEnumerable<Interface> interfaces)
    {
        var lines = new List<string>();
        foreach (var nic in interfaces)
        {
            var addresses = nic.Addresses.Distinct().Order(StringComparer.Ordinal).ToList();
            if (addresses.Count == 0)
                continue;
            var gateways = nic.Gateways.Distinct().Order(StringComparer.Ordinal);
            lines.Add($"{nic.Name}=[{string.Join(", ", addresses)}] via [{string.Join(", ", gateways)}]");
        }

        lines.Sort(StringComparer.Ordinal);
        return lines.Count == 0 ? "none" : string.Join("; ", lines);
    }

    // Whether an address says anything about which network this is. A
    // link-local or loopback one does not: every interface has one, and the
    // OS hands them out and takes them back without the route out moving.
    public static bool Counts(IPAddress address) =>
        !IPAddress.IsLoopback(address)
        && !address.IsIPv6LinkLocal
        && !(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
             && address.GetAddressBytes() is [169, 254, ..]);

    // The desktops', from .NET's view of the interfaces - and the fallback
    // for any platform without a better one.
    public static string? OfThisMachine()
    {
        try
        {
            var interfaces = new List<Interface>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                var properties = nic.GetIPProperties();
                var addresses = properties.UnicastAddresses.Select(a => a.Address).Where(Counts).Select(a => a.ToString()).ToList();
                interfaces.Add(new Interface(nic.Name, addresses, GatewaysOf(properties)));
            }
            return Of(interfaces);
        }
        catch (Exception)
        {
            // Not knowing is not the same network: the caller rechecks.
            return null;
        }
    }

    // Not every OS fills these in for .NET; an interface whose gateways
    // cannot be read is still described by its addresses.
    private static List<string> GatewaysOf(IPInterfaceProperties properties)
    {
        try
        {
            return [.. properties.GatewayAddresses.Select(g => g.Address).Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any)).Select(a => a.ToString())];
        }
        catch (Exception)
        {
            return [];
        }
    }
}
