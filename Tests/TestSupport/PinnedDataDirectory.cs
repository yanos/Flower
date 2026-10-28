using System;
using System.Collections.Generic;
using System.IO;

using Flower.Persistence;

namespace Flower.Tests.TestSupport;

// Base class for tests whose subject writes under AppDataDirectory. Without
// this they would read and write the developer's real library.json/
// settings.json. Pair it with [Collection("PlatformDataDirectory")] - the
// override is a shared static, so those tests must not run in parallel. That
// pairing is checked, not merely asked for: see
// TestDataDirectoryIsolationTests, which fails on any subclass missing it,
// because a class that pins the global while another collection is running
// redirects that collection's writes here and then deletes the directory under
// them.
//
// Restoring the previous value on Dispose is what keeps this composable, and
// the previous value is never null - AssemblySetup pins a temp directory for
// the whole assembly, so even an unpinned test, or a fire-and-forget save that
// lands after this class has torn down, writes somewhere disposable rather than
// into the developer's real Application Support directory.
public abstract class PinnedDataDirectory : IDisposable
{
    protected string DataDirectory { get; }

    private readonly string? _previous;

    // Things built by a test that must not outlive it - above all a
    // MainViewModel, whose PeerSyncCoordinator starts a periodic DispatcherTimer
    // that keeps ticking on the shared headless dispatcher until it is disposed
    // (see MainViewModelHarness.Parts and TestSupport/AssemblySetup.cs). Owning
    // them here rather than at the end of each test body also means a failing
    // assertion cannot skip the teardown.
    private readonly List<IDisposable> _owned = new();

    protected PinnedDataDirectory()
    {
        DataDirectory = Directory.CreateTempSubdirectory("flower-test-appdata").FullName;
        _previous = PlatformDataDirectory.Current;
        PlatformDataDirectory.Current = DataDirectory;
    }

    protected T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    public virtual void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
            _owned[i].Dispose();
        _owned.Clear();

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
