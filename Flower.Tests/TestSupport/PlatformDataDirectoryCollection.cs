namespace Flower.Tests.TestSupport;

// PlatformDataDirectory.Current is a shared static - any test class that
// pins it to an isolated temp directory has to run sequentially relative
// to every other such class, or they'd stomp on each other's isolation
// under xUnit's default parallel-by-class execution. Tests in the same
// named collection never run in parallel with each other, which is all
// this needs - no shared fixture object required.
[CollectionDefinition("PlatformDataDirectory")]
public sealed class PlatformDataDirectoryCollection
{
}
