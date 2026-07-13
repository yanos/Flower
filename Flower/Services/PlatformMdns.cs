using System;
using System.Net;

namespace Flower.Services
{
    // A resolved mDNS/Bonjour instance announcement, in the platform-neutral shape
    // NetworkDiscoveryService consumes regardless of which IMdnsBackend produced it.
    public sealed class MdnsInstanceFound
    {
        public required string InstanceName { get; init; }
        public required IPEndPoint EndPoint { get; init; }
    }

    // Advertise+browse for one mDNS service type. NetworkDiscoveryService's default
    // implementation (MakaretuMdnsBackend, in NetworkDiscoveryService.cs) opens its
    // own raw multicast socket via Makaretu.Dns.Multicast - this works everywhere
    // except real iOS hardware, which silently drops raw multicast traffic without
    // a hard-to-get Apple entitlement (com.apple.developer.networking.multicast,
    // requires the paid Developer Program plus a separate Apple approval - not
    // practical for this project). See SYNC-PLAN.md. Real iOS instead gets
    // Flower.iOS's BonjourMdnsBackend, backed by NSNetService/NSNetServiceBrowser -
    // those run through the system's own mDNSResponder daemon rather than a raw
    // socket, so they're exempt from that restriction entirely.
    public interface IMdnsBackend : IDisposable
    {
        event EventHandler<MdnsInstanceFound>? InstanceFound;
        event EventHandler<string>? InstanceLost; // raw instance name

        void Advertise(string instanceName, string serviceType, int port);
        void Browse(string serviceType);
        void Stop();
    }

    // Set by the platform entry point (Flower.iOS's Program.cs) before Avalonia
    // starts. Left null everywhere else, where NetworkDiscoveryService just uses
    // its own default Makaretu-based backend directly.
    public static class PlatformMdns
    {
        public static IMdnsBackend? Current { get; set; }
    }
}
