using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

using Flower.Server.Configuration;
using Flower.Services;

namespace Flower.Server.Services;

// Announces this server as _flowersync._tcp so a Flower client's sidebar can
// find it without anyone typing an address (SYNC-PLAN.md). Discovery is
// convenience only: appearing here gets the server a row and an address, and
// nothing else - the row is untrusted until a device redeems a pairing code,
// which is what actually carries the fingerprint pin. See PairingInvite.
//
// Advertise-only, deliberately: the server has no reason to browse for peers.
// It uses the same MakaretuMdnsBackend the client browses with (moved into
// Flower.Core for exactly this), so the two cannot drift on the record shape.
//
// IHostedLifecycleService rather than IHostedService: the port has to come from
// the server's actually-bound address, and that is only populated once Kestrel
// has started. StartedAsync runs after it has.
public sealed class MdnsAdvertiser(
    IServer server,
    IOptions<FlowerServerOptions> options,
    ILogger<MdnsAdvertiser> logger) : IHostedLifecycleService, IDisposable
{
    private IMdnsBackend? _backend;

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.AdvertiseOnLan)
            return Task.CompletedTask;

        var addresses = (server.Features.Get<IServerAddressesFeature>()?.Addresses ?? []).ToArray();
        var port = AdvertisablePort(addresses);
        if (port is null)
        {
            // Neither case is actionable for the operator and nothing else
            // breaks, so neither is a startup failure: the server still serves
            // every request, it just has to be reached by address rather than
            // found. Bound-to-loopback is a deliberate choice often enough
            // (a dev instance, a reverse proxy in front) that it is not even a
            // warning.
            if (addresses.Length == 0)
                logger.LogWarning("Could not determine the bound port; skipping mDNS advertisement.");
            else
                logger.LogInformation(
                    "Bound only to loopback ({Addresses}); skipping mDNS advertisement, since nothing off this machine could reach it.",
                    string.Join(", ", addresses));

            return Task.CompletedTask;
        }

        try
        {
            _backend = PlatformMdns.Current ?? new MakaretuMdnsBackend();
            _backend.Advertise(InstanceName(settings), SyncProtocol.ServiceType, port.Value);
            logger.LogInformation(
                "Advertising {Instance} as {ServiceType} on port {Port}.",
                InstanceName(settings), SyncProtocol.ServiceType, port.Value);
        }
        catch (Exception ex)
        {
            // Multicast is routinely unavailable - a container without host
            // networking, a locked-down VLAN, a firewall. Same reasoning as
            // above: not being discoverable is a degraded experience, not a
            // broken server, so it must not take the process down.
            logger.LogWarning(ex, "Could not advertise on the local network; the server is still reachable by address.");
            _backend = null;
        }

        return Task.CompletedTask;
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        // Unadvertise on the way out so a client sees the goodbye and prunes
        // the row immediately, rather than waiting out its own poll failures.
        _backend?.Stop();
        return Task.CompletedTask;
    }

    public static string InstanceName(FlowerServerOptions options) =>
        string.IsNullOrWhiteSpace(options.Alias) ? Environment.MachineName : options.Alias.Trim();

    // The port to advertise, or null if there is nothing worth advertising.
    //
    // Only a non-loopback bind is announced. The mDNS record resolves to this
    // machine's LAN addresses regardless of what Kestrel actually bound, so a
    // server started on --urls http://localhost:5599 would otherwise publish
    // itself as reachable at <lan-ip>:5599, where every client that found it
    // gets a connection refused it can do nothing about - and, advertising
    // under the machine name, collides with the row of whichever server on the
    // box is real. That is a dev-instance mistake rather than a deployment
    // one, which is exactly why it is worth catching here: the symptom shows
    // up on someone else's screen, a hop away from the cause.
    public static int? AdvertisablePort(IEnumerable<string> boundAddresses) =>
        boundAddresses.Select(Parse).FirstOrDefault(uri => uri is { IsLoopback: false })?.Port;

    private static Uri? Parse(string address)
    {
        // Kestrel reports wildcard binds as http://[::]:4533, http://+:4533 or
        // http://*:4533, none of which Uri will parse - substituting 0.0.0.0
        // preserves both things read here, the port and whether the bind is
        // loopback-only, which a wildcard bind is not.
        var normalized = address.Replace("[::]", "0.0.0.0").Replace("//+", "//0.0.0.0").Replace("//*", "//0.0.0.0");
        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.Port > 0 ? uri : null;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _backend?.Dispose();
}
