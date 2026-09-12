using System.Globalization;

namespace Flower.Server.Services;

// How this server says no to a caller that has spent its budget - one answer,
// shared by every rate-limited surface (SyncEndpoints' three planes and the
// adapter's three).
//
// It lived on the OpenSubsonic adapter's endpoints, which made refusing a
// request on Flower's own surface a call into the adapter. That was backwards in
// the ordinary way and load-bearing in a specific one: the adapter was meant to
// be deletable, and a 429 is not something Flower's own routes can lose. The
// adapter has since been deleted, which is the argument having been right.
public static class RateLimitResponse
{
    // One window, rounded up. Anything shorter invites a client to spend the
    // budget it does not have yet; anything longer stalls a listener over a
    // burst that has already passed.
    public const int RetryAfterSeconds = 60;

    // A 429 with nothing else on it tells a client only that it lost; it has to
    // guess how long to wait, and the guess a decoder makes under pressure is
    // "immediately, three times". Retry-After turns the refusal into an
    // instruction - SeekableHttpStream believes it, waits, and keeps the track
    // rather than declaring it dead.
    public static IResult TooManyRequests(HttpContext context)
    {
        context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }
}
