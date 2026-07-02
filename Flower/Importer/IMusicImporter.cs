using System.Collections.Generic;
using System.Threading.Tasks;

using Flower.Models;

namespace Flower.Importer
{
    public interface IMusicImporter
    {
        Task<List<Track>> ImportAsync(IEnumerable<string>? libraryPaths = null);
    }
}
