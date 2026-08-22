using System.Collections.Generic;
using System.Threading.Tasks;

using Flower.Models;

namespace Flower.Importer;

// Where a head's playlists come from, for a head whose playlists are not its
// own. The track counterpart of IMusicImporter, and deliberately a second
// interface rather than another method on it: a desktop imports tracks from
// disk and reads its playlists out of its own database, so the two questions
// have different answers on the same host and only one of them is ever remote.
//
// Optional in the container - only the browser branch registers one. A head
// with no implementation keeps the playlists it loaded from its own store,
// which is what every head did before this existed.
public interface IPlaylistImporter
{
    // Resolved against the library that was just imported, because a playlist
    // on the wire is a list of track *descriptions* rather than references
    // (see PlaylistSyncMapper.ResolveTracks and Track.SyncKey - a Path means
    // nothing on another device). Anything the origin has that this head's
    // library does not is silently dropped, exactly as it is for a peer sync.
    Task<List<Playlist>> ImportAsync(IReadOnlyList<Track> library);
}
