using System.Collections.Generic;
using System.Threading.Tasks;

using Flower.Models;

namespace Flower.Importer;

// The importer for a head that has a library screen but nothing it is allowed
// to fill it from - today, a browser tab opened without a session token.
//
// A null object rather than leaving IMusicImporter unregistered, because the
// shared registration is the filesystem scanner and inheriting it would send a
// browser sandbox looking for a music folder. "There is nothing here" is a real
// answer and this is the honest way to give it.
public sealed class EmptyLibraryImporter : IMusicImporter
{
    public bool ScansLocalFiles => false;

    public Task<List<Track>> ImportAsync(IEnumerable<string>? libraryPaths = null) =>
        Task.FromResult(new List<Track>());
}
