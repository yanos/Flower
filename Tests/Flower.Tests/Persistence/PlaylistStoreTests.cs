using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using Flower.Controls;
using Flower.Importer;
using Flower.Models;
using Flower.Persistence;
using Flower.Persistence.Sql;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.Persistence;

// Playlists on disk: order, membership resolved against the library, entries
// whose track has gone, smart-playlist rules alongside their materialized
// contents, and the listener a playlist belongs to.
[Collection("PlatformDataDirectory")]
public class PlaylistStoreTests : IsolatedHome
{
    private static TrackRepository Repo() => new(FlowerDb.OpenDefault());
    private static TrackRepository Repo(FlowerDb db) => new(db);
    private static PlaylistRepository PlaylistRepo() => new(FlowerDb.OpenDefault());
    private static PlaylistRepository PlaylistRepo(FlowerDb db) => new(db);

    [Fact]
    public async Task PlaylistStore_round_trips_playlist_by_resolving_track_paths_against_the_library()
    {
        var trackA = new Track { Title = "A", Path = "/music/a.mp3" };
        var trackB = new Track { Title = "B", Path = "/music/b.mp3" };
        var playlist = new Playlist("Favorites", new List<Track> { trackA, trackB });

        PlaylistRepo().Save(new List<Playlist> { playlist });

        var loaded = new PlaylistStore().Load(new List<Track> { trackA, trackB });

        Assert.Single(loaded);
        Assert.Equal("Favorites", loaded[0].Name);
        Assert.Equal(new[] { "A", "B" }, loaded[0].Tracks.Select(t => t.Title));
    }

    // Whose playlist it is, on a server (see Listeners): written once with
    // the row and read back, and a later save of the same playlist cannot
    // move it to another listener.
    [Fact]
    public void A_playlists_listener_round_trips_through_the_repository()
    {
        var track = new Track { Title = "A", Path = "/music/a.mp3" };
        var guests = new Playlist(Guid.NewGuid(), "A guest's", [track], DateTimeOffset.UtcNow, listener: "5306d3aa");
        var owners = new Playlist("The owner's", [track]);

        PlaylistRepo().Save([guests, owners]);

        var loaded = new PlaylistStore().Load([track]).ToDictionary(p => p.Name);
        Assert.Equal("5306d3aa", loaded["A guest's"].Listener);
        Assert.Equal(Flower.Services.Listeners.Owner, loaded["The owner's"].Listener);
    }

    // A folder, and which folder a playlist is in: written with the row, and
    // a playlist moved back out of its folder saves as at the top level.
    [Fact]
    public void Folders_and_what_is_in_them_round_trip_through_the_repository()
    {
        var track = new Track { Title = "A", Path = "/music/a.mp3" };
        var folder = Playlist.NewFolder("Rock");
        var inside = new Playlist("Live", [track]) { ParentId = folder.Id };
        var leaving = new Playlist("Studio", [track]) { ParentId = folder.Id };

        PlaylistRepo().Save([folder, inside, leaving]);
        leaving.ParentId = null;
        PlaylistRepo().Save([folder, inside, leaving]);

        var loaded = new PlaylistStore().Load([track]).ToDictionary(p => p.Name);
        Assert.True(loaded["Rock"].IsFolder);
        Assert.False(loaded["Live"].IsFolder);
        Assert.Equal(folder.Id, loaded["Live"].ParentId);
        Assert.Null(loaded["Studio"].ParentId);
    }

    [Fact]
    public async Task PlaylistStore_Load_skips_tracks_no_longer_present_in_the_library()
    {
        var trackA    = new Track { Title = "A",    Path = "/music/a.mp3" };
        var trackGone = new Track { Title = "Gone", Path = "/music/gone.mp3" };
        var playlist  = new Playlist("Favorites", new List<Track> { trackA, trackGone });

        PlaylistRepo().Save(new List<Playlist> { playlist });

        // Simulate "Gone" having been removed from the library since the playlist was saved.
        var loaded = new PlaylistStore().Load(new List<Track> { trackA });

        Assert.Single(loaded);
        var only = Assert.Single(loaded[0].Tracks);
        Assert.Equal("A", only.Title);
    }

    [Fact]
    public void PlaylistStore_Load_returns_empty_list_when_no_file_exists()
    {
        var loaded = new PlaylistStore().Load(new List<Track>());
        Assert.Empty(loaded);
    }

    [Fact]
    public async Task PlaylistStore_round_trips_Id_and_UpdatedAt()
    {
        var playlist = new Playlist("Favorites", new List<Track>());
        var originalId = playlist.Id;
        var originalUpdatedAt = playlist.UpdatedAt;

        PlaylistRepo().Save(new List<Playlist> { playlist });
        var loaded = new PlaylistStore().Load(new List<Track>());

        var only = Assert.Single(loaded);
        Assert.Equal(originalId, only.Id);
        Assert.Equal(originalUpdatedAt, only.UpdatedAt);
    }

    [Fact]
    public async Task PlaylistStore_renaming_does_not_change_Id()
    {
        var playlist = new Playlist("Old Name", new List<Track>());
        var id = playlist.Id;
        playlist.Name = "New Name";

        PlaylistRepo().Save(new List<Playlist> { playlist });
        var loaded = new PlaylistStore().Load(new List<Track>());

        var only = Assert.Single(loaded);
        Assert.Equal(id, only.Id);
        Assert.Equal("New Name", only.Name);
    }

    // PlaylistStore used to persist entries as `t.Path` and filter out anything
    // with a null Path - so adding a synced-but-not-yet-downloaded track (see
    // SYNC-PLAN.md Phase 3) to a playlist worked until the save, then the entry
    // was gone on next launch with nothing logged.
    [Fact]
    public async Task PlaylistStore_round_trips_a_not_yet_downloaded_placeholder_track()
    {
        var placeholder = new Track { Title = "Synced", Path = null, OriginDeviceFingerprint = "peer-fp" };
        var local       = new Track { Title = "Local",  Path = "/music/local.mp3" };
        var store       = new PlaylistStore(NullLogger<PlaylistStore>.Instance);

        PlaylistRepo().Save(new[] { new Playlist("Mix", new List<Track> { local, placeholder }) });
        var loaded = store.Load(new List<Track> { local, placeholder });

        var tracks = Assert.Single(loaded).Tracks;
        Assert.Equal(2, tracks.Count);
        Assert.Same(placeholder, tracks[1]);
    }

    // Track.Id is the only thing a playlist entry stores, so an entry naming a
    // track the library no longer has is simply dropped - there's no path
    // fallback to rescue it with.
    [Fact]
    public async Task PlaylistStore_drops_entries_whose_track_is_gone_from_the_library()
    {
        var kept = new Track { Title = "A", Path = "/music/a.mp3" };
        var gone = new Track { Title = "B", Path = "/music/b.mp3" };
        var store = new PlaylistStore(NullLogger<PlaylistStore>.Instance);

        PlaylistRepo().Save(new[] { new Playlist("Mix", new List<Track> { kept, gone }) });
        var loaded = store.Load(new List<Track> { kept });

        var playlist = Assert.Single(loaded);
        Assert.Same(kept, Assert.Single(playlist.Tracks));
    }

    [Fact]
    public async Task Playlists_round_trip_with_their_order_and_resolve_against_the_library()
    {
        var db = new FlowerDb(Path.Combine(PlatformDataDirectory.Current!, "flower.db"));
        var libraryStore = new LibraryStore(NullLogger<LibraryStore>.Instance, db);
        var playlistStore = new PlaylistStore(NullLogger<PlaylistStore>.Instance, db);

        var a = new Track { Title = "A", Path = "/music/a.mp3" };
        var b = new Track { Title = "B", Path = "/music/b.mp3" };
        var c = new Track { Title = "C", Path = "/music/c.mp3" };
        Repo(db).ReplaceAll([a, b, c]);

        var playlist = new Playlist("Mix", [c, a, b]);
        PlaylistRepo(db).Save([playlist]);

        var reloaded = Assert.Single(playlistStore.Load(libraryStore.Load()));
        Assert.Equal(playlist.Id, reloaded.Id);
        Assert.Equal("Mix", reloaded.Name);
        Assert.Equal(["C", "A", "B"], reloaded.Tracks.Select(t => t.Title));
    }

    [Fact]
    public async Task A_playlist_entry_whose_track_left_the_library_is_dropped_on_load()
    {
        var db = new FlowerDb(Path.Combine(PlatformDataDirectory.Current!, "flower.db"));
        var libraryStore = new LibraryStore(NullLogger<LibraryStore>.Instance, db);
        var playlistStore = new PlaylistStore(NullLogger<PlaylistStore>.Instance, db);

        var kept = new Track { Title = "Kept", Path = "/music/kept.mp3" };
        var gone = new Track { Title = "Gone", Path = "/music/gone.mp3" };
        Repo(db).ReplaceAll([kept, gone]);
        PlaylistRepo(db).Save([new Playlist("Mix", [kept, gone])]);

        // The file was deleted and a rescan dropped it from the library, but
        // the playlist row still references it.
        Repo(db).ReplaceAll([kept]);

        var reloaded = Assert.Single(playlistStore.Load(libraryStore.Load()));
        Assert.Equal("Kept", Assert.Single(reloaded.Tracks).Title);
    }

    [Fact]
    public async Task Deleting_a_playlist_removes_its_membership_rows()
    {
        var db = new FlowerDb(Path.Combine(PlatformDataDirectory.Current!, "flower.db"));
        var libraryStore = new LibraryStore(NullLogger<LibraryStore>.Instance, db);
        var playlistStore = new PlaylistStore(NullLogger<PlaylistStore>.Instance, db);

        var track = new Track { Title = "A", Path = "/music/a.mp3" };
        Repo(db).ReplaceAll([track]);

        var kept = new Playlist("Kept", [track]);
        var deleted = new Playlist("Deleted", [track]);
        PlaylistRepo(db).Save([kept, deleted]);
        PlaylistRepo(db).Save([kept]);

        var reloaded = Assert.Single(playlistStore.Load(libraryStore.Load()));
        Assert.Equal("Kept", reloaded.Name);

        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM playlist_tracks;";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public async Task A_smart_playlists_rules_round_trip_alongside_its_materialized_contents()
    {
        var db = new FlowerDb(Path.Combine(PlatformDataDirectory.Current!, "flower.db"));
        var libraryStore = new LibraryStore(NullLogger<LibraryStore>.Instance, db);
        var playlistStore = new PlaylistStore(NullLogger<PlaylistStore>.Instance, db);

        var jazz = new Track { Title = "Blue in Green", Genre = "Jazz", Path = "/music/blue.mp3" };
        Repo(db).ReplaceAll([jazz]);

        var playlist = new Playlist("Recent Jazz", [jazz])
        {
            Rules = new SmartPlaylistRules(
                MatchMode.All,
                [
                    new SmartCondition(SmartField.Genre, SmartOperator.Is, new SmartValue.Text("Jazz")),
                    new SmartCondition(SmartField.DateAdded, SmartOperator.InTheLast, new SmartValue.Relative(30, RelativeUnit.Days)),
                ],
                new SmartLimit(25, LimitUnit.Items, LimitSelector.LeastRecentlyPlayed)),
        };
        PlaylistRepo(db).Save([playlist]);

        var reloaded = Assert.Single(playlistStore.Load(libraryStore.Load()));

        Assert.True(reloaded.IsSmart);
        Assert.Equal(playlist.Rules!.Conditions, reloaded.Rules!.Conditions);
        Assert.Equal(playlist.Rules.Limit, reloaded.Rules.Limit);
        // The materialized rows are still what a load produces, so the sidebar
        // has contents before anything re-evaluates - see Schema.V6.
        Assert.Equal(["Blue in Green"], reloaded.Tracks.Select(t => t.Title));
    }

    [Fact]
    public async Task An_ordinary_playlist_stores_no_rules_at_all()
    {
        var db = new FlowerDb(Path.Combine(PlatformDataDirectory.Current!, "flower.db"));
        var libraryStore = new LibraryStore(NullLogger<LibraryStore>.Instance, db);
        var playlistStore = new PlaylistStore(NullLogger<PlaylistStore>.Instance, db);

        var track = new Track { Title = "A", Path = "/music/a.mp3" };
        Repo(db).ReplaceAll([track]);
        PlaylistRepo(db).Save([new Playlist("Mix", [track])]);

        Assert.False(Assert.Single(playlistStore.Load(libraryStore.Load())).IsSmart);

        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM playlists WHERE rules IS NOT NULL;";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    // "Convert to ordinary playlist": drop the rules, keep whatever was last
    // materialized. Unlike created_at, rules is in the upsert's DO UPDATE for
    // exactly this - a clear that never reached the row would leave the
    // playlist recomputing itself forever.
    [Fact]
    public async Task Clearing_the_rules_converts_a_smart_playlist_back_to_an_ordinary_one()
    {
        var db = new FlowerDb(Path.Combine(PlatformDataDirectory.Current!, "flower.db"));
        var libraryStore = new LibraryStore(NullLogger<LibraryStore>.Instance, db);
        var playlistStore = new PlaylistStore(NullLogger<PlaylistStore>.Instance, db);

        var track = new Track { Title = "A", Genre = "Jazz", Path = "/music/a.mp3" };
        Repo(db).ReplaceAll([track]);

        var playlist = new Playlist("Jazz", [track])
        {
            Rules = SmartPlaylistRules.MatchAll(
                new SmartCondition(SmartField.Genre, SmartOperator.Is, new SmartValue.Text("Jazz"))),
        };
        PlaylistRepo(db).Save([playlist]);

        playlist.Rules = null;
        PlaylistRepo(db).Save([playlist]);

        var reloaded = Assert.Single(playlistStore.Load(libraryStore.Load()));
        Assert.False(reloaded.IsSmart);
        Assert.Equal(["A"], reloaded.Tracks.Select(t => t.Title));
    }

    // A rules blob can arrive from a peer or a newer build. Failing the whole
    // playlist load over one is worse than losing the rules: the playlist keeps
    // the tracks it last materialized and behaves as an ordinary one.
    [Fact]
    public async Task A_playlist_whose_rules_blob_cannot_be_read_still_loads()
    {
        var db = new FlowerDb(Path.Combine(PlatformDataDirectory.Current!, "flower.db"));
        var libraryStore = new LibraryStore(NullLogger<LibraryStore>.Instance, db);
        var playlistStore = new PlaylistStore(NullLogger<PlaylistStore>.Instance, db);

        var track = new Track { Title = "A", Path = "/music/a.mp3" };
        Repo(db).ReplaceAll([track]);
        PlaylistRepo(db).Save([new Playlist("Mix", [track])]);

        using (var connection = db.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE playlists SET rules = 'not json at all';";
            command.ExecuteNonQuery();
        }

        var reloaded = Assert.Single(playlistStore.Load(libraryStore.Load()));
        Assert.False(reloaded.IsSmart);
        Assert.Equal(["A"], reloaded.Tracks.Select(t => t.Title));
    }
}
