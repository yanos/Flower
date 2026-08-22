using System.Collections.Generic;
using System.Threading.Tasks;

using Flower.Models;

namespace Flower.Importer
{
    public interface IMusicImporter
    {
        Task<List<Track>> ImportAsync(IEnumerable<string>? libraryPaths = null);

        // Whether what this returns are files on this machine. False for a
        // catalog pulled off a server, whose tracks are placeholders this device
        // does not hold (see RemoteLibraryImporter).
        //
        // Asked rather than inferred from the concrete type, because the callers
        // that need to know are asking a question about the *library* - the
        // startup rescan skips the iTunes play-count and date-added syncs for a
        // remote one, since those read this machine's own Music.app database and
        // have nothing to say about someone else's catalog.
        bool ScansLocalFiles => true;
    }
}
