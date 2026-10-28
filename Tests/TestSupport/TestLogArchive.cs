using System;
using System.IO;

using Flower.Logging;
using Flower.Services;

namespace Flower.Tests.TestSupport;

// A DeviceLogArchive rooted in a throwaway directory. Every test that builds a
// LibrarySyncService needs one, and almost none of them care what it holds -
// but it writes real files, so it must never land on the developer's own
// archive under AppDataDirectory.
internal static class TestLogArchive
{
    public static DeviceLogArchive InTempDirectory() =>
        new(new ClientLogStore(Path.Combine(Path.GetTempPath(), "flower-log-archive-" + Guid.NewGuid())),
            InMemoryLogStore.Instance);
}
