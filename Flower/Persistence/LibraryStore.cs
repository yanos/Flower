using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Models;

using Flower.Persistence.Sql;

namespace Flower.Persistence
{
    // The library's persistence, backed by SQLite (see Flower.Core's
    // Persistence/Sql/) rather than a single library.json document - see
    // docs/ARCHITECTURE-REVIEW.md Tier 4.1.
    //
    // What is no longer here is the stats write, debounce and all. It was
    // written when a stats bump meant re-serializing every track on disk, and
    // it outlived that: the write is one indexed UPDATE of one row, so the
    // coalescing was left buying nothing and costing the two things a deferred
    // write always costs - a window in which a crash loses the increment, and
    // a flush-on-exit hook to shrink that window. Two real defects came out of
    // it: Flush() was never actually called from anywhere (MainWindow's
    // Closing handler saved the *whole library* instead - 16k upserts on every
    // quit to push at most two changed rows), and iOS/Android have no such
    // hook at all, so a backgrounded phone dropped whatever was in the window.
    //
    // Removing the window was the first half; the second was noticing that
    // asking a caller to mutate the library and then remember to save it is
    // the same structural problem again, and that it was not confined to the
    // stats path - a rescan, a sync merge, an iTunes import, a finished
    // download and a tag edit each ended with their own hand-written save.
    // Half of them persisted one changed row by rewriting all 16k. Library
    // issues every one of those writes itself now, through the
    // TrackRepository handed to it as an ITrackStore - the same registration
    // Flower.Server uses, so the two hosts persist a library change through
    // exactly the same code.
    //
    // What is left here is the read, and only the read: it is the one part
    // that is genuinely the client's own policy, because a corrupt or
    // unreadable database has to degrade to an empty library and let the app
    // start, where the server would rather fail to boot.
    public class LibraryStore
    {
        private readonly ILogger<LibraryStore> _logger;
        private readonly TrackRepository _tracks;

        public LibraryStore(ILogger<LibraryStore> logger) : this(logger, FlowerDb.OpenDefault())
        {
        }

        public LibraryStore(ILogger<LibraryStore> logger, FlowerDb db)
        {
            _logger = logger;
            _tracks = new TrackRepository(db);
        }

        // The app-data directory the database lives in - MainViewModel uses
        // this to open the containing folder.
        public static string StorePath => FlowerDb.DefaultPath;

        public List<Track> Load()
        {
            try
            {
                return _tracks.LoadAll();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load the library from {Path}; starting with an empty library", StorePath);
                return [];
            }
        }

        public Task<List<Track>> LoadAsync() => Task.FromResult(Load());
    }
}
