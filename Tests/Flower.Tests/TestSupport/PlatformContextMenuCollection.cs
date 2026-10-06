namespace Flower.Tests.TestSupport;

// PlatformContextMenu.Current is a shared static, which
// PlatformContextMenuTests points at a recording stub - nobody else may be
// opening a menu meanwhile. Same remedy as AlbumArtLoaderCollection.
[CollectionDefinition("PlatformContextMenu")]
public sealed class PlatformContextMenuCollection
{
}
