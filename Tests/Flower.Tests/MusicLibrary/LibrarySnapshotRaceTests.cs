using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Flower.Models;

using Xunit;

namespace Flower.Tests.MusicLibrary;

// Library.Snapshot is built outside the lock, and the library can change while
// it is being built - docs/CODE-REVIEW-2026-09.md B3. A snapshot of the
// library as it was must never outlive the change: every browse, search and
// Find(id) on the server reads through it.
public class LibrarySnapshotRaceTests
{
    // Not a deterministic repro - the window is one Build - so it is given many
    // chances. Before the fix it lost within a few dozen rounds on a library
    // this size.
    [Fact]
    public async Task A_snapshot_built_across_a_rescan_is_not_kept_after_it()
    {
        var library = new Library(Album("Before", 4000));

        for (var round = 0; round < 200; round++)
        {
            using var stop = new CancellationTokenSource();
            var reader = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                    _ = library.Snapshot;
            });

            var replacement = Album($"Round {round}", 4000);
            library.Reset(replacement);
            stop.Cancel();
            await reader;

            Assert.True(
                library.Snapshot.ById.ContainsKey(replacement[0].Id),
                $"round {round}: the snapshot still describes the library before Reset");
        }
    }

    private static List<Track> Album(string album, int count) =>
        Enumerable.Range(1, count)
            .Select(i => new Track { Id = Guid.NewGuid(), Title = $"Song {i}", Album = album, Path = $"/music/{album}/{i}.flac" })
            .ToList();
}
