using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Importer;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests;

// The awkward situations a client and its server get into, played end to end:
// a real client Library pulling and pushing through LibrarySyncService and
// PlaylistSyncService, against a SimulatedFlowerServer answering with the
// server's own mapping and merge code. Each test is one situation - something
// that changes on one side, or on both, or in the wrong order - and asserts
// what both libraries should look like once the dust settles.
//
// The unit tests beside this (LibraryTests, PlaylistSyncPlannerTests,
// TrackStateReportingTests) pin each rule on its own. What these are for is the
// seams between rules: a merge that is right on its own and wrong once the
// playlist sync runs before it, a retag that is right for the library and
// orphans a playlist entry.
[Collection("PlatformDataDirectory")]
public class SyncScenarioTests : PinnedDataDirectory
{
    private static Track ServerTrack(string title, string artist = "Artist", string album = "Album", double seconds = 200, string? path = null) => new()
    {
        Title = title,
        Artists = artist,
        Album = album,
        Duration = TimeSpan.FromSeconds(seconds),
        Path = path ?? $"/srv/music/{artist}/{album}/{title}.flac",
        DateAdded = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
    };

    private static Track LocalFile(string title, string artist = "Artist", string album = "Album", double seconds = 200) => new()
    {
        Title = title,
        Artists = artist,
        Album = album,
        Duration = TimeSpan.FromSeconds(seconds),
        Path = $"/Users/me/Music/{artist}/{album}/{title}.m4a",
    };

    // One client device: its own library and the two services that sync it,
    // and its own data directory. The stores behind the services (playlist
    // baselines, star baselines) write under the process-wide
    // PlatformDataDirectory, so two clients left sharing it would be reading
    // each other's baselines - a phone taking the tablet's record of what the
    // server once agreed to as its own, and deleting playlists on the strength
    // of it. Every entry point below points the directory at this device's
    // own first; the collection this class is in keeps that single-threaded.
    private sealed class Client : IDisposable
    {
        private readonly string _dataDirectory = System.IO.Directory.CreateTempSubdirectory("flower-sync-client").FullName;

        public DeviceSigningKey Key { get; } = TestSigningKey.Create();
        public Library Library { get; }
        public LibrarySyncService LibrarySync { get; }
        public PlaylistSyncService PlaylistSync { get; }

        public Client(IEnumerable<Track>? tracks = null)
        {
            Library = new Library((tracks ?? []).ToList());
            var identity = new DeviceIdentity { Fingerprint = Key.Fingerprint, Alias = "Phone" };
            LibrarySync = new LibrarySyncService(
                Library, identity, Key, new AppSettings(),
                new ServerStarBaselineStore(NullLogger<ServerStarBaselineStore>.Instance),
                TestLogArchive.InTempDirectory(),
                NullLogger<LibrarySyncService>.Instance,
                NullLogger<RemoteLibraryImporter>.Instance);
            PlaylistSync = new PlaylistSyncService(
                Library, identity, Key, new AppSettings(),
                new PlaylistSyncStateStore(NullLogger<PlaylistSyncStateStore>.Instance),
                new DeviceNicknameStore(NullLogger<DeviceNicknameStore>.Instance),
                NullLogger<PlaylistSyncService>.Instance);
        }

        private void Enter() => PlatformDataDirectory.Current = _dataDirectory;

        public async Task PullLibraryAsync(SimulatedFlowerServer server)
        {
            Enter();
            var result = await LibrarySync.SyncWithAsync(server.Device);
            Assert.True(result.Success, $"library sync failed: {result.Failure}");
        }

        public Task SyncPlaylistsAsync(SimulatedFlowerServer server)
        {
            Enter();
            return PlaylistSync.SyncWithAsync(server.Device, forceInitiator: true);
        }

        public Task<bool> PushTrackStateAsync(SimulatedFlowerServer server)
        {
            Enter();
            LibrarySync.MarkTrackStateChanged(Library.Tracks);
            return LibrarySync.PushTrackStateAsync(server.Device);
        }

        // What PeerSyncCoordinator does on first contact and on a force sync.
        public async Task SyncEverythingAsync(SimulatedFlowerServer server)
        {
            Enter();
            await Task.WhenAll(
                PlaylistSync.SyncWithAsync(server.Device, forceInitiator: true),
                LibrarySync.SyncWithAsync(server.Device));
        }

        // "Remove from Library" as this device would run it, paired with
        // `server` and reaching it through a resolver that always answers
        // with that server - PairedServerReachability without the discovery.
        public LibraryRemovalService RemovalAgainst(SimulatedFlowerServer server) =>
            new(Library,
                new AppSettings { PairedServerFingerprint = server.Fingerprint },
                new FixedResolver(server),
                new SignedDeviceCredentials(new DeviceIdentity { Fingerprint = Key.Fingerprint, Alias = "Phone" }, Key),
                NullLogger<LibraryRemovalService>.Instance);

        private sealed class FixedResolver(SimulatedFlowerServer server) : PeerTrackResolver
        {
            public override DiscoveredDevice? Resolve(Track track) =>
                track.OriginDeviceFingerprint == server.Fingerprint ? server.Device : null;
        }

        public Track Single(string title) => Library.Tracks.Single(t => t.Title == title);

        public Playlist Playlist(string name) => Library.Playlists.Single(p => p.Name == name);

        public void Dispose()
        {
            Key.Dispose();
            try { System.IO.Directory.Delete(_dataDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    private Client NewClient(IEnumerable<Track>? tracks = null) => Own(new Client(tracks));

    private static void AssertPlaylistEntriesAreLibraryTracks(Library library)
    {
        var ids = library.Tracks.Select(t => t.Id).ToHashSet();
        foreach (var playlist in library.Playlists)
        {
            foreach (var entry in playlist.Tracks)
                Assert.True(ids.Contains(entry.Id), $"\"{playlist.Name}\" holds \"{entry.Title}\", which is not in the library");
        }
    }

    private static string[] Titles(Playlist playlist) => playlist.Tracks.Select(t => t.Title!).ToArray();

    // ── Library: the server's catalog changes under a client ─────────────

    [Fact]
    public async Task A_first_pull_mirrors_every_server_track_as_a_placeholder_and_a_second_pull_changes_nothing()
    {
        using var server = new SimulatedFlowerServer([ServerTrack("One"), ServerTrack("Two"), ServerTrack("Three")]);
        var client = NewClient();

        await client.PullLibraryAsync(server);
        var ids = client.Library.Tracks.Select(t => t.Id).OrderBy(i => i).ToList();
        server.Library.NotifyLibraryChanged(); // a fresh token, so the second pull is not a 304
        await client.PullLibraryAsync(server);

        Assert.Equal(3, client.Library.Tracks.Count);
        Assert.All(client.Library.Tracks, t => Assert.Null(t.Path));
        Assert.Equal(ids, client.Library.Tracks.Select(t => t.Id).OrderBy(i => i));
    }

    // The server keeps a retagged file's identity (its rescan matches by path),
    // so its id does not move. Neither should anything the client has hung off
    // the placeholder: its own Id, the playlist holding it, its star.
    [Fact]
    public async Task A_title_fixed_on_the_server_updates_the_placeholder_in_place()
    {
        var song = ServerTrack("Teh Song");
        using var server = new SimulatedFlowerServer([song]);
        var client = NewClient();
        await client.PullLibraryAsync(server);
        var placeholder = client.Single("Teh Song");
        client.Library.AddPlaylist(new Playlist("Mix", [placeholder]));

        song.Title = "The Song";
        server.Library.NotifyLibraryChanged();
        await client.PullLibraryAsync(server);

        var renamed = Assert.Single(client.Library.Tracks);
        Assert.Equal("The Song", renamed.Title);
        Assert.Equal(placeholder.Id, renamed.Id);
        Assert.Equal(["The Song"], Titles(client.Playlist("Mix")));
        AssertPlaylistEntriesAreLibraryTracks(client.Library);
    }

    // The same retag, for a track this device already downloaded. The file on
    // disk still carries the old tags, and the server's new ones no longer
    // share its SyncKey - but it is the same song, and a second row for it is
    // a duplicate.
    [Fact]
    public async Task A_retag_on_the_server_does_not_duplicate_a_track_this_device_downloaded()
    {
        var song = ServerTrack("Teh Song");
        using var server = new SimulatedFlowerServer([song]);
        var client = NewClient();
        await client.PullLibraryAsync(server);
        var downloaded = client.Single("Teh Song");
        downloaded.Path = "/phone/Downloads/Teh Song.flac";
        downloaded.IsLocallyDownloaded = true;

        song.Title = "The Song";
        server.Library.NotifyLibraryChanged();
        await client.PullLibraryAsync(server);

        var only = Assert.Single(client.Library.Tracks);
        Assert.Equal(downloaded.Id, only.Id);
        Assert.Equal(song.Id.ToKey(), only.OriginTrackId);
    }

    // A client that has its own copy of a song the server also has is told
    // "the server has this" - which is what lets the phone's delete-a-download
    // warning call deleting it reversible. Once the server stops having it,
    // that has to stop being said.
    [Fact]
    public async Task A_track_the_server_deleted_stops_being_vouched_for_on_a_local_copy()
    {
        var song = ServerTrack("Shared");
        using var server = new SimulatedFlowerServer([song]);
        var client = NewClient([LocalFile("Shared")]);
        await client.PullLibraryAsync(server);
        Assert.Equal(server.Fingerprint, client.Single("Shared").OriginDeviceFingerprint);

        server.Library.UpdateTracks([]);
        await client.PullLibraryAsync(server);

        var mine = client.Single("Shared");
        Assert.NotNull(mine.Path);
        Assert.Null(mine.OriginDeviceFingerprint);
        Assert.Null(mine.OriginTrackId);
    }

    // The server's library folder briefly unavailable (a NAS unmounted, a
    // drive asleep): its catalog comes back empty, and then full again. The
    // client's playlists must come out the other side still pointing at
    // tracks it has.
    [Fact]
    public async Task A_catalog_that_empties_and_comes_back_leaves_the_clients_playlists_whole()
    {
        var a = ServerTrack("A");
        var b = ServerTrack("B");
        using var server = new SimulatedFlowerServer([a, b]);
        var client = NewClient();
        await client.PullLibraryAsync(server);
        client.Library.AddPlaylist(new Playlist("Mix", [client.Single("A"), client.Single("B")]));

        server.Library.Reset([]);
        await client.PullLibraryAsync(server);
        Assert.Empty(client.Library.Tracks);

        server.Library.Reset([a, b]);
        await client.PullLibraryAsync(server);

        Assert.Equal(["A", "B"], Titles(client.Playlist("Mix")));
        AssertPlaylistEntriesAreLibraryTracks(client.Library);
    }

    // Two files on the server that tag identically - the same album ripped
    // twice into two folders. Whatever the client makes of that, it must make
    // the same thing every time, rather than growing or flip-flopping.
    [Fact]
    public async Task Two_identically_tagged_server_files_settle_into_a_stable_client_library()
    {
        using var server = new SimulatedFlowerServer([
            ServerTrack("Twin", path: "/srv/music/rip1/Twin.flac"),
            ServerTrack("Twin", path: "/srv/music/rip2/Twin.flac"),
        ]);
        var client = NewClient();

        await client.PullLibraryAsync(server);
        var first = client.Library.Tracks.Select(t => (t.Id, t.OriginTrackId)).OrderBy(x => x.Id).ToList();
        for (var i = 0; i < 3; i++)
        {
            server.Library.NotifyLibraryChanged();
            await client.PullLibraryAsync(server);
        }

        Assert.Equal(first, client.Library.Tracks.Select(t => (t.Id, t.OriginTrackId)).OrderBy(x => x.Id));
        var serverIds = server.Library.Tracks.Select(t => t.Id.ToKey()).ToHashSet();
        Assert.All(client.Library.Tracks, t => Assert.Contains(t.OriginTrackId!, serverIds));
    }

    // macOS hands out decomposed Unicode (e + combining acute) where most
    // taggers write composed (é). Same song, same tags as far as any person
    // can see - a second row for it is a duplicate.
    [Fact]
    public async Task A_local_file_tagged_in_decomposed_unicode_matches_the_servers_composed_copy()
    {
        using var server = new SimulatedFlowerServer([ServerTrack("Café", artist: "Björk")]);
        var client = NewClient([LocalFile("Café", artist: "Björk")]);

        await client.PullLibraryAsync(server);

        var only = Assert.Single(client.Library.Tracks);
        Assert.NotNull(only.Path);
        Assert.Equal(server.Fingerprint, only.OriginDeviceFingerprint);
    }

    // Two encodes of one song, or two decoders' opinion of one file, can
    // disagree by a second. Tags that agree exactly and a length within a
    // second or two are the same song.
    [Fact]
    public async Task A_local_copy_a_second_longer_than_the_servers_is_still_the_same_song()
    {
        using var server = new SimulatedFlowerServer([ServerTrack("Long One", seconds: 171.4)]);
        var client = NewClient([LocalFile("Long One", seconds: 172.6)]);

        await client.PullLibraryAsync(server);

        var only = Assert.Single(client.Library.Tracks);
        Assert.NotNull(only.Path);
        Assert.Equal(server.Fingerprint, only.OriginDeviceFingerprint);
    }

    // The tolerance above must not swallow a genuinely different recording:
    // a live take of the same title on the same album is minutes apart.
    [Fact]
    public async Task A_same_titled_track_of_a_clearly_different_length_stays_a_separate_song()
    {
        using var server = new SimulatedFlowerServer([ServerTrack("Intro", seconds: 60)]);
        var client = NewClient([LocalFile("Intro", seconds: 245)]);

        await client.PullLibraryAsync(server);

        Assert.Equal(2, client.Library.Tracks.Count);
    }

    // The server reinstalled: same music, new identity. The client re-pairs;
    // nothing it hung off the old placeholders should be lost or doubled.
    [Fact]
    public async Task Repairing_with_a_reinstalled_server_keeps_every_placeholder_and_playlist_entry()
    {
        using var oldServer = new SimulatedFlowerServer([ServerTrack("A"), ServerTrack("B")], fingerprint: "old-fp");
        var client = NewClient();
        await client.PullLibraryAsync(oldServer);
        client.Library.AddPlaylist(new Playlist("Mix", [client.Single("A"), client.Single("B")]));
        client.Library.SetStarred(StarTarget.Song, client.Single("A").Id.ToString(), true);
        var ids = client.Library.Tracks.Select(t => t.Id).OrderBy(i => i).ToList();

        using var newServer = new SimulatedFlowerServer([ServerTrack("A"), ServerTrack("B")], fingerprint: "new-fp");
        await client.PullLibraryAsync(newServer);

        Assert.Equal(ids, client.Library.Tracks.Select(t => t.Id).OrderBy(i => i));
        Assert.All(client.Library.Tracks, t => Assert.Equal("new-fp", t.OriginDeviceFingerprint));
        Assert.Equal(["A", "B"], Titles(client.Playlist("Mix")));
        AssertPlaylistEntriesAreLibraryTracks(client.Library);
    }

    // Unpairing drops the placeholders (RemoveTracksFromOrigin). Pairing
    // again brings them back - and the playlists that held them should find
    // them again rather than holding rows the library no longer has.
    [Fact]
    public async Task Unpairing_and_pairing_again_reconnects_playlist_entries_to_the_returning_placeholders()
    {
        using var server = new SimulatedFlowerServer([ServerTrack("A"), ServerTrack("B")]);
        var client = NewClient();
        await client.PullLibraryAsync(server);
        client.Library.AddPlaylist(new Playlist("Mix", [client.Single("A"), client.Single("B")]));

        client.Library.RemoveTracksFromOrigin(server.Fingerprint);
        using var again = new SimulatedFlowerServer(server.Library.Tracks, fingerprint: server.Fingerprint);
        await client.PullLibraryAsync(again);

        Assert.Equal(["A", "B"], Titles(client.Playlist("Mix")));
        AssertPlaylistEntriesAreLibraryTracks(client.Library);
    }

    // A placeholder played on the phone, then the server retags the file: the
    // phone's plays are still of that song, and still reported under its id.
    [Fact]
    public async Task Plays_made_on_a_placeholder_still_reach_the_server_after_the_server_retagged_it()
    {
        var song = ServerTrack("Teh Song");
        using var server = new SimulatedFlowerServer([song]);
        var client = NewClient();
        await client.PullLibraryAsync(server);
        client.Library.IncrementPlayCount(client.Single("Teh Song"));
        client.Library.IncrementPlayCount(client.Single("Teh Song"));

        song.Title = "The Song";
        server.Library.NotifyLibraryChanged();
        await client.PullLibraryAsync(server);

        Assert.Equal(2, song.RemotePlayCounts.GetValueOrDefault(client.Key.Fingerprint));
        Assert.Equal(2, Assert.Single(client.Library.Tracks).PlayCount);
    }

    // A listener - paired, not an admin - stars a song. The server drops the
    // star (it is the owner's library), and the next pull must not then treat
    // the server's "not starred" as news and take the listener's star away.
    [Fact]
    public async Task A_listeners_own_star_survives_the_server_ignoring_it()
    {
        using var server = new SimulatedFlowerServer([ServerTrack("Fave")]) { CallerIsAdmin = false };
        var client = NewClient();
        await client.PullLibraryAsync(server);
        client.Library.SetStarred(StarTarget.Song, client.Single("Fave").Id.ToString(), true);
        await client.PushTrackStateAsync(server);

        server.Library.NotifyLibraryChanged();
        await client.PullLibraryAsync(server);

        Assert.False(server.Library.Tracks.Single().Starred);
        Assert.True(client.Single("Fave").Starred);
    }

    // Two admin devices: the phone stars a song and pushes; the desktop, which
    // pulled before that, unstars nothing - it must take the phone's star on
    // its next pull rather than reporting its own stale "unstarred" over it.
    [Fact]
    public async Task A_star_from_one_admin_device_reaches_another_without_being_walked_back()
    {
        using var server = new SimulatedFlowerServer([ServerTrack("Fave")]);
        var phone = NewClient();
        var desktop = NewClient();
        await phone.PullLibraryAsync(server);
        await desktop.PullLibraryAsync(server);

        phone.Library.SetStarred(StarTarget.Song, phone.Single("Fave").Id.ToString(), true);
        await phone.PushTrackStateAsync(server);
        await desktop.PushTrackStateAsync(server); // the desktop's own tick, before its pull
        await desktop.PullLibraryAsync(server);
        await desktop.PushTrackStateAsync(server);

        Assert.True(server.Library.Tracks.Single().Starred);
        Assert.True(desktop.Single("Fave").Starred);
    }

    // ── Playlists: the ordering and the lossy copies ─────────────────────

    // A brand-new phone pairs. The coordinator starts the playlist sync and the
    // library pull together, so the playlist sync can easily run against an
    // empty library, resolve none of the server's playlist entries, and push
    // that emptiness back as this device's answer. The server's playlists
    // must survive a client that simply has not caught up yet.
    [Fact]
    public async Task A_new_client_syncing_playlists_before_its_library_does_not_empty_the_servers_playlists()
    {
        var a = ServerTrack("A");
        var b = ServerTrack("B");
        using var server = new SimulatedFlowerServer([a, b]);
        server.Library.AddPlaylist(new Playlist("Road Trip", [a, b]));
        var client = NewClient();

        await client.SyncPlaylistsAsync(server);

        Assert.Equal(["A", "B"], Titles(server.Library.Playlists.Single()));
    }

    // And once that client's library has caught up, its own copy of the
    // playlist catches up with it, rather than staying the empty version
    // it first resolved.
    [Fact]
    public async Task A_playlist_first_seen_before_the_library_fills_in_once_the_library_arrives()
    {
        var a = ServerTrack("A");
        var b = ServerTrack("B");
        using var server = new SimulatedFlowerServer([a, b]);
        server.Library.AddPlaylist(new Playlist("Road Trip", [a, b]));
        var client = NewClient();

        await client.SyncPlaylistsAsync(server);
        await client.PullLibraryAsync(server);
        await client.SyncPlaylistsAsync(server);

        Assert.Equal(["A", "B"], Titles(client.Playlist("Road Trip")));
        Assert.Equal(["A", "B"], Titles(server.Library.Playlists.Single()));
        AssertPlaylistEntriesAreLibraryTracks(client.Library);
    }

    // The same thing, the way the coordinator really does it: both at once.
    [Fact]
    public async Task First_contact_syncing_everything_at_once_converges_both_sides()
    {
        var a = ServerTrack("A");
        var b = ServerTrack("B");
        using var server = new SimulatedFlowerServer([a, b]);
        server.Library.AddPlaylist(new Playlist("Road Trip", [a, b]));
        var client = NewClient();

        await client.SyncEverythingAsync(server);
        await client.SyncEverythingAsync(server);

        Assert.Equal(["A", "B"], Titles(client.Playlist("Road Trip")));
        Assert.Equal(["A", "B"], Titles(server.Library.Playlists.Single()));
    }

    // A playlist on the phone holding a song only the phone has (imported
    // there, never uploaded). The server cannot hold that entry; the phone
    // must not lose it for the server's shorter copy.
    [Fact]
    public async Task A_playlist_entry_only_the_client_has_survives_every_round_trip_on_the_client()
    {
        var shared = ServerTrack("Shared");
        using var server = new SimulatedFlowerServer([shared]);
        var client = NewClient([LocalFile("Mine Only", artist: "Me")]);
        await client.PullLibraryAsync(server);
        client.Library.AddPlaylist(new Playlist("Mix", [client.Single("Shared"), client.Single("Mine Only")]));

        for (var i = 0; i < 3; i++)
            await client.SyncPlaylistsAsync(server);

        Assert.Equal(["Shared", "Mine Only"], Titles(client.Playlist("Mix")));
        Assert.Equal(["Shared"], Titles(server.Library.Playlists.Single()));
    }

    [Fact]
    public async Task The_same_song_twice_in_a_playlist_stays_twice_on_both_sides()
    {
        var a = ServerTrack("A");
        var b = ServerTrack("B");
        using var server = new SimulatedFlowerServer([a, b]);
        server.Library.AddPlaylist(new Playlist("Loop", [a, b, a]));
        var client = NewClient();
        await client.PullLibraryAsync(server);

        await client.SyncPlaylistsAsync(server);
        await client.SyncPlaylistsAsync(server);

        Assert.Equal(["A", "B", "A"], Titles(client.Playlist("Loop")));
        Assert.Equal(["A", "B", "A"], Titles(server.Library.Playlists.Single()));
    }

    // A file the server has with no title tag. The catalog names it after its
    // file, the playlist sync by its (empty) tag - two different keys for one
    // song, unless both agree on which one they mean.
    [Fact]
    public async Task An_untitled_server_track_in_a_playlist_reaches_the_clients_copy()
    {
        var untitled = ServerTrack("x", path: "/srv/music/Track 07.flac");
        untitled.Title = null;
        var titled = ServerTrack("Named");
        using var server = new SimulatedFlowerServer([untitled, titled]);
        server.Library.AddPlaylist(new Playlist("Odds", [titled, untitled]));
        var client = NewClient();
        await client.PullLibraryAsync(server);

        await client.SyncPlaylistsAsync(server);

        Assert.Equal(2, client.Playlist("Odds").Tracks.Count);
        Assert.Equal(2, server.Library.Playlists.Single().Tracks.Count);
    }

    // A playlist the server got from another client after this one read the
    // server's set and before it wrote its own back. The push replaces the
    // server's collection wholesale, so a push built from a stale read
    // deletes a playlist this client never knew existed.
    [Fact]
    public async Task A_playlist_created_elsewhere_mid_sync_is_not_deleted_by_a_stale_push()
    {
        var a = ServerTrack("A");
        using var server = new SimulatedFlowerServer([a]);
        var client = NewClient();
        await client.PullLibraryAsync(server);
        client.Library.AddPlaylist(new Playlist("Phone Mix", [client.Single("A")]));

        server.AfterNextPlaylistsGet = () => server.Library.AddPlaylist(new Playlist("Desktop Mix", [a]));
        await client.SyncPlaylistsAsync(server);

        Assert.Contains(server.Library.Playlists, p => p.Name == "Desktop Mix");
        Assert.Contains(server.Library.Playlists, p => p.Name == "Phone Mix");
    }

    // A device whose clock runs behind the server's. It adopts the server's
    // copy (stamped in server time), then edits it: the edit is stamped
    // earlier than the version it edited, and a planner that compares
    // timestamps reads "nothing changed here" - and the next time the server
    // changes too, the edit is silently replaced.
    [Fact]
    public async Task An_edit_on_a_device_whose_clock_is_behind_still_counts_as_an_edit()
    {
        var a = ServerTrack("A");
        var b = ServerTrack("B");
        using var server = new SimulatedFlowerServer([a, b]);
        var serverClockAhead = DateTimeOffset.UtcNow.AddHours(2);
        server.Library.AddPlaylist(new Playlist(Guid.NewGuid(), "Mix", [a], serverClockAhead));
        var client = NewClient();
        await client.PullLibraryAsync(server);
        await client.SyncPlaylistsAsync(server);

        client.Playlist("Mix").AppendTrack(client.Single("B"));

        Assert.True(client.Playlist("Mix").UpdatedAt > serverClockAhead);
    }

    // Deleted on the phone while the desktop (through the server) renamed it.
    // The rename is an edit made since they last agreed; it wins over the
    // delete without anyone being asked, and both sides end up with it.
    [Fact]
    public async Task A_playlist_deleted_here_but_renamed_on_the_server_comes_back_renamed_on_both_sides()
    {
        var a = ServerTrack("A");
        using var server = new SimulatedFlowerServer([a]);
        server.Library.AddPlaylist(new Playlist("Old Name", [a]));
        var client = NewClient();
        await client.PullLibraryAsync(server);
        await client.SyncPlaylistsAsync(server);

        client.Library.RemovePlaylist(client.Playlist("Old Name"));
        await Task.Delay(5);
        server.Library.Playlists.Single().Name = "New Name";
        await client.SyncPlaylistsAsync(server);

        Assert.Equal("New Name", client.Library.Playlists.Single().Name);
        Assert.Equal("New Name", server.Library.Playlists.Single().Name);
    }

    // Deleted on the phone and untouched on the server: the delete goes up.
    [Fact]
    public async Task A_playlist_deleted_here_and_untouched_on_the_server_is_deleted_there_too()
    {
        var a = ServerTrack("A");
        using var server = new SimulatedFlowerServer([a]);
        server.Library.AddPlaylist(new Playlist("Doomed", [a]));
        var client = NewClient();
        await client.PullLibraryAsync(server);
        await client.SyncPlaylistsAsync(server);

        client.Library.RemovePlaylist(client.Playlist("Doomed"));
        await client.SyncPlaylistsAsync(server);

        Assert.Empty(server.Library.Playlists);
        Assert.Empty(client.Library.Playlists);
    }

    // Two phones, one server. Each edits a different playlist; neither edit
    // may cost the other.
    [Fact]
    public async Task Two_clients_editing_different_playlists_both_land()
    {
        var a = ServerTrack("A");
        var b = ServerTrack("B");
        using var server = new SimulatedFlowerServer([a, b]);
        server.Library.AddPlaylist(new Playlist("One", [a]));
        server.Library.AddPlaylist(new Playlist("Two", [a]));
        var phone = NewClient();
        var tablet = NewClient();
        foreach (var c in new[] { phone, tablet })
        {
            await c.PullLibraryAsync(server);
            await c.SyncPlaylistsAsync(server);
        }

        await Task.Delay(5);
        phone.Playlist("One").AppendTrack(phone.Single("B"));
        tablet.Playlist("Two").AppendTrack(tablet.Single("B"));
        await phone.SyncPlaylistsAsync(server);
        await tablet.SyncPlaylistsAsync(server);
        await phone.SyncPlaylistsAsync(server);

        Assert.Equal(["A", "B"], Titles(server.Library.Playlists.Single(p => p.Name == "One")));
        Assert.Equal(["A", "B"], Titles(server.Library.Playlists.Single(p => p.Name == "Two")));
        Assert.Equal(["A", "B"], Titles(phone.Playlist("Two")));
        Assert.Equal(["A", "B"], Titles(tablet.Playlist("One")));
    }

    // A playlist the server holds a song in that it then deletes from disk.
    // The server's own playlist keeps the entry (Library.RebindPlaylistTracks:
    // a scan not finding a file is not proof it is gone), and the client's
    // copy agrees with it rather than drifting into a version of its own.
    [Fact]
    public async Task A_song_deleted_on_the_server_leaves_the_clients_playlist_agreeing_with_the_servers()
    {
        var a = ServerTrack("A");
        var b = ServerTrack("B");
        using var server = new SimulatedFlowerServer([a, b]);
        server.Library.AddPlaylist(new Playlist("Mix", [a, b]));
        var client = NewClient();
        await client.PullLibraryAsync(server);
        await client.SyncPlaylistsAsync(server);

        server.Library.UpdateTracks([ServerTrack("A")]);
        await client.PullLibraryAsync(server);
        await client.SyncPlaylistsAsync(server);

        Assert.DoesNotContain(client.Library.Tracks, t => t.Title == "B");
        Assert.Equal(Titles(server.Library.Playlists.Single()), Titles(client.Playlist("Mix")));
        Assert.Contains(client.Library.Tracks, t => t.Id == client.Playlist("Mix").Tracks[0].Id);
    }

    // The lossy copy from the first-contact test above, pushed alongside a
    // genuine edit to a different playlist - so the set as a whole has
    // changed and cannot be waved through as "nothing new".
    [Fact]
    public async Task A_lossy_copy_pushed_alongside_a_real_edit_does_not_cost_the_server_its_entries()
    {
        var a = ServerTrack("A");
        var b = ServerTrack("B");
        using var server = new SimulatedFlowerServer([a, b]);
        server.Library.AddPlaylist(new Playlist("Road Trip", [a, b]));
        var client = NewClient();
        await client.SyncPlaylistsAsync(server); // before any catalog: resolves nothing
        client.Library.AddPlaylist(new Playlist("Phone Mix", []));

        await client.SyncPlaylistsAsync(server);

        Assert.Equal(["A", "B"], Titles(server.Library.Playlists.Single(p => p.Name == "Road Trip")));
        Assert.Contains(server.Library.Playlists, p => p.Name == "Phone Mix");
    }

    // Both sides edited the same playlist; the user keeps this device's
    // version. That choice has to hold on the server too, even though this
    // device's edit is the older of the two on the clock.
    [Fact]
    public async Task Keeping_this_devices_side_of_a_conflict_sticks_on_the_server_even_when_it_is_older()
    {
        var a = ServerTrack("A");
        var b = ServerTrack("B");
        using var server = new SimulatedFlowerServer([a, b]);
        server.Library.AddPlaylist(new Playlist("Mix", [a]));
        var client = NewClient();
        await client.PullLibraryAsync(server);
        await client.SyncPlaylistsAsync(server);

        client.Playlist("Mix").AppendTrack(client.Single("B")); // phone edits first
        await Task.Delay(5);
        server.Library.Playlists.Single().Name = "Renamed";      // then the desktop
        client.PlaylistSync.ConflictDetected += (_, e) => e.Resolution.SetResult(PlaylistConflictChoice.KeepLocal);
        await client.SyncPlaylistsAsync(server);

        Assert.Equal("Mix", server.Library.Playlists.Single().Name);
        Assert.Equal(["A", "B"], Titles(server.Library.Playlists.Single()));
    }

    // A browser tab deletes a playlist: the server has to hear that as a
    // delete now that a push leaving a playlist out no longer means one.
    [Fact]
    public async Task A_playlist_deleted_in_a_browser_tab_is_deleted_on_the_server()
    {
        var a = ServerTrack("A");
        using var server = new SimulatedFlowerServer([a]);
        server.Library.AddPlaylist(new Playlist("Keep", [a]));
        server.Library.AddPlaylist(new Playlist("Doomed", [a]));
        using var key = TestSigningKey.Create();
        using var http = new System.Net.Http.HttpClient();
        var writer = new OriginPlaylistWriter(http, server.Device.Origin, new SignedDeviceCredentials(
            new DeviceIdentity { Fingerprint = key.Fingerprint, Alias = "Tab" }, key),
            NullLogger<OriginPlaylistWriter>.Instance);
        writer.NoteOriginState(server.Library.Playlists);

        writer.Schedule(server.Library.Playlists.Where(p => p.Name == "Keep").ToList());
        await writer.InFlight;

        Assert.Equal(["Keep"], server.Library.Playlists.Select(p => p.Name));
    }

    // ── Remove from Library ──────────────────────────────────────────────

    // The owner's phone removes a song the server serves. It goes from the
    // server, from the phone, and - through the next pull and playlist sync -
    // from every other device's library and playlists, and stays gone.
    [Fact]
    public async Task An_admin_removing_a_server_song_removes_it_everywhere()
    {
        var keep = ServerTrack("Keep");
        var drop = ServerTrack("Drop");
        using var server = new SimulatedFlowerServer([keep, drop]);
        server.Library.AddPlaylist(new Playlist("Mix", [keep, drop]));
        var phone = NewClient();
        var tablet = NewClient();
        foreach (var c in new[] { phone, tablet })
        {
            await c.PullLibraryAsync(server);
            await c.SyncPlaylistsAsync(server);
        }

        var outcome = await phone.RemovalAgainst(server).RemoveAsync([phone.Single("Drop")], deleteFiles: false);
        await phone.PullLibraryAsync(server);
        await tablet.PullLibraryAsync(server);
        await tablet.SyncPlaylistsAsync(server);

        Assert.Null(outcome.Error);
        Assert.DoesNotContain(server.Library.Tracks, t => t.Title == "Drop");
        Assert.DoesNotContain(phone.Library.Tracks, t => t.Title == "Drop");
        Assert.DoesNotContain(tablet.Library.Tracks, t => t.Title == "Drop");
        Assert.Equal(["Keep"], Titles(server.Library.Playlists.Single()));
        Assert.Equal(["Keep"], Titles(phone.Playlist("Mix")));
        Assert.Equal(["Keep"], Titles(tablet.Playlist("Mix")));
    }

    // With "also delete the files" ticked, the server deletes its file and the
    // phone its downloaded copy.
    [Fact]
    public async Task An_admin_removing_with_the_files_deletes_the_servers_file_and_the_local_copy()
    {
        var dir = System.IO.Path.Combine(DataDirectory, "files");
        System.IO.Directory.CreateDirectory(dir);
        var serverFile = System.IO.Path.Combine(dir, "server.flac");
        var phoneFile = System.IO.Path.Combine(dir, "phone.flac");
        System.IO.File.WriteAllBytes(serverFile, [1]);
        System.IO.File.WriteAllBytes(phoneFile, [1]);
        using var server = new SimulatedFlowerServer([ServerTrack("Drop", path: serverFile)]);
        var phone = NewClient();
        await phone.PullLibraryAsync(server);
        phone.Single("Drop").Path = phoneFile;
        phone.Single("Drop").IsLocallyDownloaded = true;

        var outcome = await phone.RemovalAgainst(server).RemoveAsync([phone.Single("Drop")], deleteFiles: true);

        Assert.Null(outcome.Error);
        Assert.False(System.IO.File.Exists(serverFile));
        Assert.False(System.IO.File.Exists(phoneFile));
        Assert.Empty(phone.Library.Tracks);
    }

    // A listener's phone: the server's songs are not its to remove, so it is
    // not offered them, and asking anyway changes nothing anywhere. Its own
    // files are another matter.
    [Fact]
    public async Task A_listener_can_remove_its_own_files_but_not_the_servers_songs()
    {
        using var server = new SimulatedFlowerServer([ServerTrack("Theirs")]) { CallerIsAdmin = false };
        var phone = NewClient([LocalFile("Mine", artist: "Me")]);
        await phone.PullLibraryAsync(server);
        var removal = phone.RemovalAgainst(server);

        Assert.False(removal.CanRemove(phone.Single("Theirs")));
        Assert.True(removal.CanRemove(phone.Single("Mine")));

        var outcome = await removal.RemoveAsync([phone.Single("Theirs"), phone.Single("Mine")], deleteFiles: false);

        Assert.Equal(1, outcome.Removed);
        Assert.Contains(server.Library.Tracks, t => t.Title == "Theirs");
        Assert.Equal(["Theirs"], phone.Library.Tracks.Select(t => t.Title));
    }

    // The server does not answer. Removing only the phone's half would look
    // like it worked until the next pull put the song back - so nothing is
    // removed, and the phone says why.
    [Fact]
    public async Task Removing_a_server_song_while_the_server_is_unreachable_removes_nothing()
    {
        using var server = new SimulatedFlowerServer([ServerTrack("Drop")]);
        var phone = NewClient([LocalFile("Mine", artist: "Me")]);
        await phone.PullLibraryAsync(server);
        server.AdminReachable = false;

        var outcome = await phone.RemovalAgainst(server).RemoveAsync(
            [phone.Single("Drop"), phone.Single("Mine")], deleteFiles: false);

        Assert.NotNull(outcome.Error);
        Assert.Equal(2, phone.Library.Tracks.Count);
        Assert.Single(server.Library.Tracks);
    }

    // A file of the phone's own, removed and kept on disk: the next rescan
    // finds it where it always was, and must leave it out.
    [Fact]
    public async Task A_removed_file_kept_on_disk_does_not_come_back_with_the_next_rescan()
    {
        using var server = new SimulatedFlowerServer([]);
        var mine = LocalFile("Mine", artist: "Me");
        var phone = NewClient([mine]);

        await phone.RemovalAgainst(server).RemoveAsync([mine], deleteFiles: false);
        phone.Library.UpdateTracks([LocalFile("Mine", artist: "Me")]);

        Assert.Empty(phone.Library.Tracks);
    }
}
