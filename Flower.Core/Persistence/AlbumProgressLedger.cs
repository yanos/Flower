using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.Extensions.Logging;

using Flower.Services;

namespace Flower.Persistence
{
    // The server's copy of every listener's Continue Playing shelf - see
    // AlbumProgressProtocol for what a shelf is and whose it is. A file rather
    // than a table: a few dozen small records per listener, read and written
    // whole, with nothing to query.
    public sealed class AlbumProgressLedger
    {
        // The shelf every admin device shares. A fingerprint is hex, so it can
        // never collide with this.
        public const string OwnerShelf = "owner";

        private readonly ILogger<AlbumProgressLedger> _logger;
        private readonly Func<DateTimeOffset> _now;
        private readonly object _lock = new();
        private Dictionary<string, List<AlbumProgressDto>>? _shelves;

        public AlbumProgressLedger(ILogger<AlbumProgressLedger> logger, Func<DateTimeOffset>? now = null)
        {
            _logger = logger;
            _now = now ?? (() => DateTimeOffset.UtcNow);
        }

        public static string StorePath => Path.Combine(AppDataDirectory.Path, "album-progress-shelves.json");

        // Merges what a device holds into its listener's shelf, keeps the
        // result, and answers with it. Synchronous and under one lock: two
        // devices of one listener exchanging at once must not each save a
        // shelf built without the other's.
        public List<AlbumProgressDto> Exchange(string shelf, IReadOnlyList<AlbumProgressDto> incoming)
        {
            lock (_lock)
            {
                _shelves ??= AtomicJsonFile.Read(StorePath, FlowerCoreJsonContext.Default.AlbumProgressShelves, _logger)
                    ?? new Dictionary<string, List<AlbumProgressDto>>();

                var stored = _shelves.TryGetValue(shelf, out var existing) ? existing : [];
                var merged = AlbumProgressMerge.Prune(
                    AlbumProgressMerge.Newest(stored, incoming, a => a.AlbumId, a => a.UpdatedAt),
                    _now());

                _shelves[shelf] = merged;
                AtomicJsonFile.Write(StorePath, _shelves, FlowerCoreJsonContext.Default.AlbumProgressShelves);
                return merged;
            }
        }
    }
}
