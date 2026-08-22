using System.Collections.Generic;
using System.Threading.Tasks;

using Flower.Models;

namespace Flower.Importer;

// Where a head's playlist edits go, for a head whose playlists are not its own.
// The mirror of IPlaylistImporter, and a separate interface for the same
// reason that one is separate from IMusicImporter: a desktop's edits go to its
// own database and then, on its own schedule, into a two-way peer merge, so
// "read the far side" and "write the far side" have different answers on the
// same host.
//
// Optional in the container - only the browser branch registers one. A head
// with no implementation keeps its edits to itself, which is what every head
// did before this existed.
public interface IPlaylistWriter
{
    // Records what the far side is already known to hold, so the very next
    // Schedule does not send it straight back. Called on the import path,
    // before the fetched set is installed: installing it raises
    // Library.PlaylistsChanged exactly as a user's own edit does, and nothing
    // in that event says which of the two it was.
    void NoteOriginState(IReadOnlyList<Playlist> playlists);

    // Queues the whole set to be pushed. Coalescing and ordering are the
    // implementation's problem: a drag-reorder raises one change per move, and
    // ten overlapping full-manifest POSTs can land in any order - the last one
    // to arrive wins, which is not necessarily the last one sent.
    void Schedule(IReadOnlyList<Playlist> playlists);

    // Completes when nothing scheduled is still in flight.
    Task InFlight { get; }
}
