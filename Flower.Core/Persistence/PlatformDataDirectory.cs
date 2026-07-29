namespace Flower.Persistence
{
    // Overrides the base app-data directory on platforms the shared project can't
    // resolve on its own (Android's FilesDir requires Android.App, unavailable here).
    // Set by the platform entry point before Avalonia starts; left null everywhere
    // else, where AppDataDirectory resolves the path itself.
    public static class PlatformDataDirectory
    {
        public static string? Current { get; set; }
    }
}
