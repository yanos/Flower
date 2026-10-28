using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Flower.Models;
using Flower.Services;

using Xunit;

namespace Flower.Tests.Sync;

// Library.MergeSyncedTracks: a server's catalog folded into this device's
// library - placeholders inserted, refreshed and pruned, real files never
// replaced, and plays, listens, stars and playback options merged by whoever
// has the newer word.
public class LibrarySyncMergeTests
{
    // One track as this device knows it and as the server serves it back,
    // matched by SyncKey the way every pull matches them.
    private static (Library library, Track mine, Track served) SyncedPair(
        Action<Track>? mine = null, Action<Track>? served = null)
    {
        var local = new Track
        {
            Title = "Pirsomnia", Artists = "Ichiko Aoba", Album = "Luminescent Creatures",
            Duration = TimeSpan.FromSeconds(200), Path = null,
            OriginDeviceFingerprint = "peer-1", OriginTrackId = "t1",
        };
        mine?.Invoke(local);
        var incoming = new Track
        {
            Title = "Pirsomnia", Artists = "Ichiko Aoba", Album = "Luminescent Creatures",
            Duration = TimeSpan.FromSeconds(200), Path = null,
            OriginDeviceFingerprint = "peer-1", OriginTrackId = "t1",
        };
        served?.Invoke(incoming);
        return (new Library(new List<Track> { local }), local, incoming);
    }

    // The same field arriving over sync for a track this device already knows
    // about: the peer may have re-imported it and minted a new id, and the
    // stale one would 404 on the next download.
    [Fact]
    public void MergeSyncedTracks_updates_the_origin_track_id_of_a_track_already_known_locally()
    {
        var known = new Track
        {
            Title = "Song", Artists = "A", Album = "B", Duration = TimeSpan.FromSeconds(100),
            Path = null, OriginDeviceFingerprint = "peer-1", OriginTrackId = "old-id",
        };
        var library = new Library(new List<Track> { known });
        var incoming = new Track
        {
            Title = "Song", Artists = "A", Album = "B", Duration = TimeSpan.FromSeconds(100),
            Path = null, OriginDeviceFingerprint = "peer-1", OriginTrackId = "new-id",
        };

        library.MergeSyncedTracks("peer-1", new List<Track> { incoming });

        Assert.Equal("new-id", library.Tracks.Single().OriginTrackId);
    }

    // The regression: a SyncKey match short-circuited the merge, so a
    // placeholder kept whatever metadata it was first created with forever.
    // Carrying the album artist over the wire therefore appeared to do nothing
    // for an already-synced library - the field arrived and was discarded, and
    // a 31-artist compilation stayed 31 album tiles.
    [Fact]
    public void MergeSyncedTracks_refreshes_placeholder_metadata_outside_the_sync_key()
    {
        var known = new Track
        {
            Title = "Sinnerman", Artists = "Nina Simone", Album = "Lazy Sunday",
            Duration = TimeSpan.FromSeconds(100), Path = null,
            OriginDeviceFingerprint = "peer-1",
        };
        var library = new Library(new List<Track> { known });
        var incoming = new Track
        {
            Title = "Sinnerman", Artists = "Nina Simone", Album = "Lazy Sunday",
            Duration = TimeSpan.FromSeconds(100), Path = null,
            OriginDeviceFingerprint = "peer-1",
            AlbumArtists = "Various Artists", IsCompilation = true,
            Genre = "Soul", Year = "2003", TrackNumber = 4,
            Bitrate = 320, SampleRate = 44100, Channels = 2, BitsPerSample = 16,
            Codec = "MPEG Version 1 Audio, Layer 3",
            EncoderProfile = "LAME 3.100, VBR (V0)",
            ArtWidth = 1400, ArtHeight = 1200,
            ArtistsSort = "Beatles, The",
        };

        library.MergeSyncedTracks("peer-1", new List<Track> { incoming });

        var merged = library.Tracks.Single();
        Assert.Equal("Various Artists", merged.AlbumArtists);
        Assert.True(merged.IsCompilation);
        Assert.Equal("Various Artists", merged.EffectiveAlbumArtist);
        Assert.Equal("Soul", merged.Genre);
        Assert.Equal("2003", merged.Year);
        Assert.Equal(4u, merged.TrackNumber);
        // The same short-circuit hid the technical fields, and this is the path
        // that heals an already-synced library rather than a fresh pull: every
        // one of its placeholders matches by SyncKey, so a re-pull that did not
        // refresh here would leave the Technical tab empty forever.
        Assert.Equal(320, merged.Bitrate);
        Assert.Equal(44100, merged.SampleRate);
        Assert.Equal(2, merged.Channels);
        Assert.Equal(16, merged.BitsPerSample);
        Assert.Equal("MPEG Version 1 Audio, Layer 3", merged.Codec);
        Assert.Equal("LAME 3.100, VBR (V0)", merged.EncoderProfile);
        // And the picture's size, which is what a smart playlist of songs with
        // no cover reads on a device that has none of their files.
        Assert.Equal((1400, 1200), (merged.ArtWidth, merged.ArtHeight));
        // The sort tags too: a placeholder has no file to read them off, so
        // without this it would file under its display text while the origin
        // filed it somewhere else.
        Assert.Equal("Beatles, The", merged.ArtistsSort);
    }

    // The per-track playback options are not tags, so nothing on this device
    // can re-derive them: sync is the only way they ever arrive. They apply to
    // a real local file too, which is the point - the phone usually has the
    // podcast the desktop was halfway through.
    [Fact]
    public void MergeSyncedTracks_takes_the_playback_options_of_whichever_device_played_it_last()
    {
        var mine = new Track
        {
            Title = "Long Set", Artists = "DJ", Album = "Mixes",
            Duration = TimeSpan.FromSeconds(100), Path = "/music/set.mp3",
            RememberPlaybackPosition = true,
            ResumePosition = TimeSpan.FromMinutes(5),
            LastPlayedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var library = new Library(new List<Track> { mine });
        var incoming = new Track
        {
            Title = "Long Set", Artists = "DJ", Album = "Mixes",
            Duration = TimeSpan.FromSeconds(100), Path = null,
            OriginDeviceFingerprint = "peer-1",
            RememberPlaybackPosition = true,
            ResumePosition = TimeSpan.FromMinutes(40),
            IgnoreWhenShuffling = true,
            VolumeAdjustment = -15,
            LastPlayedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
        };

        library.MergeSyncedTracks("peer-1", new List<Track> { incoming });

        var merged = library.Tracks.Single();
        Assert.Equal(TimeSpan.FromMinutes(40), merged.ResumePosition);
        Assert.True(merged.IgnoreWhenShuffling);
        Assert.Equal(-15, merged.VolumeAdjustment);
    }

    // The other direction, and the reason the rule is "last played" rather than
    // "the origin always wins": listening on this device after the origin did
    // must not be undone by the next pull.
    [Fact]
    public void MergeSyncedTracks_keeps_a_resume_position_this_device_set_more_recently()
    {
        var mine = new Track
        {
            Title = "Long Set", Artists = "DJ", Album = "Mixes",
            Duration = TimeSpan.FromSeconds(100), Path = "/music/set.mp3",
            RememberPlaybackPosition = true,
            ResumePosition = TimeSpan.FromMinutes(50),
            LastPlayedAt = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var library = new Library(new List<Track> { mine });
        var incoming = new Track
        {
            Title = "Long Set", Artists = "DJ", Album = "Mixes",
            Duration = TimeSpan.FromSeconds(100), Path = null,
            OriginDeviceFingerprint = "peer-1",
            RememberPlaybackPosition = true,
            ResumePosition = TimeSpan.FromMinutes(40),
            LastPlayedAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
        };

        library.MergeSyncedTracks("peer-1", new List<Track> { incoming });

        Assert.Equal(TimeSpan.FromMinutes(50), library.Tracks.Single().ResumePosition);
    }

    // A listen moves LastPlayedAt only once it is counted, so a podcast paused
    // here ties with the server's copy until then. A pull landing before the
    // pause was reported must not put back where the server last had it.
    [Fact]
    public void MergeSyncedTracks_keeps_a_position_this_device_moved_and_has_not_reported()
    {
        var mine = new Track
        {
            Title = "Long Set", Artists = "DJ", Album = "Mixes",
            Duration = TimeSpan.FromSeconds(100), Path = "/music/set.mp3",
            OriginTrackId = "srv-1", RememberPlaybackPosition = true,
            ResumePosition = TimeSpan.FromMinutes(20),
        };
        var library = new Library(new List<Track> { mine });
        var incoming = new Track
        {
            Title = "Long Set", Artists = "DJ", Album = "Mixes",
            Duration = TimeSpan.FromSeconds(100), Path = null,
            OriginDeviceFingerprint = "peer-1", OriginTrackId = "srv-1",
            RememberPlaybackPosition = true,
            ResumePosition = TimeSpan.FromMinutes(10),
        };

        library.MergeSyncedTracks("peer-1", new List<Track> { incoming },
            resumeBaseline: new Dictionary<string, TimeSpan?> { ["srv-1"] = TimeSpan.FromMinutes(10) });

        Assert.Equal(TimeSpan.FromMinutes(20), library.Tracks.Single().ResumePosition);
    }

    // The other side of the same rule: the server moving the position - the
    // phone carried on with the episode - is taken.
    [Fact]
    public void MergeSyncedTracks_takes_a_position_the_server_moved_in_the_same_listen()
    {
        var mine = new Track
        {
            Title = "Long Set", Artists = "DJ", Album = "Mixes",
            Duration = TimeSpan.FromSeconds(100), Path = "/music/set.mp3",
            OriginTrackId = "srv-1", RememberPlaybackPosition = true,
            ResumePosition = TimeSpan.FromMinutes(10),
        };
        var library = new Library(new List<Track> { mine });
        var incoming = new Track
        {
            Title = "Long Set", Artists = "DJ", Album = "Mixes",
            Duration = TimeSpan.FromSeconds(100), Path = null,
            OriginDeviceFingerprint = "peer-1", OriginTrackId = "srv-1",
            RememberPlaybackPosition = true,
            ResumePosition = TimeSpan.FromMinutes(30),
        };

        library.MergeSyncedTracks("peer-1", new List<Track> { incoming },
            resumeBaseline: new Dictionary<string, TimeSpan?> { ["srv-1"] = TimeSpan.FromMinutes(10) });

        Assert.Equal(TimeSpan.FromMinutes(30), library.Tracks.Single().ResumePosition);
    }

    // A real local file's tags belong to the importer that read them off that
    // file. A peer's copy must not overwrite them - the next rescan would only
    // put the local values back, so the two would fight indefinitely.
    [Fact]
    public void MergeSyncedTracks_leaves_the_metadata_of_a_real_local_file_alone()
    {
        var local = new Track
        {
            Title = "Sinnerman", Artists = "Nina Simone", Album = "Lazy Sunday",
            Duration = TimeSpan.FromSeconds(100), Path = "/music/sinnerman.mp3",
            Genre = "Jazz", AlbumArtists = "Nina Simone",
        };
        var library = new Library(new List<Track> { local });
        var incoming = new Track
        {
            Title = "Sinnerman", Artists = "Nina Simone", Album = "Lazy Sunday",
            Duration = TimeSpan.FromSeconds(100), Path = null,
            OriginDeviceFingerprint = "peer-1",
            AlbumArtists = "Various Artists", IsCompilation = true, Genre = "Soul",
        };

        library.MergeSyncedTracks("peer-1", new List<Track> { incoming });

        var merged = library.Tracks.Single();
        Assert.Equal("Nina Simone", merged.AlbumArtists);
        Assert.False(merged.IsCompilation);
        Assert.Equal("Jazz", merged.Genre);
    }

    [Fact]
    public void MergeSyncedTracks_inserts_a_new_placeholder_for_a_track_not_already_known()
    {
        var library = new Library(new List<Track> { new Track { Title = "Local", Artists = "A", Album = "Al", Duration = TimeSpan.FromSeconds(100), Path = "/music/local.mp3" } });
        var remote = new Track { Title = "Remote", Artists = "B", Album = "Bl", Duration = TimeSpan.FromSeconds(200), OriginDeviceFingerprint = "peer-1" };

        library.MergeSyncedTracks("peer-1", new List<Track> { remote });

        Assert.Equal(2, library.Tracks.Count);
        var inserted = library.Tracks.Single(t => t.Title == "Remote");
        Assert.Null(inserted.Path);
        Assert.Equal("peer-1", inserted.OriginDeviceFingerprint);
    }

    [Fact]
    public void MergeSyncedTracks_updates_OriginDeviceFingerprint_for_an_existing_placeholder()
    {
        var placeholder = new Track { Title = "Remote", Artists = "B", Album = "Bl", Duration = TimeSpan.FromSeconds(200), OriginDeviceFingerprint = "old-peer" };
        var library = new Library(new List<Track> { placeholder });
        var remoteAgain = new Track { Title = "Remote", Artists = "B", Album = "Bl", Duration = TimeSpan.FromSeconds(200), OriginDeviceFingerprint = "new-peer" };

        library.MergeSyncedTracks("new-peer", new List<Track> { remoteAgain });

        Assert.Single(library.Tracks);
        Assert.Equal("new-peer", library.Tracks.Single().OriginDeviceFingerprint);
    }

    [Fact]
    public void MergeSyncedTracks_updates_OriginAlbumArtId_for_an_existing_placeholder()
    {
        var placeholder = new Track { Title = "Remote", Artists = "B", Album = "Bl", Duration = TimeSpan.FromSeconds(200), OriginDeviceFingerprint = "peer-1", OriginAlbumArtId = "al-old" };
        var library = new Library(new List<Track> { placeholder });
        var remoteAgain = new Track { Title = "Remote", Artists = "B", Album = "Bl", Duration = TimeSpan.FromSeconds(200), OriginDeviceFingerprint = "peer-1", OriginAlbumArtId = "al-new" };

        library.MergeSyncedTracks("peer-1", new List<Track> { remoteAgain });

        Assert.Equal("al-new", library.Tracks.Single().OriginAlbumArtId);
    }

    [Fact]
    public void MergeSyncedTracks_never_replaces_Path_for_a_track_already_backed_by_a_real_file_but_still_records_its_origin()
    {
        // Path itself is never overwritten - this device's own real file always
        // wins. OriginDeviceFingerprint IS still recorded even so (unlike an
        // earlier version of this behavior), so a later "delete downloaded
        // file" action can tell this copy is recoverable from that peer rather
        // than treating it as an unrecoverable delete - see
        // MobileMainViewModel.IsRecoverableDownload.
        var local = new Track { Title = "Same Song", Artists = "A", Album = "Al", Duration = TimeSpan.FromSeconds(100), Path = "/music/local.mp3" };
        var library = new Library(new List<Track> { local });
        var remote = new Track { Title = "Same Song", Artists = "A", Album = "Al", Duration = TimeSpan.FromSeconds(100), OriginDeviceFingerprint = "peer-1" };

        library.MergeSyncedTracks("peer-1", new List<Track> { remote });

        Assert.Single(library.Tracks);
        Assert.Same(local, library.Tracks.Single());
        Assert.Equal("/music/local.mp3", library.Tracks.Single().Path);
        Assert.Equal("peer-1", library.Tracks.Single().OriginDeviceFingerprint);
    }

    [Fact]
    public void MergeSyncedTracks_merges_remote_play_counts_into_a_track_already_backed_by_a_real_file()
    {
        // Unlike OriginDeviceFingerprint/Path (see the test above), play counts
        // ARE news even for a track this device already has for real - a peer
        // may have played its own copy of the same song, and that should still
        // count toward the total shown here.
        var local = new Track { Title = "Same Song", Artists = "A", Album = "Al", Duration = TimeSpan.FromSeconds(100), Path = "/music/local.mp3", PlayCount = 3 };
        var library = new Library(new List<Track> { local });
        var remote = new Track
        {
            Title = "Same Song", Artists = "A", Album = "Al", Duration = TimeSpan.FromSeconds(100),
            OriginDeviceFingerprint = "peer-1",
            RemotePlayCounts = new Dictionary<string, int> { ["peer-1"] = 6 },
        };

        library.MergeSyncedTracks("peer-1", new List<Track> { remote });

        var merged = library.Tracks.Single();
        Assert.Same(local, merged);
        Assert.Equal(3, merged.PlayCount); // Local's own count is untouched, not overwritten.
        Assert.Equal(6, merged.RemotePlayCounts["peer-1"]);
        Assert.Equal(9, merged.TotalPlayCount);
    }

    [Fact]
    public void MergeSyncedTracks_merges_remote_play_counts_into_an_existing_placeholder_by_max()
    {
        var placeholder = new Track
        {
            Title = "Remote", Artists = "B", Album = "Bl", Duration = TimeSpan.FromSeconds(200),
            OriginDeviceFingerprint = "peer-1",
            RemotePlayCounts = new Dictionary<string, int> { ["peer-1"] = 5, ["peer-2"] = 10 },
        };
        var library = new Library(new List<Track> { placeholder });
        var remoteAgain = new Track
        {
            Title = "Remote", Artists = "B", Album = "Bl", Duration = TimeSpan.FromSeconds(200),
            OriginDeviceFingerprint = "peer-1",
            // peer-1 played it twice more since the last sync (5 -> 7); peer-2's
            // count reported here is stale/lower than what's already known (10 ->
            // 8, e.g. a report relayed through a third device with older
            // information) and must not regress the existing higher value.
            RemotePlayCounts = new Dictionary<string, int> { ["peer-1"] = 7, ["peer-2"] = 8 },
        };

        library.MergeSyncedTracks("peer-1", new List<Track> { remoteAgain });

        var merged = library.Tracks.Single();
        Assert.Equal(7, merged.RemotePlayCounts["peer-1"]);
        Assert.Equal(10, merged.RemotePlayCounts["peer-2"]);
    }

    [Fact]
    public void MergeSyncedTracks_never_removes_a_track_backed_by_a_real_file()
    {
        // Doubly safe here: Path != null exempts it from pruning outright, and
        // it has no OriginDeviceFingerprint at all (never synced), so it
        // wouldn't match "peer-1" even if it were a placeholder.
        var local = new Track { Title = "Local Only", Path = "/music/local.mp3" };
        var library = new Library(new List<Track> { local });

        library.MergeSyncedTracks("peer-1", new List<Track>());

        Assert.Single(library.Tracks);
        Assert.Same(local, library.Tracks.Single());
    }

    [Fact]
    public void MergeSyncedTracks_prunes_a_placeholder_the_syncing_peer_no_longer_has()
    {
        var stale = new Track { Title = "Gone Song", Artists = "A", Album = "Al", Duration = TimeSpan.FromSeconds(100), OriginDeviceFingerprint = "peer-1" };
        var library = new Library(new List<Track> { stale });

        var removedCount = library.MergeSyncedTracks("peer-1", new List<Track>());

        Assert.Empty(library.Tracks);
        Assert.Equal(1, removedCount);
    }

    [Fact]
    public void MergeSyncedTracks_does_not_prune_a_placeholder_from_a_different_peer()
    {
        // "peer-2" is being synced with here, not "peer-1" - a placeholder
        // last known to come from some other, currently-unrelated peer (e.g.
        // left over from before a re-pair) must survive this sync untouched.
        var fromOtherPeer = new Track { Title = "Elsewhere Song", Artists = "A", Album = "Al", Duration = TimeSpan.FromSeconds(100), OriginDeviceFingerprint = "peer-1" };
        var library = new Library(new List<Track> { fromOtherPeer });

        var removedCount = library.MergeSyncedTracks("peer-2", new List<Track>());

        Assert.Single(library.Tracks);
        Assert.Equal(0, removedCount);
    }

    [Fact]
    public void MergeSyncedTracks_does_not_prune_a_real_file_the_syncing_peer_no_longer_mentions()
    {
        // The core "only prune what has no real file" rule - a downloaded (or
        // locally-imported) track is never deleted just because its origin
        // server's manifest stopped mentioning it.
        var downloaded = new Track
        {
            Title = "Downloaded Song", Artists = "A", Album = "Al", Duration = TimeSpan.FromSeconds(100),
            Path = "/music/downloaded.mp3", OriginDeviceFingerprint = "peer-1",
        };
        var library = new Library(new List<Track> { downloaded });

        var removedCount = library.MergeSyncedTracks("peer-1", new List<Track>());

        Assert.Single(library.Tracks);
        Assert.Same(downloaded, library.Tracks.Single());
        Assert.Equal(0, removedCount);
    }

    // A listen on another device reached this one as a play count and never as
    // a date: the pull merged RemotePlayCounts for a track it already had and
    // left LastPlayedAt at whatever the first pull put there. 71 tracks on a
    // real phone, 47 of them reading "never played" in History.
    [Fact]
    public void MergeSyncedTracks_takes_a_later_listen_the_server_knows_about()
    {
        var later = new DateTimeOffset(2026, 9, 14, 16, 40, 0, TimeSpan.Zero);
        var (library, _, served) = SyncedPair(
            mine: t => t.LastPlayedAt = later.AddDays(-1),
            served: t => t.LastPlayedAt = later);

        library.MergeSyncedTracks("peer-1", new List<Track> { served });

        Assert.Equal(later, library.Tracks.Single().LastPlayedAt);
    }

    [Fact]
    public void MergeSyncedTracks_takes_the_servers_listen_for_a_track_this_device_never_played()
    {
        var at = new DateTimeOffset(2026, 9, 7, 2, 8, 0, TimeSpan.Zero);
        var (library, _, served) = SyncedPair(served: t => t.LastPlayedAt = at);

        library.MergeSyncedTracks("peer-1", new List<Track> { served });

        Assert.Equal(at, library.Tracks.Single().LastPlayedAt);
    }

    // Forward only, the same high-water mark the server keeps: this device's
    // own later listen has not reached the server yet, and a pull must not
    // walk it back before the push that carries it gets there.
    [Fact]
    public void MergeSyncedTracks_keeps_a_later_listen_of_this_devices_own()
    {
        var mine = new DateTimeOffset(2026, 9, 17, 20, 0, 0, TimeSpan.Zero);
        var (library, _, served) = SyncedPair(
            mine: t => t.LastPlayedAt = mine,
            served: t => t.LastPlayedAt = mine.AddDays(-3));

        library.MergeSyncedTracks("peer-1", new List<Track> { served });

        Assert.Equal(mine, library.Tracks.Single().LastPlayedAt);
    }

    // The star the server moved since the last pull - starred from the desktop
    // or the browser. It used to be ignored here, and then the push, finding
    // this device's value different from the server's, reported it back as a
    // change and unstarred the track for everyone.
    [Fact]
    public void MergeSyncedTracks_takes_a_star_the_server_set_since_the_last_pull()
    {
        var (library, _, served) = SyncedPair(served: t => t.Starred = true);

        library.MergeSyncedTracks("peer-1", new List<Track> { served }, starBaseline: new HashSet<string>());

        var merged = library.Tracks.Single();
        Assert.True(merged.Starred);
    }

    [Fact]
    public void MergeSyncedTracks_takes_an_unstar_the_server_made_since_the_last_pull()
    {
        var (library, _, served) = SyncedPair(
            mine: t => t.Starred = true);

        library.MergeSyncedTracks("peer-1", new List<Track> { served }, starBaseline: new HashSet<string> { "t1" });

        Assert.False(library.Tracks.Single().Starred);
    }

    // The other half of the three-way merge: the server says what it said
    // last time, so the difference is this device's own and not yet reported
    // - starred offline, say. Kept, so the push that follows the pull carries
    // it up instead of the pull erasing it first.
    [Fact]
    public void MergeSyncedTracks_keeps_a_star_of_this_devices_own_the_server_has_not_heard_about()
    {
        var (library, _, served) = SyncedPair(
            mine: t => t.Starred = true);

        library.MergeSyncedTracks("peer-1", new List<Track> { served }, starBaseline: new HashSet<string>());

        Assert.True(library.Tracks.Single().Starred);
    }

    // No baseline is a first pull from this server: nothing to tell a local
    // change from a local value, and the library being mirrored wins.
    [Fact]
    public void MergeSyncedTracks_takes_the_servers_star_when_it_has_no_baseline()
    {
        var (library, _, served) = SyncedPair(served: t => t.Starred = true);

        library.MergeSyncedTracks("peer-1", new List<Track> { served });

        Assert.True(library.Tracks.Single().Starred);
    }

    [Fact]
    public void MergeSyncedTracks_refreshes_a_placeholders_disc_number()
    {
        var (library, _, served) = SyncedPair(served: t => { t.DiscNumber = 2; t.DiscCount = 3; });

        library.MergeSyncedTracks("peer-1", new List<Track> { served });

        var merged = library.Tracks.Single();
        Assert.Equal(2u, merged.DiscNumber);
        Assert.Equal(3u, merged.DiscCount);
    }
}
