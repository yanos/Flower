using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Importer;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests;

// The client half of docs/TRUST-BOUNDARY-PLAN.md step 3: a refusal is read by
// its code, not its status. The one that matters most is which refusals may
// end a pairing - device-unknown, and nothing else, however it arrives.
[Collection("PlatformDataDirectory")]
public class RefusalContractTests : IDisposable
{
    private readonly string? _originalHome;
    private readonly string _tempHome;

    public RefusalContractTests()
    {
        _originalHome = Environment.GetEnvironmentVariable("HOME");
        _tempHome = Path.Combine(Path.GetTempPath(), "flower-refusal-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempHome);
        Environment.SetEnvironmentVariable("HOME", _tempHome);
        PlatformDataDirectory.Current = _tempHome;
        SignatureClock.Reset();
    }

    public void Dispose()
    {
        SignatureClock.Reset();
        Environment.SetEnvironmentVariable("HOME", _originalHome);
        PlatformDataDirectory.Current = AssemblySetup.DefaultDataDirectory;
        try { Directory.Delete(_tempHome, recursive: true); } catch { /* best effort */ }
    }

    private static FakePeerHttpServer Refusing(int status, string? contentType, string body) =>
        new(async context =>
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = status;
            if (contentType != null)
                context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });

    private static string Problem(int status, string code, DateTimeOffset? serverTime = null) =>
        FlowerProblem.Serialize(FlowerProblem.Create(status, code, serverTime: serverTime));

    private async Task<(bool Rejected, LibrarySyncResult Result)> SyncAgainstAsync(FakePeerHttpServer peer)
    {
        using var key = TestSigningKey.Create();
        var service = new LibrarySyncService(
            new Library([]),
            new DeviceIdentity { Fingerprint = key.Fingerprint, Alias = "Client" },
            key,
            new AppSettings(),
            new ServerStarBaselineStore(NullLogger<ServerStarBaselineStore>.Instance),
            TestLogArchive.InTempDirectory(),
            NullLogger<LibrarySyncService>.Instance,
            NullLogger<RemoteLibraryImporter>.Instance);

        var rejected = false;
        service.PeerTrustRejected += (_, _) => rejected = true;

        var result = await service.SyncWithAsync(new DiscoveredDevice
        {
            InstanceName = "peer",
            BaseUri = NetworkDiscoveryService.HttpOrigin(new IPEndPoint(IPAddress.Loopback, peer.Port)),
            Fingerprint = "peer-fingerprint",
        });
        return (rejected, result);
    }

    [Fact]
    public async Task Device_unknown_is_the_refusal_that_reads_as_revoked()
    {
        using var peer = Refusing(401, FlowerProblem.ContentType, Problem(401, ProblemCodes.DeviceUnknown));

        var (rejected, result) = await SyncAgainstAsync(peer);

        Assert.True(rejected);
        Assert.Equal(SyncFailure.NotTrusted, result.Failure);
    }

    // A stale signature is a 401 too, and must never end a pairing - the
    // laptop that slept with a request in flight.
    [Theory]
    [InlineData(ProblemCodes.ClockSkew)]
    [InlineData(ProblemCodes.SignatureInvalid)]
    [InlineData(ProblemCodes.NonceReused)]
    public async Task Any_other_authentication_failure_leaves_the_pairing_alone(string code)
    {
        using var peer = Refusing(401, FlowerProblem.ContentType, Problem(401, code, DateTimeOffset.UtcNow));

        var (rejected, result) = await SyncAgainstAsync(peer);

        Assert.False(rejected);
        Assert.NotEqual(SyncFailure.NotTrusted, result.Failure);
    }

    // What used to unpair a device: a bare 403, which a proxy, a tunnel or a
    // captive portal can answer as readily as the server.
    [Fact]
    public async Task A_bare_403_from_something_on_the_path_does_not_read_as_revoked()
    {
        using var peer = Refusing(403, "text/html", "<html><body>Access denied</body></html>");

        var (rejected, result) = await SyncAgainstAsync(peer);

        Assert.False(rejected);
        Assert.NotEqual(SyncFailure.NotTrusted, result.Failure);
    }

    // A clock-skew refusal carries the server's time, and every client
    // PeerHttpClient builds corrects the clock it signs with from it.
    [Fact]
    public async Task A_clock_skew_refusal_corrects_the_clock_the_next_request_signs_with()
    {
        var serverTime = DateTimeOffset.UtcNow.AddMinutes(-7);
        using var peer = Refusing(401, FlowerProblem.ContentType, Problem(401, ProblemCodes.ClockSkew, serverTime));
        using var http = PeerHttpClient.Create(TimeSpan.FromSeconds(5));

        using var response = await http.GetAsync($"http://127.0.0.1:{peer.Port}/api/flower/v1/library");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True((SignatureClock.Offset - TimeSpan.FromMinutes(-7)).Duration() < TimeSpan.FromSeconds(5));
        Assert.True((SignatureClock.UtcNow - serverTime).Duration() < TimeSpan.FromSeconds(5));

        // And the refusal is still there for the caller to read.
        Assert.Equal(ProblemCodes.ClockSkew, FlowerProblem.TryRead(await response.Content.ReadAsStringAsync())?.Code);
    }

    // The media path signs its own requests, so it can send one again at once
    // in the corrected time rather than leaving the track to fail.
    [Fact]
    public async Task A_signed_media_request_is_sent_again_once_in_the_corrected_time()
    {
        var serverTime = DateTimeOffset.UtcNow.AddMinutes(10);
        var requests = 0;
        using var peer = new FakePeerHttpServer(async context =>
        {
            var first = Interlocked.Increment(ref requests) == 1;
            var bytes = Encoding.UTF8.GetBytes(first ? Problem(401, ProblemCodes.ClockSkew, serverTime) : "audio");
            context.Response.StatusCode = first ? 401 : 200;
            context.Response.ContentType = first ? FlowerProblem.ContentType : "audio/wav";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });
        using var key = TestSigningKey.Create();
        var credentials = new SignedDeviceCredentials(new DeviceIdentity { Fingerprint = key.Fingerprint, Alias = "Client" }, key);
        using var http = new HttpClient(new PeerCredentialsHandler(() => credentials, new HttpClientHandler()));

        using var response = await http.GetAsync($"http://127.0.0.1:{peer.Port}/api/flower/v1/stream?id=abc");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, requests);
    }
}
