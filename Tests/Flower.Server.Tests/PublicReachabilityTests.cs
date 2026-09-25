using System.Net;
using System.Security.Cryptography;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Flower.Server.Configuration;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Tests;

// What a server with the door open tells clients to dial from outside, and
// whether it checks that address leads back to itself - see PublicReachability.
// Every request here goes to a stub: the public address lookup and the dial
// back in are both calls to the internet on a real server.
public class PublicReachabilityTests
{
    private const string PublicIp = "203.0.113.7";

    private static readonly string[] BothListeners = ["http://0.0.0.0:4533", "https://0.0.0.0:4534"];

    private static FlowerServerOptions Open(string advertisedHost = "") =>
        new() { AllowPublicAccess = true, AdvertisedHost = advertisedHost };

    [Fact]
    public async Task Advertises_the_public_address_at_the_https_port_once_the_door_is_open()
    {
        using var harness = new Harness(_ => Harness.InfoFrom(Harness.Key.Fingerprint));

        await harness.Reachability.CheckAsync(Open(), TestContext.Current.CancellationToken);

        Assert.Equal($"https://{PublicIp}:4534", harness.Reachability.OriginFor(Open()));
    }

    [Fact]
    public async Task Advertises_nothing_while_the_door_is_shut()
    {
        using var harness = new Harness(_ => Harness.InfoFrom(Harness.Key.Fingerprint));
        var shut = new FlowerServerOptions { AllowPublicAccess = false };

        Assert.Null(await harness.Reachability.CheckAsync(shut, TestContext.Current.CancellationToken));
        Assert.Null(harness.Reachability.OriginFor(shut));
        Assert.Equal(0, harness.Probes.Calls); // and never asked anyone
    }

    // An operator who named the address - a tunnel, a proxy, a DNS name - has
    // said how the server is reached, and the home IP is not it.
    [Fact]
    public async Task Stands_aside_for_an_advertised_host()
    {
        using var harness = new Harness(_ => Harness.InfoFrom(Harness.Key.Fingerprint));
        var named = Open("https://music.example.com");

        Assert.Null(await harness.Reachability.CheckAsync(named, TestContext.Current.CancellationToken));
        Assert.Null(harness.Reachability.OriginFor(named));
    }

    [Fact]
    public async Task Advertises_nothing_with_tls_off()
    {
        using var harness = new Harness(_ => Harness.InfoFrom(Harness.Key.Fingerprint), ["http://0.0.0.0:4533"]);

        Assert.Null(await harness.Reachability.CheckAsync(Open(), TestContext.Current.CancellationToken));
        Assert.Null(harness.Reachability.OriginFor(Open()));
    }

    [Fact]
    public async Task Reachable_when_its_own_fingerprint_answers()
    {
        using var harness = new Harness(_ => Harness.InfoFrom(Harness.Key.Fingerprint));

        var result = await harness.Reachability.CheckAsync(Open(), TestContext.Current.CancellationToken);

        Assert.Equal(new PublicReachabilityResult($"https://{PublicIp}:4534", PublicReachabilityStatus.Reachable), result);
        Assert.Equal($"https://{PublicIp}:4534{SyncProtocol.InfoPath}", harness.Dials.Requested.Single().ToString());
    }

    // The router forwarding the port to some other machine: something
    // answered, and it was not this server.
    [Fact]
    public async Task Tells_another_server_answering_apart_from_this_one()
    {
        using var harness = new Harness(_ => Harness.InfoFrom("someone-else"));

        var result = await harness.Reachability.CheckAsync(Open(), TestContext.Current.CancellationToken);

        Assert.Equal(PublicReachabilityStatus.OtherServerAnswered, result!.Status);
    }

    [Fact]
    public async Task Tells_a_router_page_apart_from_this_server()
    {
        using var harness = new Harness(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>login</html>") });

        var result = await harness.Reachability.CheckAsync(Open(), TestContext.Current.CancellationToken);

        Assert.Equal(PublicReachabilityStatus.OtherServerAnswered, result!.Status);
    }

    // Unreachable is a warning and nothing more: a router that cannot loop a
    // connection back to itself fails this for a server the internet reaches
    // fine, so the address is advertised regardless.
    [Fact]
    public async Task Still_advertises_an_address_it_could_not_reach()
    {
        using var harness = new Harness(_ => throw new HttpRequestException("refused"));

        var result = await harness.Reachability.CheckAsync(Open(), TestContext.Current.CancellationToken);

        Assert.Equal(PublicReachabilityStatus.Unreachable, result!.Status);
        Assert.Equal($"https://{PublicIp}:4534", harness.Reachability.OriginFor(Open()));
    }

    [Fact]
    public async Task Answers_a_second_look_from_the_cache()
    {
        using var harness = new Harness(_ => Harness.InfoFrom(Harness.Key.Fingerprint));

        await harness.Reachability.CheckAsync(Open(), TestContext.Current.CancellationToken);
        await harness.Reachability.CheckAsync(Open(), TestContext.Current.CancellationToken);

        Assert.Single(harness.Dials.Requested);
    }

    private sealed class Harness : IDisposable
    {
        public static readonly DeviceSigningKey Key = CreateKey();

        public StubHandler Probes { get; }
        public StubHandler Dials { get; }
        public IServer Server { get; }
        public PublicReachability Reachability { get; }

        public Harness(Func<HttpRequestMessage, HttpResponseMessage> dial, string[]? bound = null)
        {
            Probes = new StubHandler(_ => StubHandler.Ok($"ip={PublicIp}\n"));
            Dials = new StubHandler(dial);
            Server = new FakeServer(bound ?? BothListeners);
            Reachability = new PublicReachability(
                new PublicAddressProbe(NullLogger<PublicAddressProbe>.Instance, Probes),
                Server, Key, new StaticOptionsMonitor(Open()), new NeverStarted(),
                NullLogger<PublicReachability>.Instance, Dials);
        }

        public static HttpResponseMessage InfoFrom(string fingerprint) =>
            StubHandler.Ok($$"""{"alias":"Flower","fingerprint":"{{fingerprint}}"}""");

        private static DeviceSigningKey CreateKey()
        {
            var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var q = ecdsa.ExportParameters(false).Q;
            var raw = new byte[65];
            raw[0] = 0x04;
            Buffer.BlockCopy(q.X!, 0, raw, 1, 32);
            Buffer.BlockCopy(q.Y!, 0, raw, 33, 32);
            return new DeviceSigningKey(ecdsa, raw);
        }

        public void Dispose() => Reachability.Dispose();
    }

    private sealed class FakeServer : IServer
    {
        public FakeServer(IEnumerable<string> addresses)
        {
            var feature = new ServerAddressesFeature();
            foreach (var address in addresses)
                feature.Addresses.Add(address);
            Features.Set<IServerAddressesFeature>(feature);
        }

        public IFeatureCollection Features { get; } = new FeatureCollection();

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken ct) where TContext : notnull =>
            Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed class StaticOptionsMonitor(FlowerServerOptions value) : IOptionsMonitor<FlowerServerOptions>
    {
        public FlowerServerOptions CurrentValue => value;

        public FlowerServerOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<FlowerServerOptions, string?> listener) => null;
    }

    private sealed class NeverStarted : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;

        public List<Uri> Requested { get; } = [];

        public static HttpResponseMessage Ok(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            lock (Requested)
                Requested.Add(request.RequestUri!);
            return Task.FromResult(reply(request));
        }
    }
}
