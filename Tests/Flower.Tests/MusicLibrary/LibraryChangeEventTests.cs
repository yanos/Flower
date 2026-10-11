using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Flower.Models;
using Flower.Services;

using Xunit;

namespace Flower.Tests.MusicLibrary;

// What the library announces and how: LibraryChanged for list changes,
// TrackChanged for in-place ones (a play, a star, an edit, a report from
// another device), and the SyncKey and ChangeToken caches those changes
// invalidate.
public class LibraryChangeEventTests
{
    [Fact]
    public void UpdateTracks_raises_LibraryChanged_exactly_once()
    {
        var library = new Library(new List<Track>());
        int raised = 0;
        library.LibraryChanged += (_, _) => raised++;

        library.UpdateTracks(new List<Track> { new Track { Title = "A" } });

        Assert.Equal(1, raised);
    }

    [Fact]
    public void UpdateTracks_copies_the_list_so_later_mutating_the_source_has_no_effect()
    {
        var source = new List<Track> { new Track { Title = "A" } };
        var library = new Library(source);

        source.Add(new Track { Title = "B" });

        Assert.Single(library.Tracks);
    }

    [Fact]
    public void RemoveTracksFromOrigin_raises_LibraryChanged()
    {
        var library = new Library(new List<Track>
        {
            new Track { Title = "From The Server", Path = null, OriginDeviceFingerprint = "server-1" },
        });
        var raised = 0;
        library.LibraryChanged += (_, _) => raised++;

        library.RemoveTracksFromOrigin("server-1");

        Assert.Equal(1, raised);
    }

    [Fact]
    public void IncrementPlayCount_raises_TrackChanged_not_LibraryChanged()
    {
        var track   = new Track { Title = "A", Path = "/music/a.mp3" };
        var library = new Library(new List<Track> { track });

        var libraryChanged = 0;
        TrackChangedEventArgs? changed = null;
        library.LibraryChanged += (_, _) => libraryChanged++;
        library.TrackChanged   += (_, e) => changed = e;

        library.IncrementPlayCount(track);

        // A play count bump used to arrive the way a library change does, which
        // means a full UI rebuild and a peer library sync - twice per song change.
        Assert.Equal(0, libraryChanged);
        Assert.Same(track, Assert.Single(changed!.Tracks));
        Assert.Equal(TrackChange.PlayFinished, changed.Change);
        Assert.Equal(ChangeSource.Local, changed.Source);
    }

    [Fact]
    public void RecordPlayed_raises_TrackChanged_with_the_resolved_track()
    {
        var oldTrack = new Track { Title = "A", Path = "/music/a.mp3" };
        var library  = new Library(new List<Track> { oldTrack });

        // A rescan replaces the instance; the event must carry the one that was
        // actually mutated, not the stale reference the caller passed in.
        var rescanned = new Track { Title = "A", Path = "/music/a.mp3" };
        library.UpdateTracks(new List<Track> { rescanned });

        TrackChangedEventArgs? changed = null;
        library.TrackChanged += (_, e) => changed = e;

        library.RecordPlayed(oldTrack);

        Assert.Same(rescanned, Assert.Single(changed!.Tracks));
        Assert.Equal(TrackChange.PlayStarted, changed.Change);
        Assert.NotNull(rescanned.LastPlayedAt);
    }

    // Stars used to have an event of their own that no track list listened to.
    [Fact]
    public void Starring_a_track_raises_TrackChanged_saying_so()
    {
        var track   = new Track { Title = "A", Path = "/music/a.mp3" };
        var library = new Library(new List<Track> { track });
        var events  = new List<TrackChangedEventArgs>();
        library.TrackChanged += (_, e) => events.Add(e);

        library.SetStarred(StarTarget.Song, track.Id.ToString(), starred: true);

        var changed = Assert.Single(events);
        Assert.Same(track, Assert.Single(changed.Tracks));
        Assert.Equal(TrackChange.Starred, changed.Change);
    }

    // One action, one event, however many tracks it touched.
    [Fact]
    public void Tracks_edited_together_are_announced_once_and_not_as_a_library_change()
    {
        var a = new Track { Title = "A", Path = "/music/a.mp3" };
        var b = new Track { Title = "B", Path = "/music/b.mp3" };
        var library = new Library(new List<Track> { a, b });
        var events = new List<TrackChangedEventArgs>();
        var libraryChanged = 0;
        library.TrackChanged   += (_, e) => events.Add(e);
        library.LibraryChanged += (_, _) => libraryChanged++;

        library.NotifyTracksChanged([a, b], TrackChange.Tags);

        var changed = Assert.Single(events);
        Assert.Equal(new[] { a, b }, changed.Tracks);
        Assert.True(changed.Reshapes);
        Assert.Equal(0, libraryChanged);
    }

    // Remote is what keeps a play a paired device reported from being
    // forwarded straight back to where it came from.
    [Fact]
    public void A_report_from_another_device_is_one_remote_TrackChanged_saying_what_moved()
    {
        var a = new Track { Title = "A", Path = "/music/a.mp3" };
        var b = new Track { Title = "B", Path = "/music/b.mp3" };
        var library = new Library(new List<Track> { a, b });
        var events = new List<TrackChangedEventArgs>();
        library.TrackChanged += (_, e) => events.Add(e);

        library.MergeReportedTrackState(
            "phone",
            [new TrackStateDto(a.Id.ToKey(), 3), new TrackStateDto(b.Id.ToKey(), 1, Starred: true)],
            callerIsAdmin: true);

        var changed = Assert.Single(events);
        Assert.Equal(ChangeSource.Remote, changed.Source);
        Assert.Equal(new[] { a, b }, changed.Tracks);
        Assert.Equal(TrackChange.PlayFinished | TrackChange.Starred, changed.Change);
    }

    // Music.app's count is the library's one number for a song: raised to the
    // highest an admin device reports, never lowered, never summed across
    // devices - and not a guest's to state. See TrackDto.ImportedPlayCount.
    [Fact]
    public void A_reported_music_app_count_is_the_highest_any_admin_device_read()
    {
        var song = new Track { Title = "A", Path = "/music/a.mp3" };
        var library = new Library(new List<Track> { song });
        var id = song.Id.ToKey();

        library.MergeReportedTrackState("mac-1", [new TrackStateDto(id, 0, ImportedCount: 19)], callerIsAdmin: true);
        library.MergeReportedTrackState("mac-2", [new TrackStateDto(id, 0, ImportedCount: 18)], callerIsAdmin: true);
        library.MergeReportedTrackState("phone", [new TrackStateDto(id, 2, ImportedCount: 500)], callerIsAdmin: false);

        Assert.Equal(19, song.ImportedPlayCount);
        Assert.Equal(2, song.RemotePlayCounts["phone"]);
        Assert.Equal(21, song.TotalPlayCount);
    }

    [Fact]
    public void Recording_a_resume_position_announces_it_and_recording_the_same_one_does_not()
    {
        var track = new Track { Title = "Episode", Path = "/music/e.mp3", RememberPlaybackPosition = true };
        var library = new Library(new List<Track> { track });
        var events = new List<TrackChangedEventArgs>();
        library.TrackChanged += (_, e) => events.Add(e);
        var token = library.ChangeToken;

        library.RecordResumePosition(track, TimeSpan.FromMinutes(12));
        library.RecordResumePosition(track, TimeSpan.FromMinutes(12));

        Assert.Equal(TrackChange.ResumePosition, Assert.Single(events).Change);
        Assert.NotEqual(token, library.ChangeToken);
    }

    // A later report from the sitting the server already has moves the
    // position on without moving the listen - how a pause on a phone gets here.
    [Fact]
    public void A_report_from_the_same_listen_moves_the_resume_position_on()
    {
        var listenedAt = DateTimeOffset.UtcNow;
        var track = new Track
        {
            Title = "Episode", Path = "/music/e.mp3",
            LastPlayedAt = listenedAt, ResumePosition = TimeSpan.FromSeconds(120),
        };
        var library = new Library(new List<Track> { track });
        var events = new List<TrackChangedEventArgs>();
        library.TrackChanged += (_, e) => events.Add(e);

        var applied = library.MergeReportedTrackState(
            "phone",
            [new TrackStateDto(track.Id.ToKey(), 0, LastPlayedAt: listenedAt, ResumePositionSeconds: 754)],
            callerIsAdmin: true);

        Assert.Equal(1, applied);
        Assert.Equal(TimeSpan.FromSeconds(754), track.ResumePosition);
        Assert.Equal(listenedAt, track.LastPlayedAt);
        Assert.Equal(TrackChange.ResumePosition, Assert.Single(events).Change);
    }

    [Fact]
    public void SyncKey_is_stable_across_reads()
    {
        var track = new Track { Title = "A", Artists = "B", Album = "C", Duration = TimeSpan.FromSeconds(10) };

        Assert.Equal(track.SyncKey, track.SyncKey);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("artists")]
    [InlineData("album")]
    [InlineData("duration")]
    public void SyncKey_is_recomputed_after_an_edit_to_any_field_it_derives_from(string field)
    {
        var track = new Track { Title = "A", Artists = "B", Album = "C", Duration = TimeSpan.FromSeconds(10) };
        var before = track.SyncKey; // Populates the cache.

        switch (field)
        {
            case "title":    track.Title    = "Z"; break;
            case "artists":  track.Artists  = "Z"; break;
            case "album":    track.Album    = "Z"; break;
            case "duration": track.Duration = TimeSpan.FromSeconds(99); break;
        }

        // A tag edit in TrackInfoWindow must not leave a stale key behind - the
        // whole sync layer matches tracks across devices on this.
        Assert.NotEqual(before, track.SyncKey);
    }

    [Fact]
    public void ChangeToken_is_stable_while_nothing_mutates_the_catalog()
    {
        var library = new Library([new Track { Title = "A", Path = "/music/a.mp3" }]);

        var token = library.ChangeToken;

        Assert.Equal(token, library.ChangeToken);
        // Reading the list is not a mutation - a peer polling /info every ~5s
        // must not see the token move on its own, or it would resync forever.
        _ = library.Tracks.Count;
        Assert.Equal(token, library.ChangeToken);
    }

    [Fact]
    public void ChangeToken_moves_for_every_mutation_that_the_sync_manifest_can_see()
    {
        var track = new Track { Title = "A", Path = "/music/a.mp3" };
        var library = new Library([track]);
        var seen = new HashSet<string> { library.ChangeToken };

        library.UpdateTracks([track, new Track { Title = "B", Path = "/music/b.mp3" }]);
        Assert.True(seen.Add(library.ChangeToken), "UpdateTracks must move the token");

        library.IncrementPlayCount(track);
        Assert.True(seen.Add(library.ChangeToken), "A play count rides along in the manifest");

        library.RecordPlayed(track);
        Assert.True(seen.Add(library.ChangeToken), "LastPlayedAt rides along in the manifest");

        library.MergeSyncedTracks("peer", [new Track { Title = "C", OriginDeviceFingerprint = "peer" }]);
        Assert.True(seen.Add(library.ChangeToken), "MergeSyncedTracks must move the token");

        // A placeholder gaining a Path after a download is an in-place
        // mutation with no list replacement - the one case a naive
        // "did the list change" check would miss.
        library.NotifyTrackChanged(track, TrackChange.File);
        Assert.True(seen.Add(library.ChangeToken), "A download landing must move the token");

        library.SetStarred(StarTarget.Song, track.Id.ToString(), starred: true);
        Assert.True(seen.Add(library.ChangeToken), "A star rides along in the manifest");

        track.VolumeAdjustment = 5;
        library.NotifyTrackChanged(track, TrackChange.Options);
        Assert.True(seen.Add(library.ChangeToken), "Playback options ride along in the manifest");

        library.NotifyLibraryChanged();
        Assert.True(seen.Add(library.ChangeToken), "NotifyLibraryChanged must move the token");
    }

    [Fact]
    public void Two_libraries_never_share_a_change_token_even_with_identical_contents()
    {
        // The token is session-scoped on purpose: a bare counter would let a
        // restarted device hand a peer a token it already holds for entirely
        // different content, which reads as a false "nothing changed".
        var tracks = new List<Track> { new() { Title = "A", Path = "/music/a.mp3" } };

        Assert.NotEqual(new Library(tracks).ChangeToken, new Library(tracks).ChangeToken);
    }
}
