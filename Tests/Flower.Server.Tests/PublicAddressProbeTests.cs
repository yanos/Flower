using System.Net;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Server.Services;

namespace Flower.Server.Tests;

// The one thing in this server that talks to somebody else's - see
// PublicAddressProbe. Everything here runs against a stub handler, on the same
// argument the fixture pins its data directory on: a test that reached the
// internet would pass or fail on the developer's link.
public class PublicAddressProbeTests
{
    private static PublicAddressProbe Probe(StubHandler handler) =>
        new(NullLogger<PublicAddressProbe>.Instance, handler);

    [Fact]
    public async Task Reads_the_address_out_of_a_cloudflare_trace()
    {
        var handler = new StubHandler(_ => StubHandler.Ok("fl=1\nh=www.cloudflare.com\nip=203.0.113.7\nts=1\n"));

        Assert.Equal("203.0.113.7", await Probe(handler).GetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Falls_through_to_the_second_provider_when_the_first_is_down()
    {
        var handler = new StubHandler(request =>
            request.RequestUri!.Host.Contains("cloudflare")
                ? throw new HttpRequestException("down")
                : StubHandler.Ok("198.51.100.4\n"));

        Assert.Equal("198.51.100.4", await Probe(handler).GetAsync(TestContext.Current.CancellationToken));
    }

    // A string from a third party heading for the operator's settings page.
    [Fact]
    public async Task Refuses_a_reply_that_is_not_an_address()
    {
        var handler = new StubHandler(_ => StubHandler.Ok("<html>we moved</html>"));

        Assert.Null(await Probe(handler).GetAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Asks_once_and_then_answers_from_the_cache()
    {
        var handler = new StubHandler(_ => StubHandler.Ok("ip=203.0.113.7\n"));
        var probe = Probe(handler);

        await probe.GetAsync(TestContext.Current.CancellationToken);
        await probe.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.Calls);
    }

    // The opposite: nothing to cache, so the next opening of the page tries
    // again rather than showing a blank line for the next quarter of an hour.
    [Fact]
    public async Task Retries_after_a_failure()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("no route"));
        var probe = Probe(handler);

        Assert.Null(await probe.GetAsync(TestContext.Current.CancellationToken));
        Assert.Null(await probe.GetAsync(TestContext.Current.CancellationToken));

        Assert.Equal(4, handler.Calls); // two providers, twice
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;

        public static HttpResponseMessage Ok(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(reply(request));
        }
    }
}
