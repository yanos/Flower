using Microsoft.AspNetCore.Http.Features;

using Flower.Persistence;
using Flower.Services;

namespace Flower.Server.Services;

// The budgets that sit in front of every signed route, and the order they are
// checked in - docs/TRUST-BOUNDARY-PLAN.md step 2.
//
// Every budget used to be keyed by source address and charged before anyone
// was authenticated. That turned away strangers, which is what it was for, and
// it also put every listener behind one proxy, tunnel or CGNAT into a single
// bucket - so one device could spend everybody's, and a household's ordinary
// traffic could lock itself out. Home Assistant's own documentation warns of
// exactly this about its IP bans. The fix is to stop asking an address
// anything it cannot answer:
//
//  1. A request whose body does not state its length is refused. The filters
//     buffer and sign a body only when it states one, so a chunked body was
//     verified as empty and then read, unsigned, by its handler. Every client
//     states a length.
//  2. A request claiming no fingerprint, or one with no key on file, is
//     refused without its body being read - a lookup, not a buffer and an
//     ECDSA verify. These are counted per address (Strangers), and past the
//     budget they are refused before even that.
//  3. A request claiming a known device whose signature then fails is counted
//     per address *and* fingerprint (Failures). Past that budget the pair is
//     refused before its body is read. Keyed by both, so someone who knows a
//     device's fingerprint - not a secret - cannot lock that device out by
//     failing on its behalf from somewhere else.
//  4. A verified request is charged to that device's own budget for its plane.
//     Devices behind one address share nothing but the two counters above,
//     which only a failing caller spends.
//
// Over any budget is a 429 with Retry-After. The plan first said a stranger
// past its budget should be dropped the way LanGuard drops outsiders; a 429
// was kept instead, because a stranger on an allowed network is more often a
// tab that has not paired yet than an attacker, and a dropped connection reads
// to it as "the server is down".
//
// A DI singleton, unlike the static limiters this replaced: the counts belong
// to one server, and a static would be shared by every host a test run boots.
public sealed class RequestGate(TrustedPeerStore trustedPeers, NonceReplayGuard replayGuard)
{
    public enum Plane
    {
        // The sync group's manifests and reports - a few large requests per
        // session (see SyncEndpoints for how sixty was arrived at).
        Bulk,
        // Cover art, one request per tile when a batch is not available.
        Art,
        // Streams, downloads and the tickets that open them: a probe, a body
        // and a reopen per track, two tracks at once with decode-ahead.
        Media,
        // The admin surface a person drives from the settings page.
        Admin,
        // An owner's device mirroring its files: two requests a song.
        Upload,
        // /info, which a paired device polls every five seconds for each
        // address it knows the server by, and again on every network change -
        // so four a second rather than the twelve a minute one poll costs. A
        // client reads a refusal here as "unreachable", which is worse than
        // the traffic.
        Info,
    }

    public enum Outcome
    {
        Admitted,
        LengthRequired,
        TooLarge,
        // No fingerprint claimed, or none this server has a key for.
        Unknown,
        // A key is on file and this request's signature did not verify.
        BadSignature,
        Throttled,
    }

    // SignatureProblem says which part of a BadSignature failed, for the
    // refusal to name (see Problems.BadSignature).
    public readonly record struct Result(
        Outcome Outcome, string? Fingerprint, byte[] Body, SignatureCheck SignatureProblem = SignatureCheck.Valid);

    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly RateLimiter _strangers = new(max: 30, Window);
    private readonly RateLimiter _failures = new(max: 10, Window);

    // Anonymous /info is the handshake every client makes before it can sign
    // anything, so it gets more room than an unknown caller on a gated route.
    private readonly RateLimiter _anonymousInfo = new(max: 120, Window);

    private readonly Dictionary<Plane, RateLimiter> _devices = new()
    {
        [Plane.Bulk] = new(max: 60, Window),
        [Plane.Art] = new(max: 600, Window),
        [Plane.Media] = new(max: 240, Window),
        [Plane.Admin] = new(max: 120, Window),
        [Plane.Upload] = new(max: 3000, Window),
        [Plane.Info] = new(max: 240, Window),
    };

    private readonly RefusalLogThrottle _logThrottle = new();

    // The whole check for a signed route: steps 1 to 4 above, in order.
    // Returns the verified fingerprint and the buffered body on success; the
    // body has been rewound for the handler to read again.
    public async Task<Result> AdmitAsync(HttpContext http, Plane plane, long maxBody, ILogger logger)
    {
        var request = http.Request;
        var now = DateTimeOffset.UtcNow;
        var address = RateLimiter.KeyFor(http.Connection.RemoteIpAddress);

        if (HasUnstatedBody(http))
            return new Result(Outcome.LengthRequired, null, []);
        if (request.ContentLength > maxBody)
            return new Result(Outcome.TooLarge, null, []);

        var claimed = DeviceSignatureAuth.GetIdentityValue(request, "X-Flower-Fingerprint");
        if (string.IsNullOrEmpty(claimed) || trustedPeers.GetPublicKey(claimed) == null)
        {
            if (!_strangers.TryAcquire(address, now))
            {
                LogThrottled(logger, http, $"strangers|{address}", "callers it has no key for", now);
                return new Result(Outcome.Throttled, null, []);
            }

            // Logged through the same path a failed verification is, so an
            // operator reading "it will not connect" finds the reason.
            DeviceSignatureAuth.AuthenticateTrustedPeer(request, [], trustedPeers, replayGuard, logger);
            return new Result(Outcome.Unknown, null, []);
        }

        var failureKey = $"{address}|{claimed}";
        if (!_failures.WouldAllow(failureKey, now))
        {
            LogThrottled(logger, http, $"failures|{failureKey}", $"failed signatures for {claimed}", now);
            return new Result(Outcome.Throttled, null, []);
        }

        var body = await ReadBodyAsync(http);

        var auth = DeviceSignatureAuth.AuthenticateTrustedPeer(request, body, trustedPeers, replayGuard, logger);
        if (auth.Failure == PeerAuthFailure.NotTrusted)
            return new Result(Outcome.Unknown, null, []);
        if (auth.Failure != PeerAuthFailure.None)
        {
            _failures.TryAcquire(failureKey, now);
            return new Result(Outcome.BadSignature, null, [], auth.SignatureProblem);
        }

        if (!ChargeDevice(plane, auth.Fingerprint!, now))
        {
            LogThrottled(logger, http, $"{plane}|{auth.Fingerprint}", $"the {plane} budget of {auth.Fingerprint}", now);
            return new Result(Outcome.Throttled, auth.Fingerprint, []);
        }

        return new Result(Outcome.Admitted, auth.Fingerprint, body);
    }

    // For a request a stream ticket admitted: the ticket names the device that
    // minted it, and its playback is charged to that device like any other.
    public bool ChargeDevice(Plane plane, string fingerprint, DateTimeOffset now) =>
        _devices[plane].TryAcquire(fingerprint, now);

    // /info's halves, which it asks separately because it answers a stranger
    // rather than refusing one.
    public bool AdmitAnonymousInfo(HttpContext http, DateTimeOffset now) =>
        _anonymousInfo.TryAcquire(RateLimiter.KeyFor(http.Connection.RemoteIpAddress), now);

    public bool FailuresAllow(HttpContext http, string fingerprint, DateTimeOffset now) =>
        _failures.WouldAllow($"{RateLimiter.KeyFor(http.Connection.RemoteIpAddress)}|{fingerprint}", now);

    public void NoteFailure(HttpContext http, string fingerprint, DateTimeOffset now) =>
        _failures.TryAcquire($"{RateLimiter.KeyFor(http.Connection.RemoteIpAddress)}|{fingerprint}", now);

    // A body is coming and nobody said how long - chunked on HTTP/1.1, or an
    // HTTP/2 stream left open. Asked of the body-detection feature rather than
    // of Transfer-Encoding, which HTTP/2 does not carry.
    public static bool HasUnstatedBody(HttpContext http) =>
        http.Request.ContentLength is null
        && http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true;

    private static async Task<byte[]> ReadBodyAsync(HttpContext http)
    {
        if (http.Request.ContentLength is not > 0)
            return [];

        http.Request.EnableBuffering();
        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, http.RequestAborted);
        http.Request.Body.Position = 0;
        return buffer.ToArray();
    }

    // Debug, throttled: being rate limited is precisely the state that
    // repeats, and a busy phone and a stranger look the same from here. It is
    // logged so that "sync got slow" has a cause to find.
    private void LogThrottled(ILogger logger, HttpContext http, string key, string budget, DateTimeOffset now)
    {
        if (!_logThrottle.ShouldLog(key, now, out var suppressed))
            return;

        _logThrottle.Prune(now);
        logger.LogDebug(
            "Rate-limited {Method} {Path} from {RemoteAddress}: over the budget for {Budget}.{AlsoSuppressed}",
            http.Request.Method, http.Request.Path.Value, RateLimiter.KeyFor(http.Connection.RemoteIpAddress), budget,
            suppressed == 0 ? "" : $" ({suppressed} more since the last one.)");
    }
}
