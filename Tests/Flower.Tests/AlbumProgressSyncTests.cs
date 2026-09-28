using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests;

// Continue Playing across devices: two devices, each with its own copy of the
// server's tracks (the same server ids, different local ids), exchanging
// through a stand-in server that runs the real ledger.
[Collection("PlatformDataDirectory")]
public class AlbumProgressSyncTests : PinnedDataDirectory
{
    private static readonly Uri Server = new("https://music.example/");

    // The server's copy of an album, as a device receives it: placeholders
    // carrying the server's ids, under ids of this device's own.
    private static List<Track> ServedAlbum(string name, int songs) =>
        Enumerable.Range(1, songs).Select(i => new Track
        {
            Title = $"{name} {i}",
            Album = name,
            Artists = "Artist",
            TrackNumber = (uint)i,
            Duration = TimeSpan.FromMinutes(4),
            OriginTrackId = $"{name}-{i}",
        }).ToList();

    // The server's side: one owner shelf, merged by the ledger the real
    // endpoint uses.
    private sealed class LedgerServer(AlbumProgressLedger ledger) : HttpMessageHandler
    {
        public int Exchanges;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Exchanges);
            Assert.Equal(AlbumProgressProtocol.Path, request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var incoming = JsonSerializer.Deserialize(body, PlayReportJsonContext.Default.AlbumProgressExchangeDto)!;
            var merged = ledger.Exchange(AlbumProgressLedger.OwnerShelf, incoming.Albums);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new AlbumProgressExchangeDto(merged), PlayReportJsonContext.Default.AlbumProgressExchangeDto),
                    Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record Device(List<Track> Tracks, AlbumProgressTracker Tracker, AlbumProgressSyncService Sync);

    private static Device NewDevice(LedgerServer server, List<Track> tracks, Func<DateTimeOffset> now)
    {
        var library = new Library(tracks);
        var tracker = new AlbumProgressTracker(library, now: now);
        var sync = new AlbumProgressSyncService(
            library, tracker, new StaticPeerCredentials("X-Test", "1"), new HttpClient(server), () => Server,
            NullLogger<AlbumProgressSyncService>.Instance, runPeriodically: false);
        return new Device(tracks, tracker, sync);
    }

    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Tick() => Now = Now.AddSeconds(1);
    }

    [Fact]
    public async Task An_album_put_down_on_one_device_is_picked_up_at_the_same_place_on_another()
    {
        var clock = new Clock();
        var server = new LedgerServer(new AlbumProgressLedger(NullLogger<AlbumProgressLedger>.Instance));
        var phone = NewDevice(server, ServedAlbum("Blue", 5), clock.Tick);
        var desktop = NewDevice(server, ServedAlbum("Blue", 5), clock.Tick);

        phone.Tracker.TrackStarted(phone.Tracks[0], shuffling: false);
        phone.Tracker.TrackStarted(phone.Tracks[1], shuffling: false);
        phone.Tracker.UpdatePosition(83_000);
        phone.Tracker.Flush();
        Assert.True(await phone.Sync.ExchangeNowAsync());

        Assert.True(await desktop.Sync.ExchangeNowAsync());

        var entry = Assert.Single(desktop.Tracker.Entries);
        Assert.Equal(desktop.Tracks[1].Id, entry.TrackId);
        Assert.Equal(TimeSpan.FromSeconds(83), entry.Position);
    }

    [Fact]
    public async Task Finishing_an_album_on_one_device_takes_it_off_the_other()
    {
        var clock = new Clock();
        var server = new LedgerServer(new AlbumProgressLedger(NullLogger<AlbumProgressLedger>.Instance));
        var phone = NewDevice(server, ServedAlbum("Blue", 3), clock.Tick);
        var desktop = NewDevice(server, ServedAlbum("Blue", 3), clock.Tick);
        phone.Tracker.TrackStarted(phone.Tracks[0], shuffling: false);
        phone.Tracker.TrackStarted(phone.Tracks[1], shuffling: false);
        await phone.Sync.ExchangeNowAsync();
        await desktop.Sync.ExchangeNowAsync();
        Assert.Single(desktop.Tracker.Entries);

        desktop.Tracker.TrackStarted(desktop.Tracks[2], shuffling: false);
        desktop.Tracker.TrackFinished(desktop.Tracks[2]);
        await desktop.Sync.ExchangeNowAsync();
        await phone.Sync.ExchangeNowAsync();

        Assert.Empty(phone.Tracker.Entries);
        // And the phone, which still had an older place, did not put it back.
        await desktop.Sync.ExchangeNowAsync();
        Assert.Empty(desktop.Tracker.Entries);
    }

    // The newer place wins whichever device holds it - an old phone coming
    // back online does not rewind the desktop.
    [Fact]
    public async Task An_older_place_does_not_overwrite_a_newer_one()
    {
        var clock = new Clock();
        var server = new LedgerServer(new AlbumProgressLedger(NullLogger<AlbumProgressLedger>.Instance));
        var phone = NewDevice(server, ServedAlbum("Blue", 5), clock.Tick);
        var desktop = NewDevice(server, ServedAlbum("Blue", 5), clock.Tick);

        phone.Tracker.TrackStarted(phone.Tracks[0], shuffling: false);
        phone.Tracker.TrackStarted(phone.Tracks[1], shuffling: false);
        desktop.Tracker.TrackStarted(desktop.Tracks[2], shuffling: false);
        desktop.Tracker.TrackStarted(desktop.Tracks[3], shuffling: false);

        await desktop.Sync.ExchangeNowAsync();
        await phone.Sync.ExchangeNowAsync();

        Assert.Equal(phone.Tracks[3].Id, Assert.Single(phone.Tracker.Entries).TrackId);
    }

    // A song the server has no id for is this device's own file, not the
    // server's to hear about; an album the server names by a song this device
    // lacks stays on the server for a device that has it.
    [Fact]
    public async Task Only_what_both_sides_can_name_crosses()
    {
        var clock = new Clock();
        var server = new LedgerServer(new AlbumProgressLedger(NullLogger<AlbumProgressLedger>.Instance));
        var local = ServedAlbum("Mine", 3);
        foreach (var track in local)
            track.OriginTrackId = null;
        var desktop = NewDevice(server, local.Concat(ServedAlbum("Blue", 3)).ToList(), clock.Tick);
        var phone = NewDevice(server, ServedAlbum("Red", 3), clock.Tick);

        desktop.Tracker.TrackStarted(desktop.Tracks[0], shuffling: false);
        desktop.Tracker.TrackStarted(desktop.Tracks[1], shuffling: false);
        desktop.Tracker.TrackStarted(desktop.Tracks[3], shuffling: false);
        desktop.Tracker.TrackStarted(desktop.Tracks[4], shuffling: false);
        Assert.Equal(2, desktop.Tracker.Entries.Count);
        Assert.Single(desktop.Sync.Outgoing());

        await desktop.Sync.ExchangeNowAsync();
        await phone.Sync.ExchangeNowAsync();

        Assert.Empty(phone.Tracker.Entries);
        Assert.Equal(2, desktop.Tracker.Entries.Count);
    }

    [Fact]
    public async Task No_server_is_no_request()
    {
        var server = new LedgerServer(new AlbumProgressLedger(NullLogger<AlbumProgressLedger>.Instance));
        var library = new Library(ServedAlbum("Blue", 2));
        var tracker = new AlbumProgressTracker(library);
        using var sync = new AlbumProgressSyncService(
            library, tracker, new StaticPeerCredentials("X-Test", "1"), new HttpClient(server), () => null,
            NullLogger<AlbumProgressSyncService>.Instance, runPeriodically: false);

        Assert.False(await sync.ExchangeNowAsync());
        Assert.Equal(0, server.Exchanges);
    }

    // A sync moving the album elsewhere mid-song means this device's position
    // in its song is no longer the album's - it must not be written over the
    // place the other device chose.
    [Fact]
    public void A_place_moved_by_another_device_is_not_overwritten_by_the_song_still_playing_here()
    {
        var clock = new Clock();
        var tracks = ServedAlbum("Blue", 5);
        var tracker = new AlbumProgressTracker(new Library(tracks), now: clock.Tick);
        tracker.TrackStarted(tracks[0], shuffling: false);
        tracker.TrackStarted(tracks[1], shuffling: false);
        tracker.UpdatePosition(30_000);

        var albumId = AlbumProgressTracker.AlbumIdOf(tracks[0])!;
        tracker.MergeRemote([new AlbumProgressEntry(albumId, tracks[4].Id, TimeSpan.FromSeconds(12), clock.Now.AddMinutes(1))], []);
        tracker.UpdatePosition(45_000);
        tracker.Flush();

        var entry = Assert.Single(tracker.Entries);
        Assert.Equal(tracks[4].Id, entry.TrackId);
        Assert.Equal(TimeSpan.FromSeconds(12), entry.Position);
    }

    [Fact]
    public void The_merge_keeps_the_newest_of_each_album_and_bounds_what_the_server_keeps()
    {
        var now = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        var mine = new[] { new AlbumProgressDto("a", "1", 10, now), new AlbumProgressDto("b", "2", 0, now) };
        var theirs = new[] { new AlbumProgressDto("a", "9", 99, now.AddMinutes(-1)), new AlbumProgressDto("b", null, 0, now.AddMinutes(1)) };

        var merged = AlbumProgressMerge.Newest(mine, theirs, x => x.AlbumId, x => x.UpdatedAt);

        Assert.Equal("1", merged.Single(x => x.AlbumId == "a").TrackId);
        Assert.True(merged.Single(x => x.AlbumId == "b").IsRemoval);

        var many = Enumerable.Range(0, 50).Select(i => new AlbumProgressDto($"live{i}", "t", 0, now.AddMinutes(-i)))
            .Append(new AlbumProgressDto("ancient", null, 0, now - AlbumProgressMerge.RemovalLifetime - TimeSpan.FromDays(1)))
            .ToList();
        var pruned = AlbumProgressMerge.Prune(many, now);

        Assert.Equal(AlbumProgressMerge.MaxLiveAlbums, pruned.Count);
        Assert.DoesNotContain(pruned, x => x.AlbumId == "ancient");
    }
}
