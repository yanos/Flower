using Flower.Services;

namespace Flower.Server.Services;

// Which of the library folders this server cannot write to, as of the last
// scan. A music folder mounted read-only - or owned by a user this process is
// not - is an ordinary way to run a server and everything a listener does
// still works on one. What stops is everything that changes a file: an upload
// from an owner's device, a tag or cover edit, a move, Delete Files. Each of
// those already refuses politely (LibraryIngest's CannotWrite), but only at
// the moment somebody tries, on a phone, with nothing on the server to say the
// refusal was coming. This says it ahead of time, in the two places an owner
// looks: the log, and the admin's browser page (GET /api/admin/library carries
// the list).
//
// Checked at the start of each rescan rather than on a timer or per request:
// a rescan runs at startup and after every change to the folder list, which
// are the two moments the answer can have changed without anybody touching the
// machine, and the probe creates and deletes a file - not something to do on a
// route a page polls.
public sealed class LibraryWriteAccess(ILogger<LibraryWriteAccess> logger)
{
    private readonly object _lock = new();
    private IReadOnlyList<string> _unwritable = [];
    private bool _checked;

    public IReadOnlyList<string> UnwritableFolders
    {
        get
        {
            lock (_lock)
                return _unwritable;
        }
    }

    public void Check(IEnumerable<string> libraryPaths)
    {
        var unwritable = LibraryFolders.Unwritable(libraryPaths);

        bool changed;
        bool wasUnwritable;
        lock (_lock)
        {
            changed = !_checked || !unwritable.SequenceEqual(_unwritable, StringComparer.Ordinal);
            wasUnwritable = _unwritable.Count > 0;
            _unwritable = unwritable;
            _checked = true;
        }

        // Once per change, not once per scan: a server left read-only on
        // purpose would otherwise say so again at every rescan for ever.
        if (!changed)
            return;

        if (unwritable.Count > 0)
        {
            logger.LogWarning(
                "Cannot write to the library folder(s) {Folders} - mounted read-only, or not writable by the user this server runs as. "
                + "Songs there still scan and play. Uploads from an owner's device, tag and artwork edits, moves and Delete Files will be refused until that changes.",
                string.Join(", ", unwritable));
        }
        else if (wasUnwritable)
        {
            logger.LogInformation("Every library folder is writable again.");
        }
    }
}
