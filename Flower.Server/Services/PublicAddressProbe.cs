using System.Net;

namespace Flower.Server.Services;

// What address the rest of the internet sees this server as.
//
// LocalAddresses can answer "which addresses does this machine hold", and that
// is what /info advertises - but a machine behind a router holds none of the
// addresses anyone outside the house would dial. The one number an operator
// needs while deciding whether to open the door, or while typing a forwarding
// rule into a router, is the one this asks a third party for, because it is the
// only way to learn it: nothing on this side of the NAT knows it.
//
// Which means an outbound request to somebody else's server, so:
//
// - It is only made when a settings page is actually being rendered, never on a
//   timer and never at startup. An operator who never opens the network tab
//   never contacts either of these hosts.
// - It fails quietly. A server with no route out, or one deliberately kept off
//   the internet, is not broken - it just has no public address to show, which
//   is exactly what the page then says.
// - The answer is cached, because a home connection's address changes on the
//   order of days and the settings page is re-read on every save.
//
// Two providers rather than one: this is a convenience readout, but a dead
// endpoint would turn it into a permanently blank line that looks like a bug in
// Flower rather than a URL that stopped existing. They are tried in order.
public sealed class PublicAddressProbe : IDisposable
{
    // Cloudflare's trace endpoint first - it is a debugging surface rather than
    // a product, so it is unmetered and has no interest in who calls it. ipify
    // is the fallback and answers with the bare address and nothing else.
    private static readonly (string Url, bool IsTrace)[] Providers =
    [
        ("https://www.cloudflare.com/cdn-cgi/trace", true),
        ("https://api.ipify.org", false),
    ];

    // Long enough that opening settings, saving, and re-reading costs one
    // lookup rather than three; short enough that an operator who has just
    // watched their address change can see the new one without a restart.
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(15);

    // Short by design. This sits in front of a settings page that is otherwise
    // instant, and an address that could not be found in three seconds is not
    // worth making someone wait for - the page renders without it.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private readonly ILogger<PublicAddressProbe> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _cached;
    private DateTimeOffset _cachedAt;

    public PublicAddressProbe(ILogger<PublicAddressProbe> logger, HttpMessageHandler? handler = null)
    {
        _logger = logger;
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout;
    }

    // Null when there is no answer: no route out, every provider refused, or the
    // reply was not an address. Never throws - a settings page that failed to
    // load because a third party was down would be a worse trade than a missing
    // line on it.
    public async Task<string?> GetAsync(CancellationToken ct = default)
    {
        if (_cached is { } fresh && DateTimeOffset.UtcNow - _cachedAt < CacheFor)
            return fresh;

        // One lookup at a time, so two admins opening the page together make one
        // outbound request between them rather than two. The second waiter
        // re-checks the cache the first just filled.
        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is { } filled && DateTimeOffset.UtcNow - _cachedAt < CacheFor)
                return filled;

            foreach (var (url, isTrace) in Providers)
            {
                var address = await AskAsync(url, isTrace, ct);
                if (address == null)
                    continue;

                _cached = address;
                _cachedAt = DateTimeOffset.UtcNow;
                return address;
            }

            // Not cached: a failure is usually "this machine has no route out
            // right now", and holding onto that for fifteen minutes would keep
            // the line blank long after the link came back.
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> AskAsync(string url, bool isTrace, CancellationToken ct)
    {
        try
        {
            var body = await _http.GetStringAsync(url, ct);
            var value = isTrace ? TraceField(body, "ip") : body.Trim();

            // Parsed rather than trusted. This is a string from somebody else's
            // server heading for the operator's settings page, and the only
            // shape it is allowed to have is an address.
            return IPAddress.TryParse(value, out var parsed) ? parsed.ToString() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Debug, not Warning: on a server that was never meant to face the
            // internet this fails every time the page is opened, and it is not
            // a fault there.
            _logger.LogDebug(ex, "Could not read this server's public address from {Provider}.", url);
            return null;
        }
    }

    // cdn-cgi/trace is a flat key=value document, one pair per line.
    private static string? TraceField(string body, string key)
    {
        foreach (var line in body.Split('\n'))
        {
            if (line.StartsWith(key + "=", StringComparison.Ordinal))
                return line[(key.Length + 1)..].Trim();
        }

        return null;
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
