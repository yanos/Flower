namespace Flower.Tests.TestSupport;

// AlbumArtLoader.Current is a shared static, and two test classes redirect it at
// a counting stub of their own - AlbumTileMergeTests and TrackRowMergeTests.
// Under xUnit's default parallel-by-class execution they overlap, and then each
// class's loads are counted by whichever stub was installed last: a test that
// caused one load reads two, or none. That is what failed a macOS CI run.
//
// Same shape as PlatformDataDirectoryCollection, and the same remedy - tests in
// one named collection never run in parallel with each other, and no shared
// fixture object is needed to say so. The per-class Dispose that restores the
// previous loader is still right; it just needed nobody else running while it
// mattered.
[CollectionDefinition("AlbumArtLoader")]
public sealed class AlbumArtLoaderCollection
{
}
