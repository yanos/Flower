using System;

using Makaretu.Dns;

namespace Flower.Services;

// Throwaway spike (see SYNC-PLAN.md): proves mDNS/DNS-SD discovery works across
// desktop, iOS, and Android before investing in the real WiFi/LAN sync transport.
// Advertises this instance under "_flowersync._tcp" and logs any other Flower
// instance found on the LAN. No file transfer, no persistence, no UI - console
// output only, verified via platform logs (stdout / adb logcat / Xcode console).
public class NetworkDiscoveryService : IDisposable
{
    private const string ServiceType = "_flowersync._tcp";
    private const int ServicePort = 53317;

    private readonly MulticastService _mdns = new();
    private readonly ServiceDiscovery _serviceDiscovery;

    public NetworkDiscoveryService()
    {
        _serviceDiscovery = new ServiceDiscovery(_mdns);
        _serviceDiscovery.ServiceInstanceDiscovered += (_, e) =>
        {
            // ServiceInstanceDiscovered fires for any service instance seen on the
            // LAN, not just ones matching what we queried for (mDNS is a shared
            // multicast channel) - e.g. printers, Chromecasts, or Apple's own
            // _apple-mobdev2._tcp device-pairing traffic show up here too. Filter to
            // our own service type so the log only reflects actual Flower peers.
            if (!e.ServiceInstanceName.ToString().EndsWith($"{ServiceType}.local", StringComparison.OrdinalIgnoreCase))
                return;

            Console.WriteLine($"[NetworkDiscovery] Discovered peer: {e.ServiceInstanceName}");
        };
    }

    public void Start()
    {
        var profile = new ServiceProfile(Environment.MachineName, ServiceType, ServicePort);
        _serviceDiscovery.Advertise(profile);
        _mdns.Start();
        _serviceDiscovery.QueryServiceInstances(ServiceType);
    }

    public void Stop()
    {
        _serviceDiscovery.Unadvertise();
        _mdns.Stop();
    }

    public void Dispose()
    {
        Stop();
        _serviceDiscovery.Dispose();
        _mdns.Dispose();
    }
}
