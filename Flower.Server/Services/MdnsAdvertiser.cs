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

        var port = ResolveBoundPort();
        if (port is null)
        {
            // Nothing actionable for the operator to fix and nothing else
            // breaks, so this is a warning and not a startup failure: the
            // server still serves every request, it just has to be reached by
            // address rather than found.
            logger.LogWarning("Could not determine the bound port; skipping mDNS advertisement.");
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

    private int? ResolveBoundPort()
    {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        foreach (var address in addresses ?? [])
        {
            // Kestrel reports wildcard binds as http://[::]:4533 or
            // http://+:4533, neither of which Uri will parse - substituting a
            // real host keeps the only part being read here, the port.
            var normalized = address.Replace("[::]", "localhost").Replace("//+", "//localhost").Replace("//*", "//localhost");
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.Port > 0)
                return uri.Port;
        }
        return null;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _backend?.Dispose();
}
