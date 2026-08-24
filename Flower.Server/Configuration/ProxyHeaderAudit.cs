using System.Net;

namespace Flower.Server.Configuration;

// Catches the misconfiguration that docs/OPEN-INTERNET-REVIEW.md finding #2 is
// about: a proxy or tunnel in front of this server that TrustedProxies does not
// name.
//
// It cannot be caught at startup. `cloudflared` dials *out* and delivers over
// loopback, so nothing about the process, the bind or the config says a tunnel
// exists - and an operator who forgets TrustedProxies gets a server that looks
// like it is working. What it does say is that every client now arrives as one
// address: they share one bucket in every per-source rate limiter, and
// LanGuard's allow-list admits them all by definition, so the network-level
// backstop under authentication is gone without anything logging that it went.
//
// The one signal that is actually available is a request carrying an
// X-Forwarded-For from a hop that is not trusted to write one. That is exactly
// the shape of an undeclared proxy - and also the shape of a client writing the
// header itself, which is worth surfacing for the same reason.
//
// So: a warning, not a refusal. A refusal would have to be a 403 at request
// time, which turns "your rate limits are pooled" into "your server is down"
// and would be triggerable by any caller willing to send a header.
public sealed class ProxyHeaderAudit(IEnumerable<IPNetwork> trustedProxies)
{
    // Long enough that a caller sending the header on every request cannot
    // flood the log, short enough that an operator switching a tunnel on sees
    // it while they are still looking.
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromMinutes(5);

    private readonly List<IPNetwork> _trusted = trustedProxies.ToList();
    private readonly object _gate = new();
    private DateTimeOffset _nextWarning = DateTimeOffset.MinValue;

    // Whether this request came through a hop allowed to speak for its client.
    // A missing header is not a finding: the ordinary direct-to-Kestrel
    // deployment has no proxy and should say nothing.
    public bool IsUndeclaredHop(IPAddress? remoteAddress, bool carriesForwardedFor) =>
        carriesForwardedFor
        && (remoteAddress == null || !_trusted.Any(network => network.Contains(remoteAddress)));

    // Throttled so the log stays readable; the caller logs only when this says
    // to. Returns false for anything that is not a finding at all.
    public bool ShouldWarn(IPAddress? remoteAddress, bool carriesForwardedFor, DateTimeOffset now)
    {
        if (!IsUndeclaredHop(remoteAddress, carriesForwardedFor))
            return false;

        lock (_gate)
        {
            if (now < _nextWarning)
                return false;
            _nextWarning = now + RepeatInterval;
            return true;
        }
    }

    public bool HasTrustedProxies => _trusted.Count > 0;
}
