using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Importer;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests;

// An owner's device and its server keeping the same files: songs added on the
// device go up, songs deleted from it are removed there, and Date Added is the
// oldest either of them knows. Played end to end, the way SyncScenarioTests
// plays the metadata half - a real client Library scanning a real folder of
// real tagged files, syncing through LibrarySyncService and
// LibraryMirrorService against a SimulatedFlowerServer that takes the files in
// with the server's own LibraryIngest.
//
// Each device here has a music folder on disk, and "a scan" is the real
// Importer over it, because the thing under test is what a scan finding or not
// finding a file comes to mean two machines away.
[Collection("PlatformDataDirectory")]
public class LibraryMirrorScenarioTests : PinnedDataDirectory
{
    private static readonly DateTimeOffset Years = new(2016, 5, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Device : IDisposable
    {
        private readonly string _dataDirectory = Directory.CreateTempSubdirectory("flower-mirror-device").FullName;
        private readonly Importer.Importer _importer = new(NullLogger<Importer.Importer>.Instance);

        public string MusicFolder { get; }
        public DeviceSigningKey Key { get; } = TestSigningKey.Create();
        public AppSettings Settings { get; }
        public Library Library { get; } = new([]);
        public LibrarySyncService LibrarySync { get; }

        // One for the life of the device, like the app's: what it has been
        // refused is remembered between syncs.
        private LibraryMirrorService? _mirror;

        public Device()
        {
            MusicFolder = Path.Combine(_dataDirectory, "Music");
            Directory.CreateDirectory(MusicFolder);
            Settings = new AppSettings { LibraryPaths = [MusicFolder] };
            LibrarySync = new LibrarySyncService(
                Library, Identity, Key, Settings,
                new ServerStarBaselineStore(NullLogger<ServerStarBaselineStore>.Instance),
                TestLogArchive.InTempDirectory(),
                NullLogger<LibrarySyncService>.Instance,
                NullLogger<RemoteLibraryImporter>.Instance);
        }

        private DeviceIdentity Identity => new() { Fingerprint = Key.Fingerprint, Alias = "Desktop" };

        public string PathOf(string relativePath) =>
            Path.Combine(MusicFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));

        public void AddFile(string relativePath, string title, byte seed = 1) =>
            TaggedAudioFile.Create(MusicFolder, relativePath, title, seed: seed);

        public void MoveFile(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathOf(to))!);
            File.Move(PathOf(from), PathOf(to));
        }

        // Rewrites the file's title the way Track Info does: into the file.
        public void Retitle(string relativePath, string title)
        {
            using var file = TagLib.File.Create(PathOf(relativePath));
            file.Tag.Title = title;
            file.Save();
        }

        // A title changed in Track Info, as either head's Save does it: into
        // the file if this device has one, onto the track, and announced.
        public void EditTitle(string title, string newTitle)
        {
            var track = Single(title);
            if (track.Path is { } path)
            {
                using var file = TagLib.File.Create(path);
                file.Tag.Title = newTitle;
                file.Save();
            }

            track.Title = newTitle;
            Library.NotifyTrackChanged(track, TrackChange.Tags);
        }

        // A cover changed in Track Info, as AlbumArtEditor does it for a song
        // with a file here: into the file, and announced.
        public void SetArt(string title, byte[] picture)
        {
            var track = Single(title);
            Assert.True(AlbumArtWriter.TryWrite(track.Path!, picture, "image/png"));
            Library.NotifyTrackChanged(track, TrackChange.Artwork);
        }

        public byte[]? ArtIn(string relativePath) => LocalAlbumArtReader.EmbeddedIn(PathOf(relativePath))?.Bytes;

        public string TitleInFile(string relativePath)
        {
            using var file = TagLib.File.Create(PathOf(relativePath));
            return file.Tag.Title;
        }

        // What the app does at launch and whenever its folders change.
        public void Scan() => Library.UpdateTracks(_importer.Import(Settings.LibraryPaths));

        public Track Single(string title) => Library.Tracks.Single(t => t.Title == title);

        // One sync, in the order PeerSyncCoordinator runs it: what has gone,
        // then the pull, then what the server lacks - and a second pull if
        // anything went up.
        //
        // away reaches the same server as a device not on its network would:
        // at an address it was told rather than one it found, and a public one.
        public async Task<LibraryUploadSummary> SyncAsync(SimulatedFlowerServer server, bool away = false)
        {
            PlatformDataDirectory.Current = _dataDirectory;
            Settings.PairedServerFingerprint = server.Fingerprint;
            _mirror ??= new LibraryMirrorService(
                Library, Settings, LibrarySync, new SignedDeviceCredentials(Identity, Key),
                NullLogger<LibraryMirrorService>.Instance);

            var device = !away
                ? server.Device
                : new DiscoveredDevice
                {
                    InstanceName = "server",
                    BaseUri = server.Device.BaseUri,
                    Fingerprint = server.Fingerprint,
                    WeAreAdmin = server.CallerIsAdmin,
                    Alias = "Server",
                    IsRemembered = true,
                    Ip = System.Net.IPAddress.Parse("203.0.113.7"),
                };

            await _mirror.ReportVanishedAsync(device);
            await PullAsync(device);
            await _mirror.PushTagEditsAsync(device);
            await _mirror.PushArtEditsAsync(device);
            var summary = await _mirror.UploadPendingAsync(device);
            if (summary.Uploaded > 0)
                await PullAsync(device);
            return summary;
        }

        private async Task PullAsync(DiscoveredDevice device)
        {
            var result = await LibrarySync.SyncWithAsync(device);
            Assert.True(result.Success, $"library sync failed: {result.Failure}");
        }

        public void Dispose()
        {
            Key.Dispose();
            TempDirectory.DeleteWhenReleased(_dataDirectory);
        }
    }

    private Device NewDevice() => Own(new Device());

    private SimulatedFlowerServer NewServer(bool callerIsAdmin = true) =>
        Own(new SimulatedFlowerServer([], musicFolder: Path.Combine(DataDirectory, $"server-music-{Guid.NewGuid():N}"))
        {
            CallerIsAdmin = callerIsAdmin,
        });

    // A song the server already holds as a file of its own, as its scan
    // would have found it.
    private static Track ServerFile(SimulatedFlowerServer server, string relativePath, string title, byte seed = 1)
    {
        var path = TaggedAudioFile.Create(server.MusicFolder!, relativePath, title, seed: seed);
        var track = new Importer.Importer(NullLogger<Importer.Importer>.Instance).ImportFile(path)!;
        return server.Library.AddScannedTrack(track);
    }

    private static string ServerPath(SimulatedFlowerServer server, string relativePath) =>
        Path.Combine(server.MusicFolder!, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static int UploadsBegun(SimulatedFlowerServer server)
    {
        lock (server.Requests)
            return server.Requests.Count(r => r == $"POST {SimulatedFlowerServer.UploadsPath}");
    }

    // ── New songs ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_new_song_on_an_owners_device_is_uploaded_to_the_same_place_on_the_server()
    {
        var server = NewServer();
        var desktop = NewDevice();
        desktop.AddFile("Angine de Poitrine/Vol.II/01 Fabienk.wav", "Fabienk");
        desktop.Scan();
        desktop.Single("Fabienk").DateAdded = Years;

        var summary = await desktop.SyncAsync(server);

        Assert.Equal(1, summary.Uploaded);
        Assert.Equal(
            File.ReadAllBytes(desktop.PathOf("Angine de Poitrine/Vol.II/01 Fabienk.wav")),
            File.ReadAllBytes(ServerPath(server, "Angine de Poitrine/Vol.II/01 Fabienk.wav")));

        var onServer = server.Library.Tracks.Single();
        Assert.Equal("Fabienk", onServer.Title);
        Assert.Equal(Years, onServer.DateAdded);

        // One song on the device, not a file and a placeholder of it; and it
        // now knows the server's name for it.
        var mine = desktop.Library.Tracks.Single();
        Assert.NotNull(mine.Path);
        Assert.Equal(onServer.Id.ToKey(), mine.OriginTrackId);
        Assert.Equal("Angine de Poitrine/Vol.II/01 Fabienk.wav", mine.OriginRelativePath);
    }

    [Fact]
    public async Task A_second_sync_uploads_nothing()
    {
        var server = NewServer();
        var desktop = NewDevice();
        desktop.AddFile("a.wav", "A");
        desktop.AddFile("b/b.wav", "B");
        desktop.Scan();
        await desktop.SyncAsync(server);
        var begun = UploadsBegun(server);

        var summary = await desktop.SyncAsync(server);

        Assert.Equal(0, summary.Uploaded);
        Assert.Equal(begun, UploadsBegun(server));
        Assert.Equal(2, server.Library.Tracks.Count);
    }

    // Another device that pairs afterwards gets it like any other server song.
    [Fact]
    public async Task An_uploaded_song_reaches_every_other_device_as_a_song_of_the_servers()
    {
        var server = NewServer();
        var desktop = NewDevice();
        desktop.AddFile("a.wav", "A");
        desktop.Scan();
        await desktop.SyncAsync(server);

        var phone = NewDevice();
        await phone.SyncAsync(server);

        var placeholder = phone.Single("A");
        Assert.Null(placeholder.Path);
        Assert.Equal(server.Library.Tracks.Single().Id.ToKey(), placeholder.OriginTrackId);
    }

    // Handing a server files is an owner's act.
    [Fact]
    public async Task A_listeners_files_stay_on_the_listeners_device()
    {
        var server = NewServer(callerIsAdmin: false);
        var phone = NewDevice();
        phone.AddFile("mine.wav", "Mine");
        phone.Scan();

        var summary = await phone.SyncAsync(server);

        Assert.Equal(0, summary.Uploaded);
        Assert.Equal(0, UploadsBegun(server));
        Assert.Empty(server.Library.Tracks);
        Assert.NotNull(phone.Single("Mine").Path);
    }

    // The same album copied to both machines by hand, long before any of this.
    [Fact]
    public async Task A_song_the_server_already_has_is_recognised_rather_than_uploaded()
    {
        var server = NewServer();
        ServerFile(server, "filed/differently/A.wav", "A");
        var desktop = NewDevice();
        desktop.AddFile("a.wav", "A");
        desktop.Scan();

        var summary = await desktop.SyncAsync(server);

        Assert.Equal(0, summary.Uploaded);
        Assert.Equal(0, UploadsBegun(server));
        Assert.Single(server.Library.Tracks);
        Assert.False(File.Exists(ServerPath(server, "a.wav")));
    }

    // A file a rescan finds is compared with the server's catalog before it is
    // offered, even when the catalog itself has not changed since the last pull.
    [Fact]
    public async Task A_file_found_by_a_later_scan_is_matched_against_the_server_before_it_is_offered()
    {
        var server = NewServer();
        ServerFile(server, "filed/differently/A.wav", "A");
        var desktop = NewDevice();
        await desktop.SyncAsync(server);

        desktop.AddFile("a.wav", "A");
        desktop.Scan();
        var summary = await desktop.SyncAsync(server);

        Assert.Equal(0, summary.Uploaded);
        Assert.Equal(0, UploadsBegun(server));
        Assert.NotNull(desktop.Library.Tracks.Single().Path);
    }

    // A download is the server's song, lent. When the server's song goes, so
    // does the copy - it is not kept as a file of the phone's own, and it is
    // certainly not handed back.
    [Fact]
    public async Task A_downloaded_copy_of_a_song_the_server_removed_is_deleted_rather_than_offered_back()
    {
        var server = NewServer();
        var theirs = ServerFile(server, "a.wav", "A");
        var phone = NewDevice();
        await phone.SyncAsync(server);
        phone.AddFile("downloads/a.wav", "A");
        var downloaded = phone.Single("A");
        downloaded.Path = phone.PathOf("downloads/a.wav");
        downloaded.IsLocallyDownloaded = true;

        server.Library.RemoveTracks([theirs], excludePaths: [theirs.Path!]);
        var summary = await phone.SyncAsync(server);

        Assert.Equal(0, summary.Uploaded);
        Assert.Empty(server.Library.Tracks);
        Assert.Empty(phone.Library.Tracks);
        Assert.False(File.Exists(phone.PathOf("downloads/a.wav")));
        Assert.Empty(phone.Library.ExcludedPaths);
    }

    // The same on a listener's phone: whose song it was is not in doubt.
    [Fact]
    public async Task A_listeners_downloaded_copy_goes_too_but_a_file_it_imported_itself_stays()
    {
        var server = NewServer(callerIsAdmin: false);
        var lent = ServerFile(server, "lent.wav", "Lent");
        var shared = ServerFile(server, "shared.wav", "Shared");
        var phone = NewDevice();
        phone.AddFile("shared.wav", "Shared");
        phone.Scan();
        await phone.SyncAsync(server);
        phone.AddFile("downloads/lent.wav", "Lent");
        var downloaded = phone.Single("Lent");
        downloaded.Path = phone.PathOf("downloads/lent.wav");
        downloaded.IsLocallyDownloaded = true;

        server.Library.RemoveTracks([lent, shared], excludePaths: []);
        await phone.SyncAsync(server);

        Assert.Equal(["Shared"], phone.Library.Tracks.Select(t => t.Title));
        Assert.NotNull(phone.Single("Shared").Path);
        Assert.False(File.Exists(phone.PathOf("downloads/lent.wav")));
    }

    // Which of the two files is right is not something a sync can know, so
    // neither is touched - and the device stops asking.
    [Fact]
    public async Task A_name_the_server_has_taken_for_a_different_file_is_left_alone_and_not_asked_about_twice()
    {
        var server = NewServer();
        ServerFile(server, "a.wav", "Something Else", seed: 2);
        var theirBytes = File.ReadAllBytes(ServerPath(server, "a.wav"));
        var desktop = NewDevice();
        desktop.AddFile("a.wav", "A");
        desktop.Scan();

        var first = await desktop.SyncAsync(server);
        var second = await desktop.SyncAsync(server);

        Assert.Equal(1, first.Refused);
        Assert.Equal(0, second.Refused);
        Assert.Equal(1, UploadsBegun(server));
        Assert.Equal(theirBytes, File.ReadAllBytes(ServerPath(server, "a.wav")));
        Assert.Equal(["Something Else"], server.Library.Tracks.Select(t => t.Title));
    }

    // ── Deleted songs ────────────────────────────────────────────────────

    [Fact]
    public async Task A_song_deleted_from_an_owners_disk_leaves_the_servers_library_and_its_file_is_kept()
    {
        var server = NewServer();
        var desktop = NewDevice();
        desktop.AddFile("keep.wav", "Keep");
        desktop.AddFile("album/drop.wav", "Drop");
        desktop.Scan();
        await desktop.SyncAsync(server);

        File.Delete(desktop.PathOf("album/drop.wav"));
        desktop.Scan();
        await desktop.SyncAsync(server);

        Assert.Equal(["Keep"], server.Library.Tracks.Select(t => t.Title));

        // Out of the library the server serves, and nothing more: the file is
        // where it was, set aside from scans, for its owner to delete or
        // restore from Removed Songs.
        Assert.True(File.Exists(ServerPath(server, "album/drop.wav")));
        Assert.Equal([ServerPath(server, "album/drop.wav")], server.Library.ExcludedPaths.Select(e => e.Path));

        // Gone from the device as well, not back as a placeholder; and on
        // record at both ends for the day it returns.
        Assert.Equal(["Keep"], desktop.Library.Tracks.Select(t => t.Title));
        Assert.Contains(server.Library.RemovedTracks, r => r.Track.Title == "Drop" && r.Deliberate);
        Assert.Contains(desktop.Library.RemovedTracks, r => r.Track.Title == "Drop" && !r.OwedToOrigin);
    }

    // The clean-up, when the owner says so: the files that are no longer in
    // the library are deleted for good, and the folders that empties go too.
    [Fact]
    public async Task Cleaning_up_the_server_deletes_the_files_of_removed_songs_for_good()
    {
        var server = NewServer();
        var desktop = NewDevice();
        desktop.AddFile("keep.wav", "Keep");
        desktop.AddFile("album/drop.wav", "Drop");
        desktop.Scan();
        await desktop.SyncAsync(server);
        File.Delete(desktop.PathOf("album/drop.wav"));
        desktop.Scan();
        await desktop.SyncAsync(server);

        var (deleted, notDeleted) = LibraryRemoval.DeleteRemovedFiles(
            server.Library, server.Library.ExcludedPaths.Select(e => e.Path).ToList(), [server.MusicFolder!],
            NullLogger.Instance);

        Assert.Equal((1, 0), (deleted, notDeleted));
        Assert.False(File.Exists(ServerPath(server, "album/drop.wav")));
        Assert.False(Directory.Exists(ServerPath(server, "album")));
        Assert.True(File.Exists(ServerPath(server, "keep.wav")));
        Assert.Empty(server.Library.ExcludedPaths);
    }

    [Fact]
    public async Task A_deleted_song_put_back_later_returns_as_the_song_it_was()
    {
        var server = NewServer();
        var desktop = NewDevice();
        desktop.AddFile("song.wav", "Song");
        desktop.AddFile("other.wav", "Other");
        desktop.Scan();
        desktop.Single("Song").DateAdded = Years;
        await desktop.SyncAsync(server);

        var id = server.Library.Tracks.Single(t => t.Title == "Song").Id;
        server.Library.SetStarred(StarTarget.Song, id.ToKey(), starred: true);
        server.Library.MergeReportedTrackState("phone", [new TrackStateDto(id.ToKey(), 12)], callerIsAdmin: false);

        File.Delete(desktop.PathOf("song.wav"));
        desktop.Scan();
        await desktop.SyncAsync(server);
        Assert.Equal(["Other"], server.Library.Tracks.Select(t => t.Title));

        desktop.AddFile("song.wav", "Song");
        desktop.Scan();
        await desktop.SyncAsync(server);

        var back = server.Library.Tracks.Single(t => t.Title == "Song");
        Assert.Equal(id, back.Id);
        Assert.True(back.Starred);
        Assert.Equal(12, back.RemotePlayCounts["phone"]);
        Assert.Equal(Years, back.DateAdded);
        Assert.Empty(server.Library.RemovedTracks);

        // And on the device that put it back.
        var mine = desktop.Single("Song");
        Assert.NotNull(mine.Path);
        Assert.Equal(Years, mine.DateAdded);
        Assert.True(mine.Starred);
        Assert.Equal(id.ToKey(), mine.OriginTrackId);
    }

    // The drive the music lives on is not plugged in. Nothing was deleted.
    [Fact]
    public async Task A_music_folder_that_is_not_there_deletes_nothing_from_the_server()
    {
        var server = NewServer();
        var desktop = NewDevice();
        desktop.AddFile("album/song.wav", "Song");
        desktop.Scan();
        await desktop.SyncAsync(server);
        var id = desktop.Single("Song").Id;
        var bytes = File.ReadAllBytes(desktop.PathOf("album/song.wav"));

        Directory.Delete(desktop.MusicFolder, recursive: true);
        desktop.Scan();
        await desktop.SyncAsync(server);

        Assert.Single(server.Library.Tracks);
        Assert.True(File.Exists(ServerPath(server, "album/song.wav")));

        // Still in the device's library, as the server's copy - under the id
        // it always had, so the playlists holding it are none the wiser.
        var placeholder = desktop.Library.Tracks.Single();
        Assert.Null(placeholder.Path);
        Assert.Equal(id, placeholder.Id);

        // Plugged back in: the same song again, and nothing goes up twice.
        var begun = UploadsBegun(server);
        Directory.CreateDirectory(Path.GetDirectoryName(desktop.PathOf("album/song.wav"))!);
        File.WriteAllBytes(desktop.PathOf("album/song.wav"), bytes);
        desktop.Scan();
        var summary = await desktop.SyncAsync(server);

        Assert.Equal(0, summary.Uploaded);
        Assert.Equal(begun, UploadsBegun(server));
        Assert.Equal(id, desktop.Library.Tracks.Single().Id);
        Assert.NotNull(desktop.Library.Tracks.Single().Path);
        Assert.Single(server.Library.Tracks);
    }

    // An empty mount point is what a share that failed to mount leaves behind.
    [Fact]
    public async Task A_music_folder_that_is_there_but_empty_deletes_nothing_from_the_server()
    {
        var server = NewServer();
        var desktop = NewDevice();
        desktop.AddFile("song.wav", "Song");
        desktop.Scan();
        await desktop.SyncAsync(server);

        File.Delete(desktop.PathOf("song.wav"));
        desktop.Scan();
        await desktop.SyncAsync(server);

        Assert.Single(server.Library.Tracks);
        Assert.True(File.Exists(ServerPath(server, "song.wav")));
    }

    // The files are where they were; the device has only been told to stop
    // looking at them.
    [Fact]
    public async Task A_folder_taken_out_of_the_settings_deletes_nothing_from_the_server()
    {
        var server = NewServer();
        var desktop = NewDevice();
        desktop.AddFile("keep/a.wav", "A");
        desktop.Scan();
        await desktop.SyncAsync(server);

        desktop.Settings.LibraryPaths = [];
        desktop.Scan();
        await desktop.SyncAsync(server);

        Assert.Single(server.Library.Tracks);
        Assert.True(File.Exists(ServerPath(server, "keep/a.wav")));
        Assert.DoesNotContain(desktop.Library.RemovedTracks, r => r.OwedToOrigin);
    }

    // The song a listener lost track of is not the owner's to lose.
    [Fact]
    public async Task A_listener_deleting_its_own_copy_removes_nothing_from_the_server()
    {
        var server = NewServer(callerIsAdmin: false);
        ServerFile(server, "a.wav", "A");
        var phone = NewDevice();
        phone.AddFile("a.wav", "A");
        phone.AddFile("other.wav", "Other");
        phone.Scan();
        await phone.SyncAsync(server);

        File.Delete(phone.PathOf("a.wav"));
        phone.Scan();
        await phone.SyncAsync(server);

        Assert.Single(server.Library.Tracks);
        Assert.DoesNotContain(phone.Library.RemovedTracks, r => r.OwedToOrigin);
    }

    // Two of the owner's machines each hold the album. Deleting a song on one
    // removes it everywhere: from the server's library, and from the other
    // machine's - whose own file is set aside rather than deleted, and is
    // certainly not uploaded back.
    [Fact]
    public async Task A_song_deleted_on_one_of_the_owners_devices_leaves_every_library()
    {
        var server = NewServer();
        var desktop = NewDevice();
        var laptop = NewDevice();
        foreach (var device in new[] { desktop, laptop })
        {
            device.AddFile("song.wav", "Song");
            device.AddFile("other.wav", "Other");
            device.Scan();
        }
        await desktop.SyncAsync(server);
        await laptop.SyncAsync(server);
        Assert.Equal(2, server.Library.Tracks.Count);

        File.Delete(desktop.PathOf("song.wav"));
        desktop.Scan();
        await desktop.SyncAsync(server);
        var summary = await laptop.SyncAsync(server);

        Assert.Equal(0, summary.Uploaded);
        Assert.Equal(["Other"], server.Library.Tracks.Select(t => t.Title));
        Assert.Equal(["Other"], laptop.Library.Tracks.Select(t => t.Title));

        // Still on the laptop's disk, out of its library and its scans, until
        // it is cleaned up there.
        Assert.True(File.Exists(laptop.PathOf("song.wav")));
        Assert.Equal([laptop.PathOf("song.wav")], laptop.Library.ExcludedPaths.Select(e => e.Path));
        laptop.Scan();
        Assert.Equal(["Other"], laptop.Library.Tracks.Select(t => t.Title));
    }

    // And when it is put back on the first, it comes back on the second too:
    // the file that was set aside there is wanted again.
    [Fact]
    public async Task A_song_put_back_returns_on_the_other_devices_that_had_set_their_copies_aside()
    {
        var server = NewServer();
        var desktop = NewDevice();
        var laptop = NewDevice();
        foreach (var device in new[] { desktop, laptop })
        {
            device.AddFile("song.wav", "Song");
            device.AddFile("other.wav", "Other");
            device.Scan();
        }
        await desktop.SyncAsync(server);
        await laptop.SyncAsync(server);
        var laptopsId = laptop.Single("Song").Id;
        File.Delete(desktop.PathOf("song.wav"));
        desktop.Scan();
        await desktop.SyncAsync(server);
        await laptop.SyncAsync(server);

        desktop.AddFile("song.wav", "Song");
        desktop.Scan();
        await desktop.SyncAsync(server);
        await laptop.SyncAsync(server);

        // Back in the laptop's library at once, as the server's song under the
        // id it had there - and off the set-aside list, so the next scan finds
        // the laptop's own file and it is a local song again.
        Assert.Equal(laptopsId, laptop.Single("Song").Id);
        Assert.Empty(laptop.Library.ExcludedPaths);
        laptop.Scan();
        Assert.Equal(laptop.PathOf("song.wav"), laptop.Single("Song").Path);
        Assert.Equal(laptopsId, laptop.Single("Song").Id);
        Assert.Equal(2, laptop.Library.Tracks.Count);
    }

    // A server that has merely lost a song - its disk, not its owner's
    // decision - names nothing as removed, and no device drops a file over it.
    [Fact]
    public async Task A_song_that_simply_went_missing_on_the_server_costs_no_device_its_own_copy()
    {
        var server = NewServer();
        var desktop = NewDevice();
        desktop.AddFile("song.wav", "Song");
        desktop.Scan();
        await desktop.SyncAsync(server);

        server.Library.UpdateTracks([]);
        var summary = await desktop.SyncAsync(server);

        var mine = desktop.Single("Song");
        Assert.NotNull(mine.Path);
        Assert.True(mine.WithdrawnByOrigin);
        Assert.Empty(desktop.Library.ExcludedPaths);
        Assert.Equal(0, summary.Uploaded);
    }

    // The file that was set aside on the server is in the way of a different
    // rip of the same song. It was waiting to be cleaned up, not being kept.
    [Fact]
    public async Task A_different_file_uploaded_where_a_removed_one_was_set_aside_takes_its_place()
    {
        var server = NewServer();
        var desktop = NewDevice();
        desktop.AddFile("song.wav", "Song", seed: 1);
        desktop.AddFile("other.wav", "Other");
        desktop.Scan();
        await desktop.SyncAsync(server);
        File.Delete(desktop.PathOf("song.wav"));
        desktop.Scan();
        await desktop.SyncAsync(server);

        desktop.AddFile("song.wav", "Song", seed: 2);
        desktop.Scan();
        var summary = await desktop.SyncAsync(server);

        Assert.Equal(1, summary.Uploaded);
        Assert.Equal(File.ReadAllBytes(desktop.PathOf("song.wav")), File.ReadAllBytes(ServerPath(server, "song.wav")));
        Assert.Empty(server.Library.ExcludedPaths);
        Assert.Equal(2, server.Library.Tracks.Count);
    }

    // ── Moved and edited files ───────────────────────────────────────────

    [Fact]
    public async Task A_file_moved_on_an_owners_device_is_moved_on_the_server_and_stays_the_same_song()
    {
        var server = NewServer();
        var desktop = NewDevice();
        desktop.AddFile("Artist/Old Album/01 Song.wav", "Song");
        desktop.Scan();
        await desktop.SyncAsync(server);
        var id = server.Library.Tracks.Single().Id;
        var begun = UploadsBegun(server);

        desktop.MoveFile("Artist/Old Album/01 Song.wav", "Artist/New Album/1 - Song.wav");
        desktop.Scan();
        var summary = await desktop.SyncAsync(server);

        Assert.Equal(1, summary.Moved);
        Assert.Equal(0, summary.Uploaded);
        Assert.Equal(begun, UploadsBegun(server));

        var onServer = server.Library.Tracks.Single();
        Assert.Equal(id, onServer.Id);
        Assert.Equal(ServerPath(server, "Artist/New Album/1 - Song.wav"), onServer.Path);
        Assert.True(File.Exists(ServerPath(server, "Artist/New Album/1 - Song.wav")));
        Assert.False(Directory.Exists(ServerPath(server, "Artist/Old Album")));
        Assert.Empty(server.Library.RemovedTracks);

        var mine = desktop.Library.Tracks.Single();
        Assert.Null(mine.MovedFromPath);
        Assert.Equal("Artist/New Album/1 - Song.wav", mine.OriginRelativePath);

        // Said once.
        var second = await desktop.SyncAsync(server);
        Assert.Equal(0, second.Moved);
    }

    // Two machines that were each handed the same album long ago may file it
    // differently, and neither layout is news. Pairing does not reorganise a
    // server's folders.
    [Fact]
    public async Task A_layout_that_merely_differs_from_the_servers_moves_nothing()
    {
        var server = NewServer();
        ServerFile(server, "filed/differently/A.wav", "A");
        var desktop = NewDevice();
        desktop.AddFile("a.wav", "A");
        desktop.Scan();

        var summary = await desktop.SyncAsync(server);

        Assert.Equal(0, summary.Moved);
        Assert.True(File.Exists(ServerPath(server, "filed/differently/A.wav")));
    }

    // Tags live in the file, so sending the file is how an edit travels.
    [Fact]
    public async Task A_tag_edit_on_an_owners_device_reaches_the_server_and_every_other_device()
    {
        var server = NewServer();
        var desktop = NewDevice();
        desktop.AddFile("song.wav", "Teh Song");
        desktop.Scan();
        await desktop.SyncAsync(server);
        var id = server.Library.Tracks.Single().Id;
        server.Library.SetStarred(StarTarget.Song, id.ToKey(), starred: true);
        var phone = NewDevice();
        server.CallerIsAdmin = false;
        await phone.SyncAsync(server);
        server.CallerIsAdmin = true;

        desktop.Retitle("song.wav", "The Song");
        desktop.Scan();
        var summary = await desktop.SyncAsync(server);

        Assert.Equal(1, summary.Uploaded);
        var onServer = server.Library.Tracks.Single();
        Assert.Equal("The Song", onServer.Title);
        Assert.Equal(id, onServer.Id);
        Assert.True(onServer.Starred);
        Assert.Equal(File.ReadAllBytes(desktop.PathOf("song.wav")), File.ReadAllBytes(ServerPath(server, "song.wav")));
        Assert.Equal(["song.wav"], Directory.EnumerateFiles(server.MusicFolder!).Select(Path.GetFileName));

        server.CallerIsAdmin = false;
        await phone.SyncAsync(server);
        Assert.Equal(["The Song"], phone.Library.Tracks.Select(t => t.Title));

        // And only once.
        server.CallerIsAdmin = true;
        var second = await desktop.SyncAsync(server);
        Assert.Equal(0, second.Uploaded);
    }

    // The same edit, made outside Flower, and a second machine of the owner's
    // that has its own copy of the file: the tags are written into that copy
    // too, and writing them does not make it a file to send back.
    [Fact]
    public async Task A_file_retagged_outside_flower_retags_the_owners_other_copies_without_an_echo()
    {
        var server = NewServer();
        var desktop = NewDevice();
        var laptop = NewDevice();
        foreach (var device in new[] { desktop, laptop })
        {
            device.AddFile("song.wav", "Teh Song");
            device.Scan();
        }
        await desktop.SyncAsync(server);
        await laptop.SyncAsync(server);

        desktop.Retitle("song.wav", "The Song");
        desktop.Scan();
        await desktop.SyncAsync(server);
        var begun = UploadsBegun(server);

        var first = await laptop.SyncAsync(server);
        var second = await laptop.SyncAsync(server);
        var third = await desktop.SyncAsync(server);

        Assert.Equal("The Song", laptop.TitleInFile("song.wav"));
        Assert.Equal(["The Song"], laptop.Library.Tracks.Select(t => t.Title));
        Assert.Equal(0, first.Uploaded + second.Uploaded + third.Uploaded);
        Assert.Equal(begun, UploadsBegun(server));
    }

    // The phone has no file for the song at all - it streams it - and the edit
    // is made in its Track Info. It reaches the server's file, and from there
    // every other device: the desktop's own copy of the file, a listener's
    // download, a listener's placeholder.
    [Fact]
    public async Task A_tag_edited_on_a_phone_reaches_the_server_and_from_there_every_other_device()
    {
        var server = NewServer();
        var desktop = NewDevice();
        desktop.AddFile("album/song.wav", "Teh Song");
        desktop.Scan();
        await desktop.SyncAsync(server);
        var id = server.Library.Tracks.Single().Id;

        var phone = NewDevice();
        await phone.SyncAsync(server);
        var guest = NewDevice();
        var guestWithDownload = NewDevice();
        server.CallerIsAdmin = false;
        await guest.SyncAsync(server);
        await guestWithDownload.SyncAsync(server);
        guestWithDownload.AddFile("downloads/song.wav", "Teh Song");
        guestWithDownload.Single("Teh Song").Path = guestWithDownload.PathOf("downloads/song.wav");
        guestWithDownload.Single("Teh Song").IsLocallyDownloaded = true;
        server.CallerIsAdmin = true;

        Assert.Null(phone.Single("Teh Song").Path);
        phone.EditTitle("Teh Song", "The Song");
        await phone.SyncAsync(server);

        // The server: its library, and the file a scan would read.
        var onServer = server.Library.Tracks.Single();
        Assert.Equal("The Song", onServer.Title);
        Assert.Equal(id, onServer.Id);
        using (var file = TagLib.File.Create(ServerPath(server, "album/song.wav")))
            Assert.Equal("The Song", file.Tag.Title);

        // The owner's desktop, which has its own copy of the file.
        var summary = await desktop.SyncAsync(server);
        Assert.Equal("The Song", desktop.Library.Tracks.Single().Title);
        Assert.Equal("The Song", desktop.TitleInFile("album/song.wav"));
        Assert.Equal(0, summary.Uploaded);

        // A rescan there reads the same thing back, and it is the same song.
        var desktopsId = desktop.Library.Tracks.Single().Id;
        desktop.Scan();
        Assert.Equal("The Song", desktop.Library.Tracks.Single().Title);
        Assert.Equal(desktopsId, desktop.Library.Tracks.Single().Id);

        // Listeners: a placeholder, and a copy one of them downloaded.
        server.CallerIsAdmin = false;
        await guest.SyncAsync(server);
        await guestWithDownload.SyncAsync(server);
        Assert.Equal(["The Song"], guest.Library.Tracks.Select(t => t.Title));
        Assert.Equal(["The Song"], guestWithDownload.Library.Tracks.Select(t => t.Title));
        Assert.Equal("The Song", guestWithDownload.TitleInFile("downloads/song.wav"));

        // And then it is over: nobody sends anybody anything again.
        server.CallerIsAdmin = true;
        lock (server.Requests)
            server.Requests.Clear();
        await desktop.SyncAsync(server);
        await phone.SyncAsync(server);
        await desktop.SyncAsync(server);
        lock (server.Requests)
        {
            Assert.DoesNotContain(server.Requests, r => r.StartsWith("POST /api/admin") || r.StartsWith("PUT /api/admin"));
        }
    }

    // Edited on a copy the phone had downloaded: written into that file, and
    // sent up as values - the file itself is the server's, lent, and is not.
    [Fact]
    public async Task A_tag_edited_on_a_downloaded_copy_reaches_the_server_without_the_file_being_sent()
    {
        var server = NewServer();
        ServerFile(server, "song.wav", "Teh Song");
        var phone = NewDevice();
        await phone.SyncAsync(server);
        phone.AddFile("downloads/song.wav", "Teh Song");
        phone.Single("Teh Song").Path = phone.PathOf("downloads/song.wav");
        phone.Single("Teh Song").IsLocallyDownloaded = true;

        phone.EditTitle("Teh Song", "The Song");
        var summary = await phone.SyncAsync(server, away: true);

        Assert.Equal(0, summary.Uploaded);
        Assert.Equal(0, UploadsBegun(server));
        Assert.Equal("The Song", server.Library.Tracks.Single().Title);
        using var file = TagLib.File.Create(ServerPath(server, "song.wav"));
        Assert.Equal("The Song", file.Tag.Title);
    }

    // Two of the owner's devices edit the same song. The later edit is the
    // one everybody ends up with, whichever of them syncs first.
    [Fact]
    public async Task The_newest_of_two_edits_wins_everywhere()
    {
        var server = NewServer();
        ServerFile(server, "song.wav", "Song");
        var phone = NewDevice();
        var tablet = NewDevice();
        await phone.SyncAsync(server);
        await tablet.SyncAsync(server);

        phone.EditTitle("Song", "Phone's Title");
        await Task.Delay(20, TestContext.Current.CancellationToken);
        tablet.EditTitle("Song", "Tablet's Title");

        // The later edit arrives first; the earlier one must not overwrite it.
        await tablet.SyncAsync(server);
        await phone.SyncAsync(server);
        await phone.SyncAsync(server);

        Assert.Equal("Tablet's Title", server.Library.Tracks.Single().Title);
        Assert.Equal(["Tablet's Title"], tablet.Library.Tracks.Select(t => t.Title));
        Assert.Equal(["Tablet's Title"], phone.Library.Tracks.Select(t => t.Title));
    }

    // Editing the server's songs is an owner's act. A guest's edit of one
    // goes nowhere and does not stick; an owner's reaches the guest like
    // anyone else - into a file of the guest's own, if that is the copy it has.
    [Fact]
    public async Task A_listener_gets_an_owners_edits_and_a_listeners_edit_of_a_server_song_does_not_travel()
    {
        var server = NewServer();
        ServerFile(server, "shared.wav", "Shared");
        ServerFile(server, "theirs.wav", "Theirs");
        var owner = NewDevice();
        await owner.SyncAsync(server);
        var guest = NewDevice();
        guest.AddFile("shared.wav", "Shared");
        guest.AddFile("only-mine.wav", "Only Mine");
        guest.Scan();
        server.CallerIsAdmin = false;
        await guest.SyncAsync(server);

        // The app does not offer this edit (SyncRolePolicy.MayEditSong); made
        // anyway, it is local, and the next thing the server says replaces it.
        guest.EditTitle("Theirs", "Mine Now");
        // A song that is only the guest's is the guest's to edit, and stays so.
        guest.EditTitle("Only Mine", "Still Only Mine");
        await guest.SyncAsync(server);
        server.CallerIsAdmin = true;
        owner.EditTitle("Shared", "Shared (Remastered)");
        owner.EditTitle("Theirs", "Theirs (Live)");
        await owner.SyncAsync(server);
        server.CallerIsAdmin = false;
        await guest.SyncAsync(server);

        Assert.Equal(["Shared (Remastered)", "Theirs (Live)"], server.Library.Tracks.Select(t => t.Title).Order());
        Assert.Equal(
            ["Shared (Remastered)", "Still Only Mine", "Theirs (Live)"],
            guest.Library.Tracks.Select(t => t.Title).Order());
        Assert.Equal("Shared (Remastered)", guest.TitleInFile("shared.wav"));
        Assert.Equal("Still Only Mine", guest.TitleInFile("only-mine.wav"));
        Assert.Equal(0, UploadsBegun(server));
    }

    // ── Sideways: what one device changes, the others' copies follow ─────

    // The desktop renames an album folder. The server's file moves, and then
    // so does every other device's copy that sat in the same place: the
    // laptop's own file, the phone's download.
    [Fact]
    public async Task A_move_reaches_the_other_devices_copies_that_were_filed_the_same_way()
    {
        var server = NewServer();
        var desktop = NewDevice();
        var laptop = NewDevice();
        foreach (var device in new[] { desktop, laptop })
        {
            device.AddFile("Artist/Old Album/01 Song.wav", "Song");
            device.AddFile("Artist/Other/keep.wav", "Keep");
            device.Scan();
        }
        await desktop.SyncAsync(server);
        await laptop.SyncAsync(server);
        var phone = NewDevice();
        server.CallerIsAdmin = false;
        await phone.SyncAsync(server);
        phone.AddFile("Artist/Old Album/01 Song.wav", "Song");
        phone.Single("Song").Path = phone.PathOf("Artist/Old Album/01 Song.wav");
        phone.Single("Song").IsLocallyDownloaded = true;
        server.CallerIsAdmin = true;
        var laptopsId = laptop.Single("Song").Id;

        desktop.MoveFile("Artist/Old Album/01 Song.wav", "Artist/New Album/1 - Song.wav");
        desktop.Scan();
        await desktop.SyncAsync(server);
        var summary = await laptop.SyncAsync(server);
        server.CallerIsAdmin = false;
        await phone.SyncAsync(server);
        server.CallerIsAdmin = true;

        Assert.True(File.Exists(laptop.PathOf("Artist/New Album/1 - Song.wav")));
        Assert.False(Directory.Exists(laptop.PathOf("Artist/Old Album")));
        Assert.Equal(laptop.PathOf("Artist/New Album/1 - Song.wav"), laptop.Single("Song").Path);
        Assert.Equal(laptopsId, laptop.Single("Song").Id);
        Assert.True(File.Exists(phone.PathOf("Artist/New Album/1 - Song.wav")));
        Assert.Equal(phone.PathOf("Artist/New Album/1 - Song.wav"), phone.Single("Song").Path);

        // Following a move is not making one: the laptop tells the server
        // nothing, and its next scan finds the same song where it now is.
        Assert.Equal(0, summary.Moved + summary.Uploaded);
        laptop.Scan();
        var again = await laptop.SyncAsync(server);
        Assert.Equal(0, again.Moved + again.Uploaded);
        Assert.Equal(laptopsId, laptop.Single("Song").Id);
        Assert.Equal(2, laptop.Library.Tracks.Count);
        Assert.Equal(2, server.Library.Tracks.Count);
    }

    // A copy this device has always filed somewhere else stays where its
    // owner put it: "the same move" means nothing from a different start.
    [Fact]
    public async Task A_move_does_not_touch_a_copy_that_was_filed_differently()
    {
        var server = NewServer();
        var desktop = NewDevice();
        desktop.AddFile("Artist/Album/song.wav", "Song");
        desktop.Scan();
        await desktop.SyncAsync(server);
        var laptop = NewDevice();
        laptop.AddFile("my own way/song.wav", "Song");
        laptop.Scan();
        await laptop.SyncAsync(server);

        desktop.MoveFile("Artist/Album/song.wav", "Artist/Renamed/song.wav");
        desktop.Scan();
        await desktop.SyncAsync(server);
        await laptop.SyncAsync(server);

        Assert.True(File.Exists(laptop.PathOf("my own way/song.wav")));
        Assert.Equal(laptop.PathOf("my own way/song.wav"), laptop.Single("Song").Path);
    }

    // A cover changed on the desktop: into the server's file, and from there
    // into the laptop's own copy and a guest's download - and seen by a device
    // that only streams the song.
    [Fact]
    public async Task New_artwork_reaches_the_server_and_every_other_devices_copy()
    {
        var before = SyntheticPng.Build(4, 4, red: 0x10);
        var after = SyntheticPng.Build(4, 4, red: 0xF0);
        var server = NewServer();
        var desktop = NewDevice();
        var laptop = NewDevice();
        foreach (var device in new[] { desktop, laptop })
        {
            device.AddFile("song.wav", "Song");
            Assert.True(AlbumArtWriter.TryWrite(device.PathOf("song.wav"), before, "image/png"));
            device.Scan();
        }
        await desktop.SyncAsync(server);
        await laptop.SyncAsync(server);
        var guest = NewDevice();
        server.CallerIsAdmin = false;
        await guest.SyncAsync(server);
        guest.AddFile("downloads/song.wav", "Song");
        guest.Single("Song").Path = guest.PathOf("downloads/song.wav");
        guest.Single("Song").IsLocallyDownloaded = true;
        server.CallerIsAdmin = true;
        var id = server.Library.Tracks.Single().Id;

        desktop.SetArt("Song", after);
        await desktop.SyncAsync(server);

        Assert.Equal(after, LocalAlbumArtReader.EmbeddedIn(ServerPath(server, "song.wav"))?.Bytes);
        Assert.Equal(id, server.Library.Tracks.Single().Id);
        Assert.NotNull(server.Library.Tracks.Single().ArtEditedAt);

        var begun = UploadsBegun(server);
        var first = await laptop.SyncAsync(server);
        server.CallerIsAdmin = false;
        await guest.SyncAsync(server);
        server.CallerIsAdmin = true;

        Assert.Equal(after, laptop.ArtIn("song.wav"));
        Assert.Equal(after, guest.ArtIn("downloads/song.wav"));

        // Receiving a picture is not changing one: nothing comes back up.
        var second = await laptop.SyncAsync(server);
        var third = await desktop.SyncAsync(server);
        Assert.Equal(0, first.Uploaded + second.Uploaded + third.Uploaded);
        Assert.Equal(begun, UploadsBegun(server));
        lock (server.Requests)
        {
            Assert.Single(server.Requests, r => r == $"PUT {SimulatedFlowerServer.ArtworkPath}");
        }
    }

    // A song re-ripped on the desktop: a different file under the same name.
    // It replaces the server's, and then every other copy there is - the
    // laptop's own, a guest's download - so that a song is the same file
    // wherever it is.
    [Fact]
    public async Task A_new_version_of_a_file_replaces_every_copy_of_it()
    {
        var server = NewServer();
        var desktop = NewDevice();
        var laptop = NewDevice();
        foreach (var device in new[] { desktop, laptop })
        {
            device.AddFile("song.wav", "Song", seed: 1);
            device.Scan();
        }
        await desktop.SyncAsync(server);
        await laptop.SyncAsync(server);
        var guest = NewDevice();
        server.CallerIsAdmin = false;
        await guest.SyncAsync(server);
        guest.AddFile("downloads/song.wav", "Song", seed: 1);
        guest.Single("Song").Path = guest.PathOf("downloads/song.wav");
        guest.Single("Song").IsLocallyDownloaded = true;
        server.CallerIsAdmin = true;
        var id = server.Library.Tracks.Single().Id;
        var laptopsId = laptop.Single("Song").Id;
        laptop.Single("Song").PlayCount = 6;

        desktop.AddFile("song.wav", "Song", seed: 2);
        var rerip = File.ReadAllBytes(desktop.PathOf("song.wav"));
        desktop.Scan();
        var sent = await desktop.SyncAsync(server);

        Assert.Equal(1, sent.Uploaded);
        Assert.Equal(rerip, File.ReadAllBytes(ServerPath(server, "song.wav")));
        Assert.Equal(id, server.Library.Tracks.Single().Id);

        var begun = UploadsBegun(server);
        var first = await laptop.SyncAsync(server);
        server.CallerIsAdmin = false;
        await guest.SyncAsync(server);
        server.CallerIsAdmin = true;

        Assert.Equal(rerip, File.ReadAllBytes(laptop.PathOf("song.wav")));
        Assert.Equal(rerip, File.ReadAllBytes(guest.PathOf("downloads/song.wav")));

        // The same song on the laptop, with everything it knew about it.
        Assert.Equal(laptopsId, laptop.Single("Song").Id);
        Assert.Equal(6, laptop.Single("Song").PlayCount);
        Assert.True(guest.Single("Song").IsLocallyDownloaded);

        // Being handed a file is not changing one: nothing goes back up, and
        // nobody fetches it twice.
        var second = await laptop.SyncAsync(server);
        var third = await desktop.SyncAsync(server);
        Assert.Equal(0, first.Uploaded + second.Uploaded + third.Uploaded);
        Assert.Equal(begun, UploadsBegun(server));
        lock (server.Requests)
        {
            Assert.Equal(2, server.Requests.Count(r => r == $"GET {SimulatedFlowerServer.DownloadPath}"));
        }
    }

    // A file is megabytes. Away from the server's network it waits, like an
    // upload does - and is fetched by the first sync that finds the device
    // home, even though the catalog has not changed since.
    [Fact]
    public async Task A_new_version_waits_for_the_servers_network_and_is_fetched_once_home()
    {
        var server = NewServer();
        var desktop = NewDevice();
        desktop.AddFile("song.wav", "Song", seed: 1);
        desktop.Scan();
        await desktop.SyncAsync(server);
        var phone = NewDevice();
        await phone.SyncAsync(server);
        phone.AddFile("downloads/song.wav", "Song", seed: 1);
        phone.Single("Song").Path = phone.PathOf("downloads/song.wav");
        phone.Single("Song").IsLocallyDownloaded = true;
        var old = File.ReadAllBytes(phone.PathOf("downloads/song.wav"));

        desktop.AddFile("song.wav", "Song", seed: 2);
        desktop.Scan();
        await desktop.SyncAsync(server);
        await phone.SyncAsync(server, away: true);

        Assert.Equal(old, File.ReadAllBytes(phone.PathOf("downloads/song.wav")));

        await phone.SyncAsync(server);

        Assert.Equal(File.ReadAllBytes(desktop.PathOf("song.wav")), File.ReadAllBytes(phone.PathOf("downloads/song.wav")));
    }

    // A corrected title is a corrected title. It does not make the whole file
    // a new version for every device to download.
    [Fact]
    public async Task An_edit_made_in_flower_does_not_send_the_file_or_make_anyone_fetch_it()
    {
        var server = NewServer();
        var desktop = NewDevice();
        var laptop = NewDevice();
        foreach (var device in new[] { desktop, laptop })
        {
            device.AddFile("song.wav", "Teh Song");
            device.Scan();
        }
        await desktop.SyncAsync(server);
        await laptop.SyncAsync(server);
        var begun = UploadsBegun(server);

        desktop.EditTitle("Teh Song", "The Song");
        var summary = await desktop.SyncAsync(server);
        await laptop.SyncAsync(server);

        Assert.Equal(0, summary.Uploaded);
        Assert.Equal(begun, UploadsBegun(server));
        Assert.Null(server.Library.Tracks.Single().FileReplacedAt);
        Assert.Equal("The Song", laptop.TitleInFile("song.wav"));
        lock (server.Requests)
        {
            Assert.DoesNotContain(server.Requests, r => r == $"GET {SimulatedFlowerServer.DownloadPath}");
        }
    }

    // On a phone, of a copy it downloaded: the picture goes up as a picture,
    // from anywhere, and the file - the server's, lent - does not.
    [Fact]
    public async Task Artwork_changed_on_a_phones_downloaded_copy_reaches_the_server_and_the_desktop()
    {
        var after = SyntheticPng.Build(4, 4, red: 0xF0);
        var server = NewServer();
        var desktop = NewDevice();
        desktop.AddFile("song.wav", "Song");
        desktop.Scan();
        await desktop.SyncAsync(server);
        var phone = NewDevice();
        await phone.SyncAsync(server);
        phone.AddFile("downloads/song.wav", "Song");
        phone.Single("Song").Path = phone.PathOf("downloads/song.wav");
        phone.Single("Song").IsLocallyDownloaded = true;

        phone.SetArt("Song", after);
        var summary = await phone.SyncAsync(server, away: true);
        await desktop.SyncAsync(server);

        Assert.Equal(0, summary.Uploaded);
        Assert.Equal(after, LocalAlbumArtReader.EmbeddedIn(ServerPath(server, "song.wav"))?.Bytes);
        Assert.Equal(after, desktop.ArtIn("song.wav"));
    }

    // A song both ends held before they ever met is not re-sent on the
    // strength of when its file was last written.
    [Fact]
    public async Task A_song_both_already_had_is_not_sent_just_for_being_older_or_newer()
    {
        var server = NewServer();
        ServerFile(server, "a.wav", "A", seed: 1);
        var theirs = File.ReadAllBytes(ServerPath(server, "a.wav"));
        var desktop = NewDevice();
        desktop.AddFile("a.wav", "A", seed: 2);
        desktop.Scan();

        var first = await desktop.SyncAsync(server);
        var second = await desktop.SyncAsync(server);

        Assert.Equal(0, first.Uploaded + second.Uploaded);
        Assert.Equal(theirs, File.ReadAllBytes(ServerPath(server, "a.wav")));
    }

    // ── Away from home ───────────────────────────────────────────────────

    // An album is a lot of somebody's data plan. The files wait for home
    // unless the user has said otherwise; nothing is lost by waiting.
    [Fact]
    public async Task Files_wait_for_the_servers_own_network_unless_uploading_away_from_home_is_on()
    {
        var server = NewServer();
        var phone = NewDevice();
        phone.AddFile("a.wav", "A");
        phone.Scan();

        var away = await phone.SyncAsync(server, away: true);

        Assert.Equal(0, away.Uploaded);
        Assert.Equal(1, away.Remaining);
        Assert.Equal(0, UploadsBegun(server));
        Assert.Empty(server.Library.Tracks);

        phone.Settings.UploadWhenAwayFromHome = true;
        var allowed = await phone.SyncAsync(server, away: true);

        Assert.Equal(1, allowed.Uploaded);
        Assert.Single(server.Library.Tracks);
    }

    [Fact]
    public async Task Files_that_waited_go_up_once_the_device_is_home()
    {
        var server = NewServer();
        var phone = NewDevice();
        phone.AddFile("a.wav", "A");
        phone.Scan();
        await phone.SyncAsync(server, away: true);

        var home = await phone.SyncAsync(server);

        Assert.Equal(1, home.Uploaded);
    }

    // That a song went, or moved, is a few hundred bytes and goes over anything.
    [Fact]
    public async Task A_deletion_and_a_move_are_told_from_anywhere()
    {
        var server = NewServer();
        var desktop = NewDevice();
        desktop.AddFile("drop.wav", "Drop");
        desktop.AddFile("old/move.wav", "Move");
        desktop.AddFile("keep.wav", "Keep");
        desktop.Scan();
        await desktop.SyncAsync(server);

        File.Delete(desktop.PathOf("drop.wav"));
        desktop.MoveFile("old/move.wav", "new/move.wav");
        desktop.Scan();
        var summary = await desktop.SyncAsync(server, away: true);

        Assert.Equal(1, summary.Moved);
        Assert.Equal(["Keep", "Move"], server.Library.Tracks.Select(t => t.Title).Order());
        Assert.True(File.Exists(ServerPath(server, "new/move.wav")));
    }

    // ── Date Added ───────────────────────────────────────────────────────

    [Fact]
    public async Task An_owners_older_date_added_reaches_the_server_and_from_there_every_device()
    {
        var server = NewServer();
        var theirs = ServerFile(server, "a.wav", "A");
        var scanned = theirs.DateAdded;
        var desktop = NewDevice();
        desktop.AddFile("a.wav", "A");
        desktop.Scan();
        desktop.Single("A").DateAdded = Years;

        await desktop.SyncAsync(server);

        Assert.Equal(Years, theirs.DateAdded);
        Assert.Equal(Years, desktop.Single("A").DateAdded);
        Assert.True(Years < scanned);

        var phone = NewDevice();
        server.CallerIsAdmin = false;
        await phone.SyncAsync(server);
        Assert.Equal(Years, phone.Single("A").DateAdded);
    }

    [Fact]
    public async Task The_servers_older_date_added_reaches_an_owners_device()
    {
        var server = NewServer();
        var theirs = ServerFile(server, "a.wav", "A");
        theirs.DateAdded = Years;
        var desktop = NewDevice();
        desktop.AddFile("a.wav", "A");
        desktop.Scan();

        await desktop.SyncAsync(server);

        Assert.Equal(Years, desktop.Single("A").DateAdded);
        Assert.Equal(Years, theirs.DateAdded);
    }

    // A listener's copy mirrors the library it is a guest in.
    [Fact]
    public async Task A_listeners_older_date_added_changes_nothing_on_the_server()
    {
        var server = NewServer(callerIsAdmin: false);
        var theirs = ServerFile(server, "a.wav", "A");
        var scanned = theirs.DateAdded;
        var phone = NewDevice();
        phone.AddFile("a.wav", "A");
        phone.Scan();
        phone.Single("A").DateAdded = Years;

        await phone.SyncAsync(server);

        Assert.Equal(scanned, theirs.DateAdded);
        Assert.Equal(scanned, phone.Single("A").DateAdded);
    }
}
