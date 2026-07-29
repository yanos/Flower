namespace Flower.Importer
{
    // Set by a platform entry point (e.g. Flower.Android's MainActivity) before Avalonia
    // starts, on platforms where the filesystem-scanning Importer isn't viable (Android's
    // scoped storage has no arbitrary FS access, so it needs a MediaStore-backed
    // implementation instead). Left null on desktop/iOS, which use Importer directly.
    public static class PlatformMusicImporter
    {
        public static IMusicImporter? Current { get; set; }
    }
}
