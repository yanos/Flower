using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests;

// The browser's answer to "what URL plays this track?" - SYNC-PLAN.md's seam 4.
//
// Unlike every other head, the browser cannot compute one: it has no signing
// key to authenticate a stream URL with and no mDNS peer to address, so it asks
// its own origin to mint a short-lived ticket bound to that one track. That
// makes this the only resolver whose answer is a network round trip, which is
// why IStreamUrlResolver returns a task at all - and why the cache below
// matters, since a resolver that went to the network on every Play would put a
// round trip in front of every auto-advance.
//
// Against a real socket (FakePeerHttpServer) rather than a fake handler: the
// point is partly what goes out on the wire - the route, the track id, and the
// credential a browser attaches to the mint request.
public class StreamTicketUrlResolverTests
{
    private static Track Placeholder(string id = "sg-1") => new()
    {
        Title = "Remote One",
        Path = null,
        OriginTrackId = id,
        OriginDeviceFingerprint = "server-fingerprint",
    };

    private static StreamTicketUrlResolver Resolver(int port, string credential = "browser-credential") =>
        new(new HttpClient(), new Uri($"http://127.0.0.1:{port}"),
            new StaticPeerCredentials("X-Test-Credential", credential), NullLogger<StreamTicketUrlResolver>.Instance);

    // What Flower.Server's StreamTicketEndpoints really answers with: the
    // assembled, origin-relative media URL alongside the raw ticket.
    private static Task ServeTicket(HttpListenerContext context, string ticket, DateTimeOffset expiresAt)
    {
        var id = context.Request.QueryString["id"];
        var body = Encoding.UTF8.GetBytes(
            $$"""
              {"ticket":"{{ticket}}","expiresAt":"{{expiresAt:o}}","url":"/rest/stream?id={{id}}&ticket={{ticket}}"}
              """);
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = body.Length;
        context.Response.OutputStream.Write(body);
        context.Response.Close();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task It_mints_a_ticket_for_the_track_and_hands_back_a_url_a_media_element_can_open()
    {
        var requests = new List<(string Path, string? Id, string? Credential)>();
        using var server = new FakePeerHttpServer(context =>
        {
            requests.Add((context.Request.Url!.AbsolutePath,
                context.Request.QueryString["id"],
                context.Request.Headers["X-Test-Credential"]));
            return ServeTicket(context, "tk-abc", DateTimeOffset.UtcNow.AddMinutes(15));
        });

        var url = await Resolver(server.Port).ResolveAsync(Placeholder());

        // Absolute, because what comes back is handed to an <audio> element and
        // not to the HttpClient that has the base address.
        Assert.Equal($"http://127.0.0.1:{server.Port}/rest/stream?id=sg-1&ticket=tk-abc", url);
        var (path, id, credential) = Assert.Single(requests);
        Assert.Equal("/api/flower/v1/stream-tickets", path);
        Assert.Equal("sg-1", id);
        // The mint request is authenticated like any other call the tab makes -
        // in a real tab, by its WebCrypto signature. What this resolver exists
        // for is the step after: an <audio> element cannot present any of that,
        // so the ticket is what it plays on.
        Assert.Equal("browser-credential", credential);
    }

    [Fact]
    public async Task A_ticket_already_in_hand_is_reused_rather_than_minted_again()
    {
        // Not just a saved request: a completed task is what lets
        // PlaylistControlViewModel.Play start on its own stack instead of
        // deferring, so replaying a track the tab has already played behaves
        // like every other head does.
        var minted = 0;
        using var server = new FakePeerHttpServer(context =>
        {
            minted++;
            return ServeTicket(context, "tk-" + minted, DateTimeOffset.UtcNow.AddMinutes(15));
        });

        var resolver = Resolver(server.Port);
        var first = await resolver.ResolveAsync(Placeholder());

        var second = resolver.ResolveAsync(Placeholder());

        Assert.True(second.IsCompleted);
        Assert.Equal(first, await second);
        Assert.Equal(1, minted);
    }

    [Fact]
    public async Task A_ticket_with_a_track_left_to_play_and_no_life_left_is_replaced()
    {
        // A media element re-presents its ticket for every range request a seek
        // makes, so one that is about to expire is worse than none: playback
        // starts and then stops being seekable partway through.
        var minted = 0;
        using var server = new FakePeerHttpServer(context =>
        {
            minted++;
            return ServeTicket(context, "tk-" + minted, DateTimeOffset.UtcNow.AddMinutes(1));
        });

        var resolver = Resolver(server.Port);
        await resolver.ResolveAsync(Placeholder());
        var second = await resolver.ResolveAsync(Placeholder());

        Assert.Equal(2, minted);
        Assert.EndsWith("ticket=tk-2", second);
    }

    [Fact]
    public async Task Two_tracks_get_two_tickets_because_a_ticket_only_opens_one()
    {
        var minted = new List<string?>();
        using var server = new FakePeerHttpServer(context =>
        {
            minted.Add(context.Request.QueryString["id"]);
            return ServeTicket(context, "tk-" + minted.Count, DateTimeOffset.UtcNow.AddMinutes(15));
        });

        var resolver = Resolver(server.Port);
        var one = await resolver.ResolveAsync(Placeholder("sg-1"));
        var two = await resolver.ResolveAsync(Placeholder("sg-2"));

        Assert.Equal(["sg-1", "sg-2"], minted);
        Assert.NotEqual(one, two);
    }

    [Fact]
    public async Task A_track_the_server_never_named_is_refused_without_asking_it()
    {
        // A local file, or anything else that did not come from a catalog, has
        // no id to mint against - see Track.OriginTrackId.
        var asked = false;
        using var server = new FakePeerHttpServer(context =>
        {
            asked = true;
            return ServeTicket(context, "tk-1", DateTimeOffset.UtcNow.AddMinutes(15));
        });

        var url = await Resolver(server.Port).ResolveAsync(new Track { Title = "Local", Path = "/music/a.flac" });

        Assert.Null(url);
        Assert.False(asked);
    }

    [Fact]
    public async Task A_server_that_refuses_yields_no_url_rather_than_throwing()
    {
        // Same contract as the peer resolver: an unplayable track is simply not
        // played. Throwing here would surface inside Play(), which has nothing
        // useful to do with it.
        using var server = new FakePeerHttpServer(context =>
        {
            context.Response.StatusCode = 401;
            context.Response.Close();
            return Task.CompletedTask;
        });

        Assert.Null(await Resolver(server.Port).ResolveAsync(Placeholder()));
    }
}
