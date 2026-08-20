using System;
using System.Linq;
using System.Net;

using Makaretu.Dns;

namespace Flower.Services;

// Default IMdnsBackend (see PlatformMdns.cs): raw multicast via
// Makaretu.Dns.Multicast. Works everywhere except real iOS hardware - see
// PlatformMdns's own doc comment for why - where Flower.iOS overrides
// PlatformMdns.Current with a Bonjour-API-backed implementation instead.
//
// Lives here rather than in the Avalonia project so Flower.Server can advertise
// itself with the same backend the client browses with (see MdnsAdvertiser
// there). Public for the same reason - it used to be internal to Flower.
public sealed class MakaretuMdnsBackend : IMdnsBackend
{
    private readonly MulticastService _mdns = new();
    private readonly ServiceDiscovery _serviceDiscovery;

    public event EventHandler<MdnsInstanceFound>? InstanceFound;
    public event EventHandler<string>? InstanceLost;

    public MakaretuMdnsBackend()
    {
        _serviceDiscovery = new ServiceDiscovery(_mdns);
        _serviceDiscovery.ServiceInstanceDiscovered += (_, e) =>
        {
            var name = e.ServiceInstanceName.ToString();

            // No separate resolve round-trip needed: the discovery answer already
            // carries the sender's real address (RemoteEndPoint) and, per DNS-SD
            // convention, the SRV record with the service port in AdditionalRecords.
            var srv = e.Message.AdditionalRecords.OfType<SRVRecord>().FirstOrDefault();
            var port = srv?.Port ?? (ushort)SyncProtocol.DefaultPort;
            var endpoint = new IPEndPoint(e.RemoteEndPoint.Address, port);
            InstanceFound?.Invoke(this, new MdnsInstanceFound { InstanceName = name, EndPoint = endpoint });
        };
        _serviceDiscovery.ServiceInstanceShutdown += (_, e) =>
            InstanceLost?.Invoke(this, e.ServiceInstanceName.ToString());
    }

    public void Advertise(string instanceName, string serviceType, int port)
    {
        _serviceDiscovery.Advertise(new ServiceProfile(instanceName, serviceType, (ushort)port));
        _mdns.Start();
    }

    public void Browse(string serviceType) => _serviceDiscovery.QueryServiceInstances(serviceType);

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
