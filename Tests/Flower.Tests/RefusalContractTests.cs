using System;
using System.IO;
using System.Linq;
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

    // The server a test talks to: its key, and the device row a client keeps
    // for it - the public key /info served and the fingerprint pinned at
    // pairing.
    private sealed class Server : IDisposable
    {
        public DeviceSigningKey Key { get; } = TestSigningKey.Create();
        public void Dispose() => Key.Dispose();
    }

    // A server refusing every request as device-unknown, signing the refusal
    // with signer - the real server's own key, another key, or none - bound to
    // the nonce of the request it answers, as Flower.Server does.
    private static FakePeerHttpServer RefusingAsUnknown(DeviceSigningKey? signer) =>
        new(async context =>
        {
            var bytes = Encoding.UTF8.GetBytes(Problem(401, ProblemCodes.DeviceUnknown));
            context.Response.StatusCode = 401;
            context.Response.ContentType = FlowerProblem.ContentType;
            if (signer != null && context.Request.Headers["X-Flower-Nonce"] is { Length: > 0 } nonce)
            {
                var (signature, timestamp) = signer.SignResponse(
                    context.Request.Url!.AbsolutePath, 401, bytes, IdentityHeaderEncoding.Decode(nonce));
                context.Response.Headers[ServerResponseSignature.SignatureHeader] = signature;
                context.Response.Headers[ServerResponseSignature.TimestampHeader] = timestamp;
            }
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });

    private async Task<(bool Rejected, LibrarySyncResult Result)> SyncAgainstAsync(
        FakePeerHttpServer peer, DeviceSigningKey? server = null)
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
            NullLogger<RemoteLibraryImporter>.Instance,
            NullLogger<Flower.Importer.Importer>.Instance);

        var rejected = false;
        service.PeerTrustRejected += (_, _) => rejected = true;

        var result = await service.SyncWithAsync(new DiscoveredDevice
        {
            InstanceName = "peer",
            BaseUri = NetworkDiscoveryService.HttpOrigin(new IPEndPoint(IPAddress.Loopback, peer.Port)),
            Fingerprint = server?.Fingerprint ?? "peer-fingerprint",
            PublicKey = server?.PublicKeyBase64 ?? "",
        });
        return (rejected, result);
    }

    // docs/TRUST-BOUNDARY-PLAN.md step 4: device-unknown ends a pairing only
    // when the server this device paired with signed it.
    [Fact]
    public async Task A_device_unknown_signed_by_the_paired_server_reads_as_revoked()
    {
        using var server = new Server();
        using var peer = RefusingAsUnknown(server.Key);

        var (rejected, result) = await SyncAgainstAsync(peer, server.Key);

        Assert.True(rejected);
        Assert.Equal(SyncFailure.NotTrusted, result.Failure);
    }

    // Anything on the path can say device-unknown; only the paired server's
    // key can sign it. Unsigned, or signed by any other key, it is one failed
    // request and the pairing stays.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_device_unknown_the_paired_server_did_not_sign_leaves_the_pairing_alone(bool signedBySomeoneElse)
    {
        using var server = new Server();
        using var impostor = TestSigningKey.Create();
        using var peer = RefusingAsUnknown(signedBySomeoneElse ? impostor : null);

        var (rejected, result) = await SyncAgainstAsync(peer, server.Key);

        Assert.False(rejected);
        Assert.NotEqual(SyncFailure.NotTrusted, result.Failure);
    }

    // The signature is bound to the nonce of the request it answers, so a
    // genuine refusal captured from an earlier exchange - before a re-pairing,
    // say - verifies against nothing a client sends afterwards.
    [Fact]
    public void A_signed_refusal_does_not_verify_as_the_answer_to_a_different_request()
    {
        using var server = TestSigningKey.Create();
        var body = Encoding.UTF8.GetBytes(Problem(401, ProblemCodes.DeviceUnknown));
        var (signature, timestamp) = server.SignResponse("/api/flower/v1/library", 401, body, "nonce-of-an-old-request");

        Assert.True(ServerResponseSignature.Verify("/api/flower/v1/library", 401, body, timestamp, signature,
            "nonce-of-an-old-request", server.PublicKeyBase64, server.Fingerprint));
        Assert.False(ServerResponseSignature.Verify("/api/flower/v1/library", 401, body, timestamp, signature,
            "nonce-of-this-request", server.PublicKeyBase64, server.Fingerprint));
    }

    // The /info half of step 4: trustsCaller: false is how a revoked device
    // finds out, and it is believed only when the answer is signed by the key
    // it names. An unsigned "no", or one signed by a key that does not hash to
    // the fingerprint the answer claims, leaves TrustsUs where it was.
    [Theory]
    [InlineData("signed by the server", false)]
    [InlineData("unsigned", true)]
    [InlineData("signed by another key", true)]
    public async Task An_info_answer_says_no_only_when_its_own_key_signed_it(string how, bool stillTrusted)
    {
        using var server = TestSigningKey.Create();
        using var impostor = TestSigningKey.Create();
        using var me = TestSigningKey.Create();
        var signer = how switch
        {
            "signed by the server" => server,
            "signed by another key" => impostor,
            _ => null,
        };
        var identity = new DeviceIdentity { Fingerprint = me.Fingerprint, Alias = "Me" };
        using var discovery = new NetworkDiscoveryService(
            identity, NullLogger<NetworkDiscoveryService>.Instance, new FakeMdnsBackend(),
            new HttpClient(new InfoSaying(server, signer, trustsCaller: false)),
            new SignedDeviceCredentials(identity, me));

        var device = await discovery.AddRememberedAsync("192.168.1.40:4533", TestContext.Current.CancellationToken);

        Assert.Equal(server.Fingerprint, device!.Fingerprint);
        Assert.Equal(stillTrusted, device.TrustsUs);
    }

    // Answers /info as the server whose key is `server`, signed by `signer`
    // over the bytes it sends and the nonce of the request it answers.
    private sealed class InfoSaying(DeviceSigningKey server, DeviceSigningKey? signer, bool trustsCaller) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(
                new SyncInfoResponseDto("Basement", "2.0", null, "server", server.Fingerprint, server.PublicKeyBase64,
                    Download: false, TrustsCaller: trustsCaller, LibraryToken: "t"),
                SyncProtocolJsonContext.Default.SyncInfoResponseDto);
            var body = Encoding.UTF8.GetBytes(json);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
                RequestMessage = request,
            };

            if (signer != null && request.Headers.TryGetValues("X-Flower-Nonce", out var nonces))
            {
                var (signature, timestamp) = signer.SignResponse(
                    request.RequestUri!.AbsolutePath, 200, body, IdentityHeaderEncoding.Decode(nonces.First()));
                response.Headers.Add(ServerResponseSignature.SignatureHeader, signature);
                response.Headers.Add(ServerResponseSignature.TimestampHeader, timestamp);
            }

            return Task.FromResult(response);
        }
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
