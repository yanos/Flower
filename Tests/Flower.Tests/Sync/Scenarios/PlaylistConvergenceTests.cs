using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence;
using Flower.Persistence.Sql;
using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests.Sync.Scenarios;

// The main defence against edge cases nobody has thought of yet
// (docs/PLAYLIST-SYNC-PLAN.md, "Testing"): three devices and a server, and a
// seeded run of random edits, syncs, failed pushes, lost answers and backup
// restores, with every prompt answered at random. Then everything settles, and
// two things must hold:
//
//   - every device holds exactly the server's playlists, row for row;
//   - no song has left a playlist unless some device removed it, deleted the
//     playlist, or a person picked the other version over it.
//
// A failure names its seed; FLOWER_CONVERGENCE_SEED reruns one.
[Collection("PlatformDataDirectory")]
public class PlaylistConvergenceTests : PinnedDataDirectory
{
    private const int Steps = 60;

    public static IEnumerable<object[]> Seeds() =>
        Environment.GetEnvironmentVariable("FLOWER_CONVERGENCE_SEED") is { Length: > 0 } one
            ? [[int.Parse(one)]]
            : Enumerable.Range(1, 12).Select(seed => new object[] { seed });

    private static readonly string[] Songs = Enumerable.Range(1, 24).Select(i => $"Song {i:00}").ToArray();

    private static Track Song(string title, string path) => new()
    {
        Title = title,
        Artists = "Artist",
        Album = "Album",
        Duration = TimeSpan.FromSeconds(200),
        Path = path,
    };

    // One device: its own library holding every song, its own flower.db for
    // the agreed copies, and its sync service.
    private sealed class Device : IDisposable
    {
        private readonly DeviceSigningKey _key = TestSigningKey.Create();

        public string Name { get; }
        public Library Library { get; }
        public PlaylistSyncService Sync { get; }

        public Device(string name, string directory)
        {
            Name = name;
            Library = new Library(Songs.Select(s => Song(s, $"/{name}/{s}.m4a")).ToList());
            var identity = new DeviceIdentity { Fingerprint = _key.Fingerprint, Alias = name };
            Sync = new PlaylistSyncService(
                Library, identity, _key, new AppSettings(),
                new PlaylistAgreementRepository(new FlowerDb(Path.Combine(directory, $"{name}.db"))),
                new DeviceNicknameStore(NullLogger<DeviceNicknameStore>.Instance),
                NullLogger<PlaylistSyncService>.Instance);
        }

        public Track Track(string title) => Library.Tracks.Single(t => t.Title == title);

        public void Dispose() => _key.Dispose();
    }

    // What the run did to songs in playlists, by (playlist, song) rather than
    // by row id: copies that agree on songs at the same place share the
    // server's row ids from then on, so a row can change id without anyone
    // touching it.
    private sealed class Ledger
    {
        public HashSet<(Guid Playlist, string Song)> Added { get; } = [];
        public HashSet<(Guid Playlist, string Song)> Removed { get; } = [];

        public void RemoveAll(Guid playlist, IEnumerable<string> songs)
        {
            foreach (var song in songs)
                Removed.Add((playlist, song));
        }
    }

    private static IEnumerable<string> SongsOf(IEnumerable<PlaylistSyncTrackDto> rows) => rows.Select(r => r.Title ?? "");

    private static IEnumerable<string> SongsOf(Playlist playlist) => playlist.Entries.Select(e => e.ToWire().Title ?? "");

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Random_edits_syncs_failures_and_restores_converge_without_losing_a_song(int seed)
    {
        var random = new Random(seed);
        using var server = new SimulatedFlowerServer(Songs.Select(s => Song(s, $"/srv/{s}.flac")));
        var devices = new[] { "phone", "tablet", "laptop" }.Select(n => Own(new Device(n, DataDirectory))).ToArray();
        var ledger = new Ledger();
        var backups = new List<SimulatedFlowerServer.ServerBackup> { server.TakeBackup() };
        var log = new List<string>();

        foreach (var device in devices)
        {
            device.Sync.ConflictDetected += (_, e) =>
            {
                // Picked at random during the run, the server's copy once it
                // settles. Whatever the side not picked held that the picked
                // one does not is gone by a person's choice.
                var choice = settling ? PlaylistConflictChoice.KeepRemote
                    : (PlaylistConflictChoice)random.Next(1, 4);
                var mine = SongsOf(e.Local).ToList();
                var theirs = e.Remote == null ? [] : SongsOf(e.Remote.Tracks).ToList();
                if (choice == PlaylistConflictChoice.KeepRemote)
                    ledger.RemoveAll(e.Local.Id, mine.Except(theirs));
                else if (choice == PlaylistConflictChoice.KeepLocal)
                    ledger.RemoveAll(e.Local.Id, theirs.Except(mine));
                log.Add($"{device.Name} asked about {e.Local.Name}{(e.DeletedOnServer ? " (deleted)" : "")}: {choice}");
                e.Resolution.SetResult(choice);
            };
        }

        for (var step = 0; step < Steps; step++)
        {
            var device = devices[random.Next(devices.Length)];
            var playlists = device.Library.Playlists;
            var playlist = playlists.Count > 0 ? playlists[random.Next(playlists.Count)] : null;
            switch (random.Next(14))
            {
                case 0 or 1:
                {
                    var songs = Enumerable.Range(0, random.Next(1, 4)).Select(_ => Songs[random.Next(Songs.Length)]).ToList();
                    var made = new Playlist($"{device.Name} {step}", songs.Select(device.Track).ToList());
                    device.Library.AddPlaylist(made);
                    foreach (var song in songs)
                        ledger.Added.Add((made.Id, song));
                    log.Add($"{device.Name} made {made.Name}");
                    break;
                }
                case 2 when playlist != null:
                {
                    var song = Songs[random.Next(Songs.Length)];
                    playlist.AppendTrack(device.Track(song));
                    ledger.Added.Add((playlist.Id, song));
                    log.Add($"{device.Name} added {song} to {playlist.Name}");
                    break;
                }
                case 3 when playlist is { Entries.Count: > 0 }:
                {
                    var at = random.Next(playlist.Entries.Count);
                    var song = SongsOf(playlist).ElementAt(at);
                    playlist.RemoveEntriesAt([at]);
                    ledger.Removed.Add((playlist.Id, song));
                    log.Add($"{device.Name} removed {song} from {playlist.Name}");
                    break;
                }
                case 4 when playlist is { Entries.Count: > 1 }:
                    playlist.MoveEntries([random.Next(playlist.Entries.Count)], random.Next(playlist.Entries.Count + 1));
                    log.Add($"{device.Name} reordered {playlist.Name}");
                    break;
                case 5 when playlist != null:
                    playlist.Name = $"{playlist.Name.Split(" (")[0]} ({device.Name} {step})";
                    log.Add($"{device.Name} renamed {playlist.Id:N} to {playlist.Name}");
                    break;
                case 6 when playlist != null:
                    ledger.RemoveAll(playlist.Id, SongsOf(playlist));
                    device.Library.RemovePlaylist(playlist);
                    log.Add($"{device.Name} deleted {playlist.Name}");
                    break;
                case 7:
                    server.FailsPlaylistChanges = true;
                    await device.Sync.SyncWithAsync(server.Device);
                    server.FailsPlaylistChanges = false;
                    log.Add($"{device.Name} synced, push failed");
                    break;
                case 8:
                    server.LosesNextChangesAnswer = true;
                    await device.Sync.SyncWithAsync(server.Device);
                    server.LosesNextChangesAnswer = false;
                    log.Add($"{device.Name} synced, answer lost");
                    break;
                case 9 when random.Next(3) == 0:
                    server.Restore(backups[random.Next(backups.Count)]);
                    log.Add("server restored from a backup");
                    break;
                case 10:
                    backups.Add(server.TakeBackup());
                    log.Add("server backed up");
                    break;
                // Another device edits and syncs between this device's read
                // and its write.
                case 11:
                {
                    var other = devices[(Array.IndexOf(devices, device) + 1 + random.Next(devices.Length - 1)) % devices.Length];
                    server.BeforeNextChanges = async () =>
                    {
                        if (other.Library.Playlists is { Count: > 0 } held)
                        {
                            var target = held[random.Next(held.Count)];
                            var song = Songs[random.Next(Songs.Length)];
                            target.AppendTrack(other.Track(song));
                            ledger.Added.Add((target.Id, song));
                            log.Add($"  meanwhile, {other.Name} added {song} to {target.Name} and synced");
                        }
                        await other.Sync.SyncWithAsync(server.Device);
                    };
                    await device.Sync.SyncWithAsync(server.Device);
                    server.BeforeNextChanges = null;
                    log.Add($"{device.Name} synced, with another device's sync landing first");
                    break;
                }
                // This device's user edits between its read and its install.
                case 12 when playlist != null:
                    server.AfterNextPlaylistsGet = () =>
                    {
                        var song = Songs[random.Next(Songs.Length)];
                        playlist.AppendTrack(device.Track(song));
                        ledger.Added.Add((playlist.Id, song));
                        log.Add($"  meanwhile, {song} added to {playlist.Name} on {device.Name}");
                    };
                    await device.Sync.SyncWithAsync(server.Device);
                    server.AfterNextPlaylistsGet = null;
                    log.Add($"{device.Name} synced, with an edit landing mid-session");
                    break;
                default:
                    await device.Sync.SyncWithAsync(server.Device);
                    log.Add($"{device.Name} synced");
                    break;
            }
        }

        settling = true;
        for (var round = 0; round < 4; round++)
        {
            foreach (var device in devices)
                await device.Sync.SyncWithAsync(server.Device);
        }

        var trace = $"seed {seed}:\n  " + string.Join("\n  ", log);
        var held = server.Library.Playlists.ToDictionary(p => p.Id);
        foreach (var device in devices)
        {
            Assert.True(
                Shape(device.Library.Playlists) == Shape(server.Library.Playlists),
                $"{device.Name} does not hold the server's playlists\n  device: {Shape(device.Library.Playlists)}\n  server: {Shape(server.Library.Playlists)}\n{trace}");
        }

        foreach (var (playlist, song) in ledger.Added.Except(ledger.Removed))
        {
            Assert.True(
                held.TryGetValue(playlist, out var kept) && SongsOf(kept).Contains(song),
                $"{song} left playlist {playlist:N} without anyone removing it\n{trace}");
        }
    }

    private bool settling;

    private static string Shape(IEnumerable<Playlist> playlists) =>
        string.Join(" | ", playlists
            .OrderBy(p => p.Id)
            .Select(p => $"{p.Name}: {string.Join(",", p.Entries.Select(e => $"{e.EntryId.ToString()[..4]}={e.ToWire().Title}"))}"));
}
