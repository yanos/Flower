using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;

using Xunit;

namespace Flower.Tests.Sync;

// Plays on a token of their own (Library.ListeningToken), so a play anywhere
// costs every other device the songs that moved rather than the whole catalog
// (October C1.1). The serving half - which token a change moves, and what
// ListeningSince reports - and the pulling half, MergeSyncedListening.
public class LibraryListeningTokenTests
{
    private static Track Song(string title) => new() { Title = title, Path = $"/music/{title}.mp3" };

    [Fact]
    public void A_play_moves_the_listening_token_and_leaves_the_catalog_token_alone()
    {
        var song = Song("A");
        var library = new Library([song, Song("B")]);
        var catalog = library.ChangeToken;
        var since = library.ListeningToken;

        library.IncrementPlayCount(song);

        Assert.Equal(catalog, library.ChangeToken);
        Assert.NotEqual(since, library.ListeningToken);
        var (token, moved) = library.ListeningSince(since)!.Value;
        Assert.Equal(library.ListeningToken, token);
        Assert.Same(song, Assert.Single(moved));
    }

    [Fact]
    public void Nothing_has_moved_since_the_current_token()
    {
        var song = Song("A");
        var library = new Library([song]);
        library.IncrementPlayCount(song);

        var (token, moved) = library.ListeningSince(library.ListeningToken)!.Value;

        Assert.Equal(library.ListeningToken, token);
        Assert.Empty(moved);
    }

    // Each of these is a device that has to read the catalog to know where it
    // stands: a token from before a restart, one ahead of anything this
    // session has issued, or something that is not a token at all.
    [Fact]
    public void A_token_this_session_did_not_issue_cannot_be_counted_from()
    {
        var library = new Library([Song("A")]);
        var current = library.ListeningToken;
        var session = current[..current.LastIndexOf('-')];

        Assert.Null(library.ListeningSince(new Library([Song("A")]).ListeningToken));
        Assert.Null(library.ListeningSince($"{session}-99"));
        Assert.Null(library.ListeningSince("not-a-token"));
        Assert.Null(library.ListeningSince(null));
    }

    // A report of plays alone is nearly every report there is. A star in the
    // same report is catalog news, and moves the catalog's token too.
    [Fact]
    public void A_reported_play_moves_only_listening_and_a_reported_star_moves_the_catalog()
    {
        var song = Song("A");
        var library = new Library([song]);
        var catalog = library.ChangeToken;
        var listening = library.ListeningToken;

        library.MergeReportedTrackState("phone", [new TrackStateDto(song.Id.ToKey(), 3)], callerIsAdmin: true);

        Assert.Equal(catalog, library.ChangeToken);
        Assert.NotEqual(listening, library.ListeningToken);

        library.MergeReportedTrackState("phone", [new TrackStateDto(song.Id.ToKey(), 3, Starred: true)], callerIsAdmin: true);

        Assert.NotEqual(catalog, library.ChangeToken);
    }

    private static Track Placeholder(string id) => new()
    {
        Title = id, Path = null, OriginDeviceFingerprint = "server", OriginTrackId = id,
    };

    [Fact]
    public void A_listening_answer_reaches_the_songs_this_device_has_from_that_server()
    {
        var mine = Placeholder("t1");
        var untouched = Placeholder("t2");
        var library = new Library([mine, untouched]);
        var announced = new List<TrackChangedEventArgs>();
        library.TrackChanged += (_, e) => announced.Add(e);
        var playedAt = new DateTimeOffset(2026, 11, 7, 20, 0, 0, TimeSpan.Zero);

        var changed = library.MergeSyncedListening("server",
        [
            LibrarySyncMapper.ToListeningTrack(
                new TrackListeningDto("t1", new Dictionary<string, int> { ["server"] = 4, ["me"] = 9 }, LastPlayed: playedAt),
                "server", ownFingerprint: "me"),
            LibrarySyncMapper.ToListeningTrack(new TrackListeningDto("t-unknown", LastPlayed: playedAt), "server", "me"),
        ]);

        Assert.Equal(1, changed);
        Assert.Equal(4, mine.RemotePlayCounts["server"]);
        // This device's own count is its own, never what a server echoes.
        Assert.False(mine.RemotePlayCounts.ContainsKey("me"));
        Assert.Equal(playedAt, mine.LastPlayedAt);
        Assert.Null(untouched.LastPlayedAt);
        var change = Assert.Single(announced);
        Assert.Equal(ChangeSource.Remote, change.Source);
        Assert.Same(mine, Assert.Single(change.Tracks));
    }

    // The same rules as a whole pull: counts by max, LastPlayedAt forward only.
    // An answer that says nothing new - the same one twice, or an older one -
    // moves nothing and announces nothing.
    [Fact]
    public void A_listening_answer_never_walks_a_count_or_a_listen_back()
    {
        var later = new DateTimeOffset(2026, 11, 7, 20, 0, 0, TimeSpan.Zero);
        var mine = Placeholder("t1");
        mine.RemotePlayCounts["server"] = 6;
        mine.LastPlayedAt = later;
        var library = new Library([mine]);
        var announced = 0;
        library.TrackChanged += (_, _) => announced++;

        var changed = library.MergeSyncedListening("server",
        [
            LibrarySyncMapper.ToListeningTrack(
                new TrackListeningDto("t1", new Dictionary<string, int> { ["server"] = 4 }, LastPlayed: later.AddDays(-1)),
                "server", "me"),
        ]);

        Assert.Equal(0, changed);
        Assert.Equal(0, announced);
        Assert.Equal(6, mine.RemotePlayCounts["server"]);
        Assert.Equal(later, mine.LastPlayedAt);
    }
}
