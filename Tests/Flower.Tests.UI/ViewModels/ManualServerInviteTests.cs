using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels;

// A flower:// pairing link pasted into the box that takes a server's address -
// which is where somebody holding a link and no address puts it. It used to be
// dialled as an address, and said nothing answered at "flower://pair?...".
[Collection("PlatformDataDirectory")]
public class ManualServerInviteTests : PinnedDataDirectory
{
    private sealed class FakeInfoHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responsesByAuthority = [];

        public void RespondAt(string authority, string json) => _responsesByAuthority[authority] = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!_responsesByAuthority.TryGetValue(request.RequestUri!.Authority, out var json))
                throw new HttpRequestException("simulated unreachable peer");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private sealed class NoListedPeers : IPeerSyncHost
    {
        public IReadOnlyList<DiscoveredDevice> ListedPeers => [];
    }

    private readonly FakeInfoHandler _handler = new();
    private readonly AppSettings _appSettings = new();
    private readonly NetworkDiscoveryService _discovery;

    public ManualServerInviteTests()
    {
        _discovery = Own(new NetworkDiscoveryService(
            new DeviceIdentity { Fingerprint = "my-fp", Alias = "Me" },
            NullLogger<NetworkDiscoveryService>.Instance,
            new FakeMdnsBackend(),
            new HttpClient(_handler)));
    }

    private PeerSyncCoordinator Coordinator() => Own(new PeerSyncCoordinator(
        new NoListedPeers(), _appSettings, appSettingsStore: null, deviceIdentityStore: null,
        NullLogger<PeerSyncCoordinator>.Instance, _discovery));

    private const string Lan = "http://192.168.50.52:4533";
    private const string Public = "https://38.133.38.247:4534";

    private static string ServerInfo(string fingerprint) =>
        $$"""{"alias":"Basement","fingerprint":"{{fingerprint}}","isServer":true,"deviceType":"server"}""";

    // Away from home: the LAN address in the link answers nowhere, the public
    // one does, and that is the one kept - with the code, so the caller can
    // pair in the same gesture.
    [AvaloniaFact]
    public async Task A_pasted_link_finds_the_server_at_whichever_of_its_addresses_answers()
    {
        _handler.RespondAt("38.133.38.247:4534", ServerInfo("server-fp"));
        var invite = new PairingInvite([Lan, Public], "YRR5M", "server-fp");

        var result = await Coordinator().AddManualServerAsync(invite.ToString());

        Assert.Equal("server-fp", result.Server?.Fingerprint);
        Assert.Equal(invite, result.Invite);
        Assert.Equal([Public], _appSettings.ManualServerAddresses);
        // The address that answered nowhere is not left behind as a remembered
        // peer, which is never pruned and would be polled for good.
        Assert.DoesNotContain(_discovery.KnownDevices, d => d.BaseUri.Host == "192.168.50.52");
    }

    // The fingerprint decides, not the address: something else answering at
    // an address the link names is not the server the link came from.
    [AvaloniaFact]
    public async Task A_pasted_link_does_not_settle_for_a_different_server_at_its_address()
    {
        _handler.RespondAt("38.133.38.247:4534", ServerInfo("someone-else"));
        var invite = new PairingInvite([Public], "YRR5M", "server-fp");

        var result = await Coordinator().AddManualServerAsync(invite.ToString());

        Assert.Null(result.Server);
        Assert.NotNull(result.Failure);
        Assert.Empty(_appSettings.ManualServerAddresses);
    }

    [AvaloniaFact]
    public async Task A_typed_address_that_answers_nowhere_says_why()
    {
        var result = await Coordinator().AddManualServerAsync("192.168.50.52:4533");

        Assert.Null(result.Server);
        Assert.False(string.IsNullOrEmpty(result.Failure));
    }
}
