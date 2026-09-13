using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Flower.Server.Configuration;
using Flower.Services;

namespace Flower.Server.Tests;

// The whole certificate story over a real TLS handshake, which is the only
// place it can actually be shown to work: the other tests check that the key in
// the certificate is the key that was paired, and this one checks that a real
// TLS stack on each end agrees.
//
// A bare Kestrel host rather than WebApplicationFactory, deliberately - the
// test transport that factory installs never performs a handshake at all, so
// it cannot answer the question this file exists to ask.
public class PinnedTlsHandshakeTests : IAsyncDisposable
{
    private readonly Func<byte[], bool>? _originalPin = PeerHttpClient.IsPinnedServerKey;
    private readonly ECDsa _serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private WebApplication? _host;

    public async ValueTask DisposeAsync()
    {
        PeerHttpClient.IsPinnedServerKey = _originalPin;
        if (_host != null)
            await _host.DisposeAsync();
        _serverKey.Dispose();
        GC.SuppressFinalize(this);
    }

    // Port 0 so the OS picks a free one - several of these can run alongside
    // whatever else is listening on the developer's machine.
    private async Task<Uri> StartAsync(ECDsa key)
    {
        var certificate = ServerTls.SelfSigned(key, "flower-tls-tests");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(
            IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.Run(context => context.Response.WriteAsync("flower"));

        await app.StartAsync(TestContext.Current.CancellationToken);
        _host = app;

        var address = app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses
            .First();
        return new Uri(address);
    }

    [Fact]
    public async Task A_paired_client_reaches_a_server_over_its_own_self_signed_certificate()
    {
        var address = await StartAsync(_serverKey);
        var paired = DeviceCertificate.PublicKeyRaw(_serverKey);
        PeerHttpClient.IsPinnedServerKey = key => key.SequenceEqual(paired);

        using var client = PeerHttpClient.Create(TimeSpan.FromSeconds(10));
        // Dialled at 127.0.0.1 while the certificate names "flower-tls-tests",
        // so this is a name mismatch as well as an untrusted chain - both of
        // which the pin is supposed to subsume.
        var body = await client.GetStringAsync(address, TestContext.Current.CancellationToken);

        Assert.Equal("flower", body);
    }

    [Fact]
    public async Task An_unpaired_client_does_not()
    {
        var address = await StartAsync(_serverKey);
        using var someoneElse = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var paired = DeviceCertificate.PublicKeyRaw(someoneElse);
        PeerHttpClient.IsPinnedServerKey = key => key.SequenceEqual(paired);

        using var client = PeerHttpClient.Create(TimeSpan.FromSeconds(10));

        // The failure a client sees when it cannot validate the certificate is
        // a transport failure, which is what makes an unusable https origin
        // present to NetworkDiscoveryService as "did not answer" and fall back
        // to the plain one rather than throwing somewhere user-visible.
        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetStringAsync(address, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_client_that_has_paired_with_nobody_does_not_either()
    {
        var address = await StartAsync(_serverKey);
        PeerHttpClient.IsPinnedServerKey = null;

        using var client = PeerHttpClient.Create(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetStringAsync(address, TestContext.Current.CancellationToken));
    }
}
