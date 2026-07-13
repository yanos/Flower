using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;

using Foundation;

using Flower.Services;

namespace Flower.iOS;

// Real iOS hardware can't do raw multicast (NetworkDiscoveryService's default
// MakaretuMdnsBackend) without a hard-to-get Apple entitlement - see
// PlatformMdns.cs. NSNetService/NSNetServiceBrowser instead go through the
// system's own mDNSResponder daemon, exactly like every native Bonjour app
// (AirPlay, AirDrop, printer discovery) - exempt from that restriction, and
// exactly what Info.plist's NSBonjourServices key already declared support
// for. Wired in from Program.cs before Avalonia starts.
public sealed class BonjourMdnsBackend : IMdnsBackend
{
    private NSNetService? _published;
    private NSNetServiceBrowser? _browser;
    private string _serviceType = "";

    // NSNetService instances mid-Resolve() need a live reference kept somewhere
    // other than the event subscription itself, or they can be collected by the
    // GC before resolution completes - keyed by object identity since a service's
    // Name isn't unique across resolve attempts in flight.
    private readonly List<NSNetService> _resolving = new();

    public event EventHandler<MdnsInstanceFound>? InstanceFound;
    public event EventHandler<string>? InstanceLost;

    public void Advertise(string instanceName, string serviceType, int port)
    {
        _published = new NSNetService("local.", serviceType + ".", instanceName, port);
        _published.Publish();
    }

    public void Browse(string serviceType)
    {
        _serviceType = serviceType;
        _browser = new NSNetServiceBrowser();
        _browser.FoundService += OnFoundService;
        _browser.ServiceRemoved += OnServiceRemoved;
        _browser.SearchForServices(serviceType + ".", "local.");
    }

    private void OnFoundService(object? sender, NSNetServiceEventArgs e)
    {
        var service = e.Service;
        lock (_resolving)
            _resolving.Add(service);

        service.AddressResolved += (_, __) => OnAddressResolved(service);
        service.ResolveFailure += (_, __) =>
        {
            lock (_resolving)
                _resolving.Remove(service);
        };
        service.Resolve(10);
    }

    private void OnAddressResolved(NSNetService service)
    {
        var endpoint = ParseFirstIPv4EndPoint(service);
        lock (_resolving)
            _resolving.Remove(service);

        if (endpoint == null)
            return;

        var instanceName = $"{service.Name}.{_serviceType}.local";
        InstanceFound?.Invoke(this, new MdnsInstanceFound { InstanceName = instanceName, EndPoint = endpoint });
    }

    private void OnServiceRemoved(object? sender, NSNetServiceEventArgs e)
    {
        var instanceName = $"{e.Service.Name}.{_serviceType}.local";
        InstanceLost?.Invoke(this, instanceName);
    }

    // Each entry in NSNetService.Addresses is a raw sockaddr blob (sockaddr_in for
    // IPv4, sockaddr_in6 for IPv6) - Flower's sync transport is IPv4-only elsewhere
    // (see the plain IPEndPoint usage throughout NetworkDiscoveryService/
    // SyncHttpServer), so only the first IPv4 entry is used here. Darwin's
    // sockaddr_in layout: byte 0 = sin_len, byte 1 = sin_family (AF_INET = 2),
    // bytes 2-3 = sin_port (unused here - NSNetService.Port already gives it to
    // us directly), bytes 4-7 = sin_addr.
    private static IPEndPoint? ParseFirstIPv4EndPoint(NSNetService service)
    {
        var addresses = service.Addresses;
        if (addresses == null)
            return null;

        foreach (var data in addresses)
        {
            var length = (int)data.Length;
            if (length < 8)
                continue;

            var raw = new byte[length];
            Marshal.Copy(data.Bytes, raw, 0, length);

            const byte afInet = 2;
            if (raw[1] != afInet)
                continue;

            var address = new IPAddress(new[] { raw[4], raw[5], raw[6], raw[7] });
            return new IPEndPoint(address, (int)service.Port);
        }

        return null;
    }

    public void Stop()
    {
        _published?.Stop();
        _browser?.Stop();
    }

    public void Dispose()
    {
        Stop();
        _published?.Dispose();
        _browser?.Dispose();
        lock (_resolving)
            _resolving.Clear();
    }
}
