using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Flower.Services;

// The clock a device signs with: its own, corrected by whatever the server
// last said its clock reads.
//
// A signature is good for SignatureVerifier.ClockSkewWindow either side of the
// server's time, so a phone whose clock is a few minutes out fails every
// request it makes, and until docs/TRUST-BOUNDARY-PLAN.md step 3 nothing told
// it why: the refusal was a 401 like any other. Now a stale signature is
// refused as clock-skew carrying the server's time (FlowerProblem), and
// SignatureClockHandler sets the offset from it, so the next request is signed
// in the server's time and goes through. AWS returns its own time on
// RequestTimeTooSkewed for the same reason.
//
// Process-wide, because a client pairs with one server - the topology is a
// star - and the offset is a fact about this device's clock, not about a
// request. Zero on the server, which never reads a refusal of its own.
public static class SignatureClock
{
    private static long _offsetTicks;

    public static TimeSpan Offset => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    public static DateTimeOffset UtcNow => DateTimeOffset.UtcNow + Offset;

    // receivedAt is when the answer carrying serverTime arrived, by this
    // device's own uncorrected clock.
    public static void Correct(DateTimeOffset serverTime, DateTimeOffset receivedAt) =>
        Interlocked.Exchange(ref _offsetTicks, (serverTime - receivedAt).Ticks);

    // For tests, which share the process.
    public static void Reset() => Interlocked.Exchange(ref _offsetTicks, 0);
}

// Reads a clock-skew refusal off any response from a Flower server and
// corrects SignatureClock from it. In the pipeline of every HttpClient
// PeerHttpClient builds, so no call site has to remember to. The body is
// buffered first, so the caller can still read the refusal itself.
public sealed class SignatureClockHandler : DelegatingHandler
{
    public SignatureClockHandler(HttpMessageHandler inner) => InnerHandler = inner;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        await ObserveAsync(response, cancellationToken);
        return response;
    }

    // Whether this response was a clock-skew refusal, having corrected the
    // clock if it was. Public for PeerCredentialsHandler, which signs its own
    // requests and so can send one again.
    public static async Task<bool> ObserveAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.Unauthorized
            || response.Content.Headers.ContentType?.MediaType != FlowerProblem.ContentType)
        {
            return false;
        }

        await response.Content.LoadIntoBufferAsync(cancellationToken);
        var problem = FlowerProblem.TryRead(await response.Content.ReadAsStringAsync(cancellationToken));
        if (problem is not { Code: ProblemCodes.ClockSkew, ServerTime: { } serverTime })
            return false;

        SignatureClock.Correct(serverTime, DateTimeOffset.UtcNow);
        return true;
    }
}
