using Flower.Models;
using Flower.Server.Endpoints;
using Flower.Services;

namespace Flower.Server.Services;

// Cover art as the server last found it, for the single route and the batch
// one. Finding it is the expensive part: an album id means trying each of its
// songs' tags with TagLib and listing their folder for a cover.* - over a NAS
// share, on the reference deployment - and every request, and each of up to 32
// ids in a batch, used to do all of it again. An album with no art did the
// most work of all, to arrive at a 404 nobody remembered.
//
// So each id keeps what was found - the picture, or that there was nothing -
// along with every file and folder the search went through to find it
// (LocalAlbumArtReader.FirstFor's consulted): the songs whose tags had no
// picture, the folders listed for a cover.*, and the file the picture came
// from. Before the answer is used again each of those is checked to be the
// size and age it was, which is a stat apiece rather than a parse and a
// listing. So a cover replaced, added or deleted on disk behind Flower's back
// is noticed at the next request: replacing one changes its own file, and
// adding, removing or renaming one changes its folder's modified time. A
// folder that was never listed - because a song before it had a picture in
// its tags, which wins - cannot change the answer, and is not checked.
//
// Anything that can move art in the library - a rescan, an artwork or tag
// edit, a replaced file - also forgets everything, because those are rare and
// an album's art can move between songs with them. A play, which is most of
// what changes, forgets nothing: the library's change token moves on every
// play, which is why this is not keyed on it.
//
// The pictures are held up to MaxBytes, least recently asked for going first;
// the reference library's covers would not fit in memory, and a few devices'
// cold scrolls do not need them to.
public sealed class CoverArtCache : IDisposable
{
    public const long MaxBytes = 64L * 1024 * 1024;

    private readonly Library _library;
    private readonly ILogger<CoverArtCache> _logger;

    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _recency = new();
    private long _bytes;

    // Art is null for an id with no art. Consulted is what the search went
    // through, each as it was just after the search.
    private sealed record Entry(string Id, LocalAlbumArt? Art, Stamp[] Consulted)
    {
        public long Bytes => Art?.Bytes.LongLength ?? 0;
    }

    // A file's age and size, or a folder's age (Length -1), or that the path
    // was not there (Length -2) - so one appearing is a change too.
    private readonly record struct Stamp(string Path, DateTime WrittenAt, long Length)
    {
        public static Stamp Of(string path)
        {
            try
            {
                var file = new FileInfo(path);
                if (file.Exists)
                    return new Stamp(path, file.LastWriteTimeUtc, file.Length);

                var folder = new DirectoryInfo(path);
                if (folder.Exists)
                    return new Stamp(path, folder.LastWriteTimeUtc, -1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Read as gone. If it reads the same way next time the answer
                // stands, and if not it is looked for again.
            }

            return new Stamp(path, DateTime.MinValue, -2);
        }
    }

    public CoverArtCache(Library library, ILogger<CoverArtCache> logger)
    {
        _library = library;
        _logger = logger;
        _library.LibraryChanged += OnLibraryChanged;
        _library.TrackChanged += OnTrackChanged;
    }

    public void Dispose()
    {
        _library.LibraryChanged -= OnLibraryChanged;
        _library.TrackChanged -= OnTrackChanged;
    }

    public int Count
    {
        get
        {
            lock (_lock)
                return _entries.Count;
        }
    }

    public long HeldBytes
    {
        get
        {
            lock (_lock)
                return _bytes;
        }
    }

    // This id's art, or null when none of its songs has any.
    public LocalAlbumArt? Find(string id)
    {
        Entry? known = null;
        lock (_lock)
        {
            if (_entries.TryGetValue(id, out var node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                known = node.Value;
            }
        }

        // The check is a stat of a file, outside the lock.
        if (known != null && StillGood(known))
            return known.Art;

        var found = Look(id);
        Remember(found);
        return found.Art;
    }

    private static bool StillGood(Entry entry)
    {
        foreach (var stamp in entry.Consulted)
        {
            if (Stamp.Of(stamp.Path) != stamp)
                return false;
        }

        return true;
    }

    private Entry Look(string id)
    {
        var consulted = new List<string>();
        var paths = MediaEndpoints.CoverArtCandidates(id, _library).Select(t => t.Path);
        var found = LocalAlbumArtReader.FirstFor(paths, _logger, consulted);

        // Stamped after the search, so a file written while it was being read
        // is found changed next time rather than believed.
        return new Entry(id, found?.Art, consulted.Select(Stamp.Of).ToArray());
    }

    private void Remember(Entry entry)
    {
        // A picture bigger than the whole budget is served and not kept.
        if (entry.Bytes > MaxBytes)
            return;

        lock (_lock)
        {
            if (_entries.Remove(entry.Id, out var stale))
            {
                _recency.Remove(stale);
                _bytes -= stale.Value.Bytes;
            }

            _entries[entry.Id] = _recency.AddFirst(entry);
            _bytes += entry.Bytes;

            while (_bytes > MaxBytes && _recency.Last is { } oldest)
            {
                _recency.RemoveLast();
                _entries.Remove(oldest.Value.Id);
                _bytes -= oldest.Value.Bytes;
            }
        }
    }

    private void Forget()
    {
        lock (_lock)
        {
            _entries.Clear();
            _recency.Clear();
            _bytes = 0;
        }
    }

    private void OnLibraryChanged(object? sender, EventArgs e) => Forget();

    private void OnTrackChanged(object? sender, TrackChangedEventArgs e)
    {
        if (e.Reshapes)
            Forget();
    }
}
