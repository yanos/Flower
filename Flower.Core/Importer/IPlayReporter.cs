using System.Threading.Tasks;

using Flower.Models;

namespace Flower.Importer;

// Where a head's plays go, for a head whose library is not its own. The third
// road out of a browser tab, after IMusicImporter in and IPlaylistWriter back:
// a tab plays a track that lives on the origin server, and until now the play
// count and the played-at stamp for that track changed in the tab and stayed
// there - lost at the next refresh, and invisible to every other client of the
// same server.
//
// The same premise as IPlaylistWriter, applied to listening rather than to
// editing: a tab has no durable identity and nothing of its own to contribute,
// so a play made in a tab is a play of the server's track, reported to the
// server as such. Not the peer-to-peer G-Counter merge two desktops run (see
// Track.RemotePlayCounts) - that needs both sides to keep a durable per-device
// total, which is exactly what a tab cannot do.
//
// Optional in the container - only the browser branch registers one. A head
// with no implementation keeps its plays to itself, which is what every head
// did before this existed, and which is correct for a desktop: its library is
// its own, so its counts are already home.
public interface IPlayReporter
{
    // A play, or a half of one, that just happened locally. Called from
    // Library.TrackStatsChanged, which is the one signal raised by both
    // moments Flower distinguishes - see Library.TrackStatsChange.
    //
    // Never blocks the caller: this runs off a playback callback, and the
    // report is a network round trip.
    void Report(Track track, TrackStatsChange change);

    // Completes when nothing scheduled is still in flight.
    Task InFlight { get; }
}
