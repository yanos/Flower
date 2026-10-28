using System;
using System.Collections.Generic;

using Flower.Services;

namespace Flower.Tests.TestSupport;

// Stands in for MakaretuMdnsBackend/BonjourMdnsBackend so
// NetworkDiscoveryServiceTests can drive discovery/loss events directly
// instead of needing a real LAN and a real mDNS responder - Advertise/
// Browse/Stop calls are just recorded for assertions, and tests raise
// InstanceFound/InstanceLost themselves via RaiseInstanceFound/
// RaiseInstanceLost.
public sealed class FakeMdnsBackend : IMdnsBackend
{
    public List<(string InstanceName, string ServiceType, int Port)> Advertised { get; } = [];
    public List<string> Browsed { get; } = [];
    public bool StopCalled { get; private set; }
    public bool DisposeCalled { get; private set; }

    public event EventHandler<MdnsInstanceFound>? InstanceFound;
    public event EventHandler<string>? InstanceLost;

    public void Advertise(string instanceName, string serviceType, int port) =>
        Advertised.Add((instanceName, serviceType, port));

    public void Browse(string serviceType) => Browsed.Add(serviceType);

    public void Stop() => StopCalled = true;

    public void RaiseInstanceFound(string instanceName, System.Net.IPEndPoint endPoint) =>
        InstanceFound?.Invoke(this, new MdnsInstanceFound { InstanceName = instanceName, EndPoint = endPoint });

    public void RaiseInstanceLost(string instanceName) => InstanceLost?.Invoke(this, instanceName);

    public void Dispose() => DisposeCalled = true;
}
