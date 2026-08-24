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

    // Stop() is reached twice on an ordinary shutdown - once from the host's
    // stopping phase, so peers get the goodbye while the process is still
    // alive, and again from Dispose - and Makaretu's Unadvertise throws a
    // NullReferenceException the second time, out of a Dispose, where it takes
    // the shutdown path down with it.
    private bool _stopped;

    // Nothing can be sent before MulticastService.Start() has run: until then it
    // holds no senders, so its packet-size ceiling reads as 0 and the first query
    // dies on "Exceeds max packet size of 0". Start() used to be reached only
    // through Advertise(), which was fine while every client also advertised
    // itself; now that only Flower.Server does, a browse-only client has to start
    // the service itself.
    private bool _started;

    // The service type Browse() was last asked for, re-queried whenever a network
    // interface appears. Start() enumerates interfaces on its own thread, so the
    // query issued immediately after it can go out before there is anything to
    // send it on - and a multicast query nobody hears is simply lost, with no
    // error to retry on.
    private string? _browsing;

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

        _mdns.NetworkInterfaceDiscovered += (_, _) =>
        {
            var serviceType = _browsing;
            if (serviceType != null)
                _serviceDiscovery.QueryServiceInstances(serviceType);
        };
    }

    public void Advertise(string instanceName, string serviceType, int port)
    {
        _stopped = false;
        var profile = new ServiceProfile(instanceName, serviceType, (ushort)port);
        _serviceDiscovery.Advertise(profile);

        // Advertise() before Start() is the order Makaretu wants: starting the
        // service is what sends the profile's unsolicited announcement. If it is
        // already running - a browse got here first - announce by hand instead.
        if (!EnsureStarted())
            _serviceDiscovery.Announce(profile);
    }

    public void Browse(string serviceType)
    {
        _stopped = false;
        _browsing = serviceType;

        // On the first call the NetworkInterfaceDiscovered handler above is what
        // actually gets a query onto the wire; querying here as well costs one
        // packet and covers every later call, when the service is long since up.
        if (!EnsureStarted())
            _serviceDiscovery.QueryServiceInstances(serviceType);
    }

    // True if this call is what started the service.
    private bool EnsureStarted()
    {
        if (_started)
            return false;

        _started = true;
        _mdns.Start();
        return true;
    }

    public void Stop()
    {
        if (_stopped)
            return;
        _stopped = true;

        _browsing = null;
        _serviceDiscovery.Unadvertise();
        if (_started)
        {
            _started = false;
            _mdns.Stop();
        }
    }

    public void Dispose()
    {
        Stop();
        _serviceDiscovery.Dispose();
        _mdns.Dispose();
    }
}
