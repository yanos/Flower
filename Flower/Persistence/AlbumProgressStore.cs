using System.IO;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Services;

namespace Flower.Persistence
{
    // Where each album the user is part-way through was left - the Home
    // screen's "Continue Playing" shelf, see AlbumProgressTracker. A file of
    // its own rather than a field on AppSettings: it is rewritten on every
    // pause and every song change, and settings.json is not something that
    // should be rewritten that often for a reason that has nothing to do with
    // settings.
    //
    // This device's copy. The listener's other devices are kept in step
    // through the server - see AlbumProgressSyncService - and this file is
    // what the shelf is between launches, and while there is no server.
    public class AlbumProgressStore
    {
        private readonly ILogger<AlbumProgressStore> _logger;

        public AlbumProgressStore(ILogger<AlbumProgressStore> logger)
        {
            _logger = logger;
        }

        public static string StorePath => Path.Combine(AppDataDirectory.Path, "album-progress.json");

        public AlbumProgressState? Load() =>
            AtomicJsonFile.Read(StorePath, FlowerJsonContext.Default.AlbumProgressState, _logger);

        // Synchronous for the one caller with no later to defer to: the app
        // going away (see PlaylistControlViewModel.SavePlaybackState).
        public void Save(AlbumProgressState state) =>
            AtomicJsonFile.Write(StorePath, state, FlowerJsonContext.Default.AlbumProgressState);

        public Task SaveAsync(AlbumProgressState state) =>
            AtomicJsonFile.WriteAsync(StorePath, state, FlowerJsonContext.Default.AlbumProgressState);
    }
}
