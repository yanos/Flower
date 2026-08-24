using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

using Flower.Services;

namespace Flower.iOS;

// Real iOS hardware can't do raw multicast (NetworkDiscoveryService's default
// MakaretuMdnsBackend) without a hard-to-get Apple entitlement - see
// PlatformMdns.cs. This backend instead talks to the system's own
// mDNSResponder daemon directly through the low-level DNS-SD C API
// (dns_sd.h, part of libSystem) - the same daemon every native Bonjour app
// (AirPlay, AirDrop, printer discovery) goes through, exempt from that
// restriction, and exactly what Info.plist's NSBonjourServices key already
// declared support for. Wired in from Program.cs before Avalonia starts.
//
// Not NSNetService/NSNetServiceBrowser (the Cocoa wrapper around this same
// API, obsoleted at iOS 15 - CA1422) and not Network.framework's
// NWListener/NWBrowser either - NWListener doesn't work for this app at all.
// Confirmed on a real device: it throws "A connection handler should be set
// before starting a NWListener", and even past that it would need to itself
// own the TCP socket on the advertised port, which the app's own listener owned
// at the time - Network.framework has no "advertise-only, for a socket something
// else owns" mode (Apple's own developer forums confirm this is a hard
// limitation, not a missing setup call: search "NSNetService is deprecated,
// how to advertise network service that is written using non-Apple API?").
// The raw DNS-SD API is Apple's own recommended answer for exactly that
// scenario, and - unlike NSNetService - isn't itself deprecated.
//
// Native callbacks below are [UnmanagedCallersOnly] static methods, not
// instance delegates/closures - also confirmed on a real device: iOS is
// AOT-only (no JIT, ever, on real hardware), so mDNSResponder calling back
// into a plain closed/instance-bound delegate throws ExecutionEngineException
// ("Attempting to JIT compile ... while running in aot-only mode") the moment
// it fires - only a statically-known trampoline can be an AOT-compiled
// reverse-P/Invoke target. Each call threads `this` (or per-resolve state)
// through as a GCHandle via DNS-SD's own void* context parameter, since a
// static method has no other way back to instance state.
public sealed unsafe class BonjourMdnsBackend : IMdnsBackend
{
    private const string DnsSdLib = "/usr/lib/libSystem.B.dylib";
    private const uint InterfaceIndexAny = 0;

    // kDNSServiceFlagsAdd - set on a browse reply when a service appeared,
    // clear when it went away (dns_sd.h).
    private const uint FlagsAdd = 0x2;
    private const int ErrNoError = 0;

    [DllImport(DnsSdLib)]
    private static extern int DNSServiceRegister(
        out IntPtr sdRef, uint flags, uint interfaceIndex,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string regType,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string domain,
        IntPtr host, ushort port, ushort txtLen, IntPtr txtRecord,
        IntPtr callBack, IntPtr context);

    [DllImport(DnsSdLib)]
    private static extern int DNSServiceBrowse(
        out IntPtr sdRef, uint flags, uint interfaceIndex,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string regType,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string domain,
        delegate* unmanaged[Cdecl]<IntPtr, uint, uint, int, IntPtr, IntPtr, IntPtr, IntPtr, void> callBack,
        IntPtr context);

    [DllImport(DnsSdLib)]
    private static extern int DNSServiceResolve(
        out IntPtr sdRef, uint flags, uint interfaceIndex,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string regType,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string domain,
        delegate* unmanaged[Cdecl]<IntPtr, uint, uint, int, IntPtr, IntPtr, ushort, ushort, IntPtr, IntPtr, void> callBack,
        IntPtr context);

    [DllImport(DnsSdLib)]
    private static extern int DNSServiceProcessResult(IntPtr sdRef);

    [DllImport(DnsSdLib)]
    private static extern void DNSServiceRefDeallocate(IntPtr sdRef);

    private IntPtr _registerRef;
    private IntPtr _browseRef;
    private GCHandle _browseContext;
    private string _serviceType = "";

    public event EventHandler<MdnsInstanceFound>? InstanceFound;
    public event EventHandler<string>? InstanceLost;

    // Unreachable on this platform, and left rather than stubbed out: nothing
    // on a phone advertises itself any more (see NetworkDiscoveryService -
    // a client browses, and only Flower.Server advertises), so IMdnsBackend's
    // advertising half has no caller here. It is kept working because the
    // interface is shared with the backend Flower.Server does advertise
    // through, and a member that quietly throws is worse than one that does
    // what it says.
    public void Advertise(string instanceName, string serviceType, int port)
    {
        // Can be called again later - tear down any previous registration
        // first, or it leaks and ends up advertising the same name twice.
        if (_registerRef != IntPtr.Zero)
        {
            DNSServiceRefDeallocate(_registerRef);
            _registerRef = IntPtr.Zero;
        }

        // No callback (IntPtr.Zero) - per DNSServiceRegister's own docs, the
        // registration still takes effect immediately in mDNSResponder
        // without needing to pump results; a NULL callback just means we
        // don't get notified of a rename-on-conflict, which the old
        // NSNetService-based version of this class didn't act on either.
        // Port must be network-byte-order.
        var networkPort = (ushort)IPAddress.HostToNetworkOrder((short)port);
        var err = DNSServiceRegister(out _registerRef, 0, InterfaceIndexAny,
            instanceName, serviceType, "local.", IntPtr.Zero, networkPort, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (err != ErrNoError)
            _registerRef = IntPtr.Zero;
    }

    public void Browse(string serviceType)
    {
        _serviceType = serviceType;

        // Browse() repeats periodically (NetworkDiscoveryService's
        // RebrowseInterval) - tear down the previous browse op first, or
        // every rebrowse leaks another live socket and pump thread forever,
        // each still redelivering duplicate results on top of the new one.
        if (_browseRef != IntPtr.Zero)
        {
            DNSServiceRefDeallocate(_browseRef);
            _browseRef = IntPtr.Zero;
        }
        if (_browseContext.IsAllocated)
            _browseContext.Free();

        _browseContext = GCHandle.Alloc(this);
        var err = DNSServiceBrowse(out _browseRef, 0, InterfaceIndexAny, serviceType, "local.",
            &OnBrowseReply, GCHandle.ToIntPtr(_browseContext));
        if (err != ErrNoError)
        {
            _browseRef = IntPtr.Zero;
            _browseContext.Free();
            return;
        }

        // DNSServiceProcessResult blocks internally until a reply arrives,
        // invokes the callback synchronously, then returns - so pumping it in
        // a plain loop on a dedicated background thread is sufficient, no
        // manual poll/select needed. The loop (and the thread) ends on its
        // own once the ref above is deallocated - either by a future Browse()
        // call replacing it, or by Stop()/Dispose() - which makes the blocked
        // call return a non-zero error.
        var sdRef = _browseRef;
        new Thread(() =>
        {
            while (DNSServiceProcessResult(sdRef) == ErrNoError)
            {
            }
        })
        { IsBackground = true, Name = "BonjourBrowse" }.Start();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBrowseReply(IntPtr sdRef, uint flags, uint interfaceIndex, int errorCode,
        IntPtr serviceNamePtr, IntPtr regTypePtr, IntPtr replyDomainPtr, IntPtr context)
    {
        if (errorCode != ErrNoError)
            return;
        if (GCHandle.FromIntPtr(context).Target is not BonjourMdnsBackend backend)
            return;

        var serviceName = Marshal.PtrToStringUTF8(serviceNamePtr) ?? "";
        var regType = Marshal.PtrToStringUTF8(regTypePtr) ?? "";
        var replyDomain = Marshal.PtrToStringUTF8(replyDomainPtr) ?? "";
        var instanceName = $"{serviceName}.{backend._serviceType}.local";

        if ((flags & FlagsAdd) == 0)
        {
            backend.InstanceLost?.Invoke(backend, instanceName);
            return;
        }

        backend.ResolveAsync(serviceName, regType, replyDomain, interfaceIndex, instanceName);
    }

    // Per-resolve state, handed to the static callback below via its own
    // GCHandle (see the class comment) - short-lived, one per in-flight
    // DNSServiceResolve call rather than a shared field, since more than one
    // can be in flight at once.
    private sealed class ResolveState
    {
        public volatile bool Done;
        public IPEndPoint? EndPoint;
    }

    // DNSServiceResolve gives back a hostname (e.g. "iPhone.local.") and port,
    // not an address - a second lookup is needed either way. Rather than a
    // third dns_sd call (DNSServiceGetAddrInfo, with its own callback/pump and
    // raw sockaddr parsing), this uses a plain managed DNS lookup: Apple's
    // getaddrinfo (which Dns.GetHostAddresses calls into) is itself
    // mDNSResponder-aware for ".local" names, so it resolves the same way
    // without another round of native interop.
    private void ResolveAsync(string serviceName, string regType, string domain, uint interfaceIndex, string instanceName)
    {
        new Thread(() =>
        {
            var state = new ResolveState();
            var handle = GCHandle.Alloc(state);
            try
            {
                var err = DNSServiceResolve(out var resolveRef, 0, interfaceIndex, serviceName, regType, domain,
                    &OnResolveReply, GCHandle.ToIntPtr(handle));
                if (err != ErrNoError)
                    return;

                // Resolve delivers exactly one useful reply and isn't
                // otherwise self-terminating - stop pumping (and deallocate)
                // as soon as it arrives, per DNSServiceResolve's own docs.
                while (!state.Done && DNSServiceProcessResult(resolveRef) == ErrNoError)
                {
                }
                DNSServiceRefDeallocate(resolveRef);
            }
            finally
            {
                handle.Free();
            }

            if (state.EndPoint != null)
                InstanceFound?.Invoke(this, new MdnsInstanceFound { InstanceName = instanceName, EndPoint = state.EndPoint });
        })
        { IsBackground = true, Name = "BonjourResolve" }.Start();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnResolveReply(IntPtr sdRef, uint flags, uint interfaceIndex, int errorCode,
        IntPtr fullNamePtr, IntPtr hostTargetPtr, ushort port, ushort txtLen, IntPtr txtRecord, IntPtr context)
    {
        if (GCHandle.FromIntPtr(context).Target is not ResolveState state)
            return;

        state.Done = true;
        if (errorCode != ErrNoError)
            return;

        var hostTarget = Marshal.PtrToStringUTF8(hostTargetPtr) ?? "";
        var hostPort = (ushort)IPAddress.NetworkToHostOrder((short)port);
        try
        {
            var addresses = Dns.GetHostAddresses(hostTarget.TrimEnd('.'));
            var address = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork);
            if (address != null)
                state.EndPoint = new IPEndPoint(address, hostPort);
        }
        catch (SocketException)
        {
            // Peer vanished between the mDNS answer and this lookup - just
            // not found this round; a later re-browse retries.
        }
    }

    public void Stop()
    {
        if (_registerRef != IntPtr.Zero)
        {
            DNSServiceRefDeallocate(_registerRef);
            _registerRef = IntPtr.Zero;
        }
        if (_browseRef != IntPtr.Zero)
        {
            DNSServiceRefDeallocate(_browseRef);
            _browseRef = IntPtr.Zero;
        }
        if (_browseContext.IsAllocated)
            _browseContext.Free();
    }

    public void Dispose() => Stop();
}
