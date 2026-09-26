using System.Net;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Flower.Server.Configuration;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Tests;

// Two servers answering to one name showed as a single sidebar row flipping
// between two machines, and a pairing code typed into it went to whichever one
// the row pointed at just then. These are MdnsAdvertiser claiming a name the
// way Bonjour would - see its class comment - on a fake network, since the
// point is what gets announced, not whether multicast works on the test box.
public class MdnsNameClaimTests
{
    private const string Ours = "bbbb0000000000000000000000000000";

    private static readonly IPEndPoint OtherBox = new(IPAddress.Parse("192.168.1.20"), 4533);
    private static readonly IPEndPoint ThirdBox = new(IPAddress.Parse("192.168.1.30"), 4533);
    private static readonly IPEndPoint OurOwnAddress = new(IPAddress.Parse("192.168.1.10"), 4533);

    [Fact]
    public async Task A_free_name_is_announced_as_given()
    {
        var network = new FakeNetwork();
        using var advertiser = Advertiser(network, "Flower");

        await advertiser.ClaimAsync(force: false);

        Assert.Equal(["Flower"], network.AnnouncedBy(advertiser));
        Assert.Equal("Flower", advertiser.Name);
        Assert.Null(advertiser.ClaimedInsteadOf);
    }

    [Fact]
    public async Task A_name_another_server_answers_to_becomes_name_2()
    {
        var network = new FakeNetwork();
        network.Other("Flower", OtherBox, "aaaa-another-server");
        using var advertiser = Advertiser(network, "Flower");

        await advertiser.ClaimAsync(force: false);

        Assert.Equal(["Flower (2)"], network.AnnouncedBy(advertiser));
        // What /info reports, so a client's label matches the row it found.
        Assert.Equal("Flower (2)", advertiser.Name);
        Assert.Equal("Flower", advertiser.ClaimedInsteadOf);
    }

    [Fact]
    public async Task Numbering_skips_names_that_are_also_taken()
    {
        var network = new FakeNetwork();
        network.Other("Flower", OtherBox, "aaaa-one");
        network.Other("Flower (2)", ThirdBox, "aaaa-two");
        using var advertiser = Advertiser(network, "Flower");

        await advertiser.ClaimAsync(force: false);

        Assert.Equal("Flower (3)", advertiser.Name);
    }

    // This server's previous run, or a sleep proxy answering for it: the same
    // name, and the fingerprint that answers is this server's own.
    [Fact]
    public async Task A_record_that_answers_as_this_server_does_not_take_the_name()
    {
        var network = new FakeNetwork();
        network.Other("Flower", OurOwnAddress, Ours);
        using var advertiser = Advertiser(network, "Flower");

        await advertiser.ClaimAsync(force: false);

        Assert.Equal("Flower", advertiser.Name);
    }

    [Fact]
    public async Task A_record_nobody_answers_for_does_not_take_the_name()
    {
        var network = new FakeNetwork();
        network.Other("Flower", OtherBox, fingerprint: null);
        using var advertiser = Advertiser(network, "Flower");

        await advertiser.ClaimAsync(force: false);

        Assert.Equal("Flower", advertiser.Name);
    }

    // The case a claim made only at startup would miss: renamed from the
    // settings page, while running, onto a name another server already has.
    [Fact]
    public async Task A_rename_onto_a_taken_name_is_claimed_as_name_2_without_a_restart()
    {
        var network = new FakeNetwork();
        network.Other("Kitchen", OtherBox, "aaaa-kitchen");
        var options = new MutableOptions(new FlowerServerOptions { Alias = "Flower" });
        using var advertiser = Advertiser(network, options);
        await advertiser.StartedAsync(TestContext.Current.CancellationToken);
        await advertiser.ClaimAsync(force: false);

        options.Set(new FlowerServerOptions { Alias = "Kitchen" });
        await advertiser.ClaimAsync(force: false);

        Assert.Equal("Kitchen (2)", advertiser.Name);
        Assert.Equal(["Kitchen (2)"], network.AnnouncedBy(advertiser));
        Assert.Contains("Flower", network.Withdrawn); // the old name said goodbye
    }

    [Fact]
    public async Task Turning_announcement_off_withdraws_the_name()
    {
        var network = new FakeNetwork();
        var options = new MutableOptions(new FlowerServerOptions { Alias = "Flower" });
        using var advertiser = Advertiser(network, options);
        await advertiser.StartedAsync(TestContext.Current.CancellationToken);
        await advertiser.ClaimAsync(force: false);

        options.Set(new FlowerServerOptions { Alias = "Flower", AdvertiseOnLan = false });
        await advertiser.ClaimAsync(force: false);

        Assert.Empty(network.AnnouncedBy(advertiser));
        Assert.Equal("Flower", advertiser.Name);
    }

    // Two servers holding one name - started together, or one of them an older
    // build that never checks. Exactly one yields: the greater fingerprint.
    [Fact]
    public async Task Of_two_servers_announcing_one_name_the_greater_fingerprint_yields()
    {
        var network = new FakeNetwork();
        using var advertiser = Advertiser(network, "Flower");
        await advertiser.ClaimAsync(force: false);

        network.Other("Flower", OtherBox, "aaaa-sorts-first");
        await network.Settled();

        Assert.Equal("Flower (2)", advertiser.Name);
    }

    [Fact]
    public async Task Of_two_servers_announcing_one_name_the_lesser_fingerprint_keeps_it()
    {
        var network = new FakeNetwork();
        using var advertiser = Advertiser(network, "Flower");
        await advertiser.ClaimAsync(force: false);

        network.Other("Flower", OtherBox, "cccc-sorts-last");
        await network.Settled();

        Assert.Equal("Flower", advertiser.Name);
    }

    [Fact]
    public async Task A_server_bound_only_to_loopback_announces_nothing()
    {
        var network = new FakeNetwork();
        using var advertiser = Advertiser(network, new MutableOptions(new FlowerServerOptions { Alias = "Flower" }),
            bound: ["http://127.0.0.1:4533"]);

        await advertiser.StartedAsync(TestContext.Current.CancellationToken);
        await advertiser.ClaimAsync(force: false);

        Assert.Empty(network.AnnouncedBy(advertiser));
    }

    private static MdnsAdvertiser Advertiser(FakeNetwork network, string alias) =>
        Advertiser(network, new MutableOptions(new FlowerServerOptions { Alias = alias }));

    private static MdnsAdvertiser Advertiser(FakeNetwork network, MutableOptions options, string[]? bound = null)
    {
        var advertiser = new MdnsAdvertiser(
            new FakeServer(bound ?? ["http://0.0.0.0:4533"]), options, Ours,
            network.NewBackend, network.FingerprintAtAsync, TimeSpan.Zero,
            NullLogger<MdnsAdvertiser>.Instance);

        // StartedAsync reads the bound port; the tests that do not call it
        // themselves still need it read.
        if (bound == null)
            advertiser.StartedAsync(CancellationToken.None).GetAwaiter().GetResult();

        network.Owner = advertiser;
        return advertiser;
    }

    // Everyone else on the LAN, plus the backends the advertiser under test
    // creates. Browsing hears every current announcement at once; announcing
    // is heard by every backend currently alive.
    private sealed class FakeNetwork
    {
        private readonly List<(string Name, IPEndPoint EndPoint)> _others = [];
        private readonly Dictionary<IPEndPoint, string?> _fingerprints = [];
        private readonly List<FakeBackend> _backends = [];
        private readonly List<Task> _pending = [];

        public MdnsAdvertiser? Owner { get; set; }
        public List<string> Withdrawn { get; } = [];

        public IMdnsBackend NewBackend()
        {
            var backend = new FakeBackend(this);
            lock (_backends)
                _backends.Add(backend);
            return backend;
        }

        public void Other(string name, IPEndPoint endpoint, string? fingerprint)
        {
            _others.Add((name, endpoint));
            _fingerprints[endpoint] = fingerprint;
            foreach (var backend in Live())
                backend.Hear(name, endpoint);
        }

        public Task<string?> FingerprintAtAsync(IPEndPoint endpoint, CancellationToken ct)
        {
            var lookup = Task.FromResult(_fingerprints.GetValueOrDefault(endpoint));
            lock (_pending)
                _pending.Add(lookup);
            return lookup;
        }

        // Lets a reaction to an announcement - a check, then a re-claim - run
        // to the end before the test looks.
        public async Task Settled()
        {
            await Task.Delay(50);
            await Owner!.ClaimAsync(force: false);
        }

        public List<string> AnnouncedBy(MdnsAdvertiser _) =>
            Live().Where(b => b.Announced != null).Select(b => b.Announced!).ToList();

        public IEnumerable<(string Name, IPEndPoint EndPoint)> Everyone(FakeBackend asking) =>
            _others.Concat(Live().Where(b => b != asking && b.Announced != null).Select(b => (b.Announced!, OurOwnAddress)));

        private List<FakeBackend> Live()
        {
            lock (_backends)
                return _backends.Where(b => !b.Disposed).ToList();
        }
    }

    private sealed class FakeBackend(FakeNetwork network) : IMdnsBackend
    {
        public event EventHandler<MdnsInstanceFound>? InstanceFound;
        public event EventHandler<string>? InstanceLost;

        public string? Announced { get; private set; }
        public bool Disposed { get; private set; }

        public void Hear(string name, IPEndPoint endpoint) =>
            InstanceFound?.Invoke(this, new MdnsInstanceFound { InstanceName = $"{name}.{SyncProtocol.ServiceType}.local", EndPoint = endpoint });

        public void Browse(string serviceType)
        {
            foreach (var (name, endpoint) in network.Everyone(this).ToList())
                Hear(name, endpoint);
        }

        public void Advertise(string instanceName, string serviceType, int port) => Announced = instanceName;

        public void Stop()
        {
            if (Announced != null)
                network.Withdrawn.Add(Announced);
            Announced = null;
            InstanceLost?.Invoke(this, "");
        }

        public void Dispose()
        {
            Stop();
            Disposed = true;
        }
    }

    private sealed class MutableOptions(FlowerServerOptions value) : IOptionsMonitor<FlowerServerOptions>
    {
        private Action<FlowerServerOptions, string?>? _listener;

        public FlowerServerOptions CurrentValue { get; private set; } = value;

        public void Set(FlowerServerOptions next)
        {
            CurrentValue = next;
            _listener?.Invoke(next, null);
        }

        public FlowerServerOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<FlowerServerOptions, string?> listener)
        {
            _listener = listener;
            return null;
        }
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
}
