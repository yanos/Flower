using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Models;

namespace Flower.Services;

// Keeps a library in step with its folders while the app or the server is
// running, where it used to be read only at launch and by a manual rescan: a
// song copied into a library folder (or dropped on the desktop window - see
// LibraryDrop) appears without either.
//
// What changed is collected until the folders have been quiet for a moment -
// a copy of an album is dozens of events, and a large file is written for
// seconds - and then dealt with in one go, in one of two ways:
//
//   - Files that are there: each is read and added on its own
//     (Library.AddScannedTrack), which is a few milliseconds a song where a
//     scan of everything is seconds for a large library and minutes for a NAS
//     share. A file already in the library is read again, keeping its plays,
//     star and Date Added - the same carry-forward a rescan gives it - which
//     is how an edit made in another program's tag editor shows up. A folder
//     that appears (moved in whole) is walked for the files in it.
//
//   - A song that is gone: a full rescan, the same one a manual rescan runs.
//     A deletion and a move are the cases a rescan has all its care for -
//     recognising a moved song as the song it was, and not reading an
//     unplugged drive as a deletion (Library.UpdateTracks,
//     LibraryMirrorService.ReportVanishedAsync) - and doing them one file at a
//     time would be a second, weaker copy of that. Whether the library still
//     has a song there is asked when the pass runs, not when the change was
//     seen: a server moves, replaces and trashes files in its own folders
//     (LibraryIngest), and by then it has already told the library, so its own
//     work never costs it a rescan. A watcher that has lost track (its buffer
//     overflowed) rescans too.
//
// Change notifications are not the whole story, because a network share sends
// none for what another machine does to it - and a server reading a NAS share
// is the ordinary deployment. So every PollInterval the folders are also
// listed (names only, no tags read) and compared with the library: a file it
// lacks is noted as if it had been seen arriving, and a song whose file is not
// there as if it had been seen going. A folder that lists nothing at all is
// left out of the second half - an unmounted share lists nothing.
public sealed class LibraryFolderWatcher : IDisposable
{
    public static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMinutes(5);

    // However busy the folders stay, what has arrived so far is dealt with
    // this often - so a long copy of many albums fills in as it goes rather
    // than all at the end.
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(15);

    private readonly Library _library;
    private readonly Importer.Importer _importer;
    private readonly Func<Task> _rescanAll;
    private readonly Func<Task> _scanInFlight;
    private readonly ILogger<LibraryFolderWatcher> _logger;
    private readonly TimeSpan _quietPeriod;
    private readonly TimeSpan _pollInterval;
    private readonly bool _listensForChanges;

    private readonly object _lock = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private List<string> _roots = [];
    private HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private bool _pendingRescan;
    private DateTime? _firstPendingAt;
    private readonly Timer _flushTimer;
    private readonly Timer _pollTimer;

    // One pass at a time: a rescan racing an import, or two rescans, would
    // each write the library from what they saw.
    private readonly SemaphoreSlim _passGate = new(1, 1);

    // rescanAll is the host's full rescan. scanInFlight, where the host has
    // one, is a scan already running that a pass must wait for rather than
    // race - the server's rescan coordinator; a scan that read the folders
    // before a file arrived would otherwise write the library back without it.
    // A pollInterval of zero turns the listing off.
    public LibraryFolderWatcher(
        Library library, Func<Task> rescanAll, ILogger<LibraryFolderWatcher> logger,
        ILogger<Importer.Importer> importerLogger, TimeSpan? quietPeriod = null,
        Func<Task>? scanInFlight = null, TimeSpan? pollInterval = null)
        : this(library, rescanAll, logger, importerLogger, quietPeriod, scanInFlight, pollInterval, listensForChanges: true)
    {
    }

    // listensForChanges false leaves out the operating system's notifications
    // and the timers, for a test that drives Note, FlushAsync and PollAsync
    // itself and must not have real events arriving in between.
    internal LibraryFolderWatcher(
        Library library, Func<Task> rescanAll, ILogger<LibraryFolderWatcher> logger,
        ILogger<Importer.Importer> importerLogger, TimeSpan? quietPeriod,
        Func<Task>? scanInFlight, TimeSpan? pollInterval, bool listensForChanges)
    {
        _listensForChanges = listensForChanges;
        _library = library;
        _importer = new Importer.Importer(importerLogger);
        _rescanAll = rescanAll;
        _scanInFlight = scanInFlight ?? (() => Task.CompletedTask);
        _logger = logger;
        _quietPeriod = quietPeriod ?? DefaultQuietPeriod;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _flushTimer = new Timer(_ => RunInBackground(FlushAsync, "Library folder changes"));
        _pollTimer = new Timer(_ => RunInBackground(PollAsync, "Listing the library folders"));
    }

    // Watches exactly the folders a scan walks (Importer.ScanRoots), replacing
    // whatever was watched before - called once the startup scan is done, and
    // again whenever the folder list may have changed. The same folders again
    // leave the watchers as they are, so nothing arriving in between is missed.
    public void Watch(IEnumerable<string> libraryPaths)
    {
        var roots = Importer.Importer.ScanRoots(libraryPaths);
        lock (_lock)
        {
            if (roots.SequenceEqual(_roots, StringComparer.OrdinalIgnoreCase) && _watchers.Count > 0)
                return;

            StopWatching();
            _roots = roots;
            if (!_listensForChanges)
                return;
            foreach (var root in roots)
            {
                try
                {
                    var watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                       | NotifyFilters.LastWrite | NotifyFilters.Size,
                    };
                    watcher.Created += (_, e) => Note(e.FullPath);
                    watcher.Changed += (_, e) => Note(e.FullPath);
                    watcher.Deleted += (_, e) => Note(e.FullPath);
                    watcher.Renamed += (_, e) =>
                    {
                        Note(e.OldFullPath);
                        Note(e.FullPath);
                    };
                    watcher.Error += (_, e) =>
                    {
                        _logger.LogWarning(e.GetException(), "Lost track of changes in {Root} - rescanning", root);
                        NoteRescan();
                    };
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch (Exception ex)
                {
                    // Still listed every PollInterval, and scanned by a rescan
                    // as it always was.
                    _logger.LogWarning(ex, "Cannot watch library folder {Root} for changes", root);
                }
            }

            if (_pollInterval > TimeSpan.Zero)
                _pollTimer.Change(_pollInterval, _pollInterval);
        }

        _logger.LogInformation("Watching {Count} of {Total} library folder(s) for changes, and listing them every {Interval}",
            _watchers.Count, roots.Count, _pollInterval);
    }

    // One path the file system says has changed - a file or a folder, there
    // or gone. Anything a scan would not take is dropped here, so the busy
    // bookkeeping of other programs sharing the folder (Music.app's database,
    // a .DS_Store, a trash) never starts a pass.
    internal void Note(string path)
    {
        string? root;
        lock (_lock)
            root = _roots.FirstOrDefault(r => IsUnder(r, path));
        if (root == null || Importer.Importer.IsUnderHiddenFolder(root, path + Path.DirectorySeparatorChar))
            return;

        if (File.Exists(path) && !Importer.Importer.IsImportable(path))
            return;
        // Gone: worth anything only if the library had a song there, or in it
        // - a folder's name says nothing about whether it held music, and a
        // temporary file coming and going says nothing at all. Asked again
        // when the pass runs (see FlushAsync).
        if (!File.Exists(path) && !Directory.Exists(path) && !HadSongsAt(path))
            return;

        lock (_lock)
        {
            _pending.Add(path);
            if (_listensForChanges)
                Schedule();
        }
    }

    private void NoteRescan()
    {
        lock (_lock)
        {
            _pendingRescan = true;
            Schedule();
        }
    }

    // Called under _lock.
    private void Schedule()
    {
        var now = DateTime.UtcNow;
        _firstPendingAt ??= now;
        var due = _quietPeriod;
        var overdue = _firstPendingAt.Value + MaxDelay - now;
        if (overdue < due)
            due = overdue < TimeSpan.Zero ? TimeSpan.Zero : overdue;
        _flushTimer.Change(due, Timeout.InfiniteTimeSpan);
    }

    // Deals with everything noted so far, now. The timer's work, and a
    // test's.
    internal async Task FlushAsync()
    {
        HashSet<string> paths;
        bool rescan;
        lock (_lock)
        {
            paths = _pending;
            rescan = _pendingRescan;
            _pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _pendingRescan = false;
            _firstPendingAt = null;
        }
        if (paths.Count == 0 && !rescan)
            return;

        await _passGate.WaitAsync();
        try
        {
            await _scanInFlight();

            // Still a song the library has, at a path that is not there.
            var gone = paths.Where(p => !File.Exists(p) && !Directory.Exists(p) && HadSongsAt(p)).ToList();
            if (rescan || gone.Count > 0)
            {
                _logger.LogInformation("Library folders changed ({Count} song path(s) gone, e.g. {Example}) - rescanning",
                    gone.Count, gone.FirstOrDefault());
                await _rescanAll();
                return;
            }

            await Task.Run(() => AddFiles(paths.SelectMany(FilesAt)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not bring the library up to date with its folders");
        }
        finally
        {
            _passGate.Release();
        }
    }

    // The folders listed and compared with the library - what stands in for
    // change notifications a network share never sends. See this class's
    // comment.
    internal async Task PollAsync()
    {
        if (!_scanInFlight().IsCompleted)
            return;

        List<string> roots;
        lock (_lock)
            roots = [.. _roots];

        var known = _library.Tracks
            .Where(t => t.Path != null)
            .Select(t => t.Path!)
            .ToHashSet(_library.PathRules);

        var arrived = new List<string>();
        var vanished = new List<string>();
        await Task.Run(() =>
        {
            foreach (var root in roots)
            {
                if (!Directory.Exists(root))
                    continue;

                var onDisk = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Where(Importer.Importer.IsImportable)
                    .Where(f => !Importer.Importer.IsUnderHiddenFolder(root, f))
                    .ToHashSet(_library.PathRules);
                arrived.AddRange(onDisk.Where(f => !known.Contains(f) && !_library.IsExcludedPath(f)));

                if (onDisk.Count > 0)
                    vanished.AddRange(known.Where(p => IsUnder(root, p) && !onDisk.Contains(p)));
            }
        });

        if (arrived.Count == 0 && vanished.Count == 0)
            return;

        _logger.LogInformation("Listing the library folders found {Arrived} new file(s) and {Vanished} missing song(s)",
            arrived.Count, vanished.Count);
        foreach (var path in arrived.Concat(vanished))
            Note(path);
    }

    // Reads each of these files into the library now, rather than waiting for
    // the watcher to see them - what a drop on the window does once it has
    // copied them (LibraryDrop). Returns how many were added or updated.
    public async Task<int> AddFilesAsync(IReadOnlyCollection<string> files)
    {
        await _passGate.WaitAsync();
        try
        {
            await _scanInFlight();
            return await Task.Run(() => AddFiles(files));
        }
        finally
        {
            _passGate.Release();
        }
    }

    private int AddFiles(IEnumerable<string> files)
    {
        var added = 0;
        foreach (var file in files.Distinct(_library.PathRules))
        {
            // A song removed from the library on purpose stays out, whatever
            // happens to its file - see Library.RemoveTracks.
            if (_library.IsExcludedPath(file))
                continue;
            // Null for a file still being written, among other things: the
            // change that finishes it is noted again.
            if (_importer.ImportFile(file) is not { } track)
                continue;
            _library.AddScannedTrack(track);
            added++;
        }

        if (added > 0)
            _logger.LogInformation("Added or updated {Count} song(s) from the library folders", added);
        return added;
    }

    private IEnumerable<string> FilesAt(string path)
    {
        if (File.Exists(path))
            return [path];
        if (!Directory.Exists(path))
            return [];

        string? root;
        lock (_lock)
            root = _roots.FirstOrDefault(r => IsUnder(r, path));
        return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(Importer.Importer.IsImportable)
            .Where(f => root == null || !Importer.Importer.IsUnderHiddenFolder(root, f));
    }

    private bool HadSongsAt(string path) =>
        _library.Tracks.Any(t => t.Path != null && IsUnder(path, t.Path));

    // Whether path is root or anywhere below it.
    public static bool IsUnder(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                                && !Path.IsPathRooted(relative);
    }

    private void RunInBackground(Func<Task> work, string description)
    {
        work().ContinueWith(
            t => _logger.LogError(t.Exception, "{Description} failed", description),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    // Called under _lock.
    private void StopWatching()
    {
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
    }

    public void Dispose()
    {
        lock (_lock)
            StopWatching();
        _flushTimer.Dispose();
        _pollTimer.Dispose();
    }
}
