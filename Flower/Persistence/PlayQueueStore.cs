using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Services;

namespace Flower.Persistence
{
    // The queue as it was left: its songs by id, the slot of the one playing,
    // and how far into it playback had got. A phone puts a paused app away and
    // kills it whenever it likes, and without this every one of those launches
    // came back to an empty queue - see PlaylistControlViewModel.RestoreQueue.
    public sealed record PlayQueueState(List<Guid> TrackIds, int Index, long PositionMs, int LinedUpCount);

    // A file of its own, like AlbumProgressStore and for the same reason: it
    // is rewritten on every song change, which settings.json should not be.
    public class PlayQueueStore
    {
        private readonly ILogger<PlayQueueStore> _logger;

        public PlayQueueStore(ILogger<PlayQueueStore> logger)
        {
            _logger = logger;
        }

        public static string StorePath => Path.Combine(AppDataDirectory.Path, "queue.json");

        public PlayQueueState? Load() =>
            AtomicJsonFile.Read(StorePath, FlowerJsonContext.Default.PlayQueueState, _logger);

        // Synchronous for the app going away, as AlbumProgressStore.Save is.
        public void Save(PlayQueueState state) =>
            AtomicJsonFile.Write(StorePath, state, FlowerJsonContext.Default.PlayQueueState);

        public Task SaveAsync(PlayQueueState state) =>
            AtomicJsonFile.WriteAsync(StorePath, state, FlowerJsonContext.Default.PlayQueueState);
    }
}
