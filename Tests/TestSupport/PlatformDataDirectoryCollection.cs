namespace Flower.Tests.TestSupport;

// PlatformDataDirectory.Current is a shared static - any test class that
// pins it to an isolated temp directory has to run sequentially relative
// to every other such class, or they'd stomp on each other's isolation
// under xUnit's default parallel-by-class execution. Tests in the same
// named collection never run in parallel with each other, which is all
// this needs - no shared fixture object required.
//
// PlatformContextMenu.Current is another such static, which the context-menu
// tests point at a recording stub, and those tests are here too: the classes
// that open menus from a whole MainView already are, so here nothing else is
// opening one meanwhile.
[CollectionDefinition("PlatformDataDirectory")]
public sealed class PlatformDataDirectoryCollection
{
}
