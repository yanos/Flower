using System;

using Flower.Manager;

using LibVLCSharp.Shared;

using Xunit;

namespace Flower.Tests.TestSupport;

// One real LibVLC core shared across every test collection that needs real
// decode - mirrors the app itself only ever creating one core (see
// App.axaml.cs) instead of a fresh one per test class, which would be both
// unrealistic and wasteful of native resources under xUnit's default
// parallel-by-class execution.
//
// Requires a real VLC install to succeed (VLC.app on macOS, system VLC on
// Linux, bundled via NuGet on Windows) - the same requirement the app itself
// already has to even run on those platforms (see CLAUDE.md's VLC section),
// not a new burden introduced by these tests.
public sealed class LibVlcFixture : IDisposable
{
    public LibVLC LibVLC { get; }

    public LibVlcFixture()
    {
        VlcNativeSetup.Initialize();
        LibVLC = new LibVLC();
    }

    public void Dispose() => LibVLC.Dispose();
}

[CollectionDefinition("LibVLC")]
public sealed class LibVlcCollection : ICollectionFixture<LibVlcFixture>
{
}
