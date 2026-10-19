using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;

using Xunit;

namespace Flower.Tests.Sync;

// What a pull costs when it changes little or nothing. The server's catalog
// token moves on any listener's play, so every paired device pulls the whole
// catalog for one play count - and every one of those pulls used to rewrite
// every row of this device's library and rebuild every list over it.
public class LibrarySyncNoOpPullTests
{
    // Counts the writes rather than making them: which kind of write a merge
    // asked for is the whole question here.
    private sealed class RecordingTrackStore : ITrackStore
    {
        public int ReplaceAllCount { get; private set; }
        public List<Track> Upserted { get; } = [];

        public void UpdateStats(Track track) { }
        public void SetStarred(StarTarget target, string value, bool starred, DateTimeOffset? starredAt) { }
        public void Upsert(Track track) => Upserted.Add(track);
        public void UpsertMany(IReadOnlyCollection<Track> tracks) => Upserted.AddRange(tracks);
        public void ReplaceAll(IEnumerable<Track> tracks) => ReplaceAllCount++;
        public void Delete(IReadOnlyCollection<Guid> ids) { }
    }

    private static Track Song(string title, string id) => new()
    {
        Title = title, Artists = "Ichiko Aoba", Album = "Luminescent Creatures",
        Duration = TimeSpan.FromSeconds(200), Path = null,
        OriginDeviceFingerprint = "peer-1", OriginTrackId = id,
        DateAdded = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
    };

    // A library that has already pulled these two songs once, and the store
    // and events that follow everything after that first pull.
    private static (Library Library, RecordingTrackStore Store, List<string> Events) Synced(params Track[] served)
    {
        var store = new RecordingTrackStore();
        var library = new Library([], NullLogger<Library>.Instance, store);
        library.MergeSyncedTracks("peer-1", served.Select(t => t.Clone()).ToList());

        store.Upserted.Clear();
        var events = new List<string>();
        library.LibraryChanged += (_, _) => events.Add("library");
        library.TrackChanged += (_, e) => events.Add($"{e.Change}/{e.Source}:{string.Join(",", e.Tracks.Select(t => t.Title))}");
        return (library, store, events);
    }

    [Fact]
    public void A_pull_that_restates_the_library_writes_nothing_and_announces_nothing()
    {
        var a = Song("Pirsomnia", "t1");
        var b = Song("Luciférine", "t2");
        var (library, store, events) = Synced(a, b);
        var replaceAllsAfterFirstPull = store.ReplaceAllCount;
        var tracksBefore = library.Tracks;
        var tokenBefore = library.ChangeToken;

        library.MergeSyncedTracks("peer-1", [a.Clone(), b.Clone()]);

        Assert.Empty(events);
        Assert.Equal(replaceAllsAfterFirstPull, store.ReplaceAllCount);
        Assert.Empty(store.Upserted);
        Assert.Same(tracksBefore, library.Tracks);
        Assert.Equal(tokenBefore, library.ChangeToken);
    }

    // The ordinary case: someone else played one song, so its count moved.
    [Fact]
    public void A_pull_that_moves_one_play_count_writes_that_row_and_refreshes_that_row()
    {
        var a = Song("Pirsomnia", "t1");
        var b = Song("Luciférine", "t2");
        var (library, store, events) = Synced(a, b);
        var replaceAllsAfterFirstPull = store.ReplaceAllCount;

        var played = a.Clone();
        played.RemotePlayCounts["guest-phone"] = 1;
        library.MergeSyncedTracks("peer-1", [played, b.Clone()]);

        Assert.Equal(["PlayFinished/Remote:Pirsomnia"], events);
        Assert.Equal(replaceAllsAfterFirstPull, store.ReplaceAllCount);
        Assert.Equal("Pirsomnia", Assert.Single(store.Upserted).Title);
        Assert.Equal(1, library.Tracks.Single(t => t.Title == "Pirsomnia").RemotePlayCounts["guest-phone"]);
    }

    [Fact]
    public void A_pull_that_brings_a_new_song_still_reshapes_the_library()
    {
        var a = Song("Pirsomnia", "t1");
        var (library, store, events) = Synced(a);
        var replaceAllsAfterFirstPull = store.ReplaceAllCount;

        library.MergeSyncedTracks("peer-1", [a.Clone(), Song("Luciférine", "t2")]);

        Assert.Equal(["library"], events);
        Assert.Equal(replaceAllsAfterFirstPull + 1, store.ReplaceAllCount);
        Assert.Equal(2, library.Tracks.Count);
    }

    [Fact]
    public void A_pull_that_drops_a_song_still_reshapes_the_library()
    {
        var a = Song("Pirsomnia", "t1");
        var b = Song("Luciférine", "t2");
        var (library, store, events) = Synced(a, b);

        library.MergeSyncedTracks("peer-1", [a.Clone()]);

        Assert.Equal(["library"], events);
        Assert.Equal("Pirsomnia", library.Tracks.Single().Title);
    }

    // A retag on the server reaches a placeholder in place, and a retag can
    // move a song to another album - so every list rebuilds for it, without
    // the library being rewritten.
    [Fact]
    public void A_pull_that_retags_a_placeholder_announces_a_reshaping_change_on_that_song_alone()
    {
        var a = Song("Pirsomnia", "t1");
        var b = Song("Luciférine", "t2");
        var (library, store, events) = Synced(a, b);

        var retagged = a.Clone();
        retagged.Genre = "Folk";
        library.MergeSyncedTracks("peer-1", [retagged, b.Clone()]);

        Assert.Equal(["Tags/Remote:Pirsomnia"], events);
        Assert.Equal("Folk", library.Tracks.Single(t => t.Title == "Pirsomnia").Genre);
        Assert.Equal("Pirsomnia", Assert.Single(store.Upserted).Title);
    }
}
