using System;
using System.IO;

using Flower.Persistence;

namespace Flower.Tests.TestSupport;

// Base class for tests whose subject writes under AppDataDirectory. Without
// this they would read and write the developer's real library.json/
// settings.json. Pair it with [Collection("PlatformDataDirectory")] - the
// override is a shared static, so those tests must not run in parallel.
public abstract class PinnedDataDirectory : IDisposable
{
    protected string DataDirectory { get; }

    private readonly string? _previous;

    protected PinnedDataDirectory()
    {
        DataDirectory = Directory.CreateTempSubdirectory("flower-test-appdata").FullName;
        _previous = PlatformDataDirectory.Current;
        PlatformDataDirectory.Current = DataDirectory;
    }

    public virtual void Dispose()
    {
        PlatformDataDirectory.Current = _previous;
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
