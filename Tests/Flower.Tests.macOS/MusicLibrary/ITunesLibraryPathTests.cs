using Flower.Importer;
using Flower.Tests.TestSupport;

namespace Flower.Tests.macOS.MusicLibrary;

[Collection("PlatformDataDirectory")]
public class ITunesLibraryPathTests : IsolatedHome
{
    [Fact]
    public void ResolveLibraryXmlPath_prefers_the_Music_app_export_when_both_exist()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Automatic Music.app and iTunes XML path resolution is macOS-only.");

        // A machine that migrated from classic iTunes to Music.app can easily
        // have both files sitting on disk - the Music.app one is the actively
        // maintained one and must win, not whichever happens to be checked first.
        var musicLibraryDir = Path.Combine(Home, "Music", "Music");
        Directory.CreateDirectory(musicLibraryDir);
        var musicAppPath = Path.Combine(musicLibraryDir, "Music Library.xml");
        File.WriteAllText(musicAppPath, "<plist/>");

        var iTunesDir = Path.Combine(Home, "Music", "iTunes");
        Directory.CreateDirectory(iTunesDir);
        File.WriteAllText(Path.Combine(iTunesDir, "iTunes Music Library.xml"), "<plist/>");

        Assert.Equal(musicAppPath, ITunesPlayCountImporter.ResolveLibraryXmlPath());
    }

    [Fact]
    public void ResolveLibraryXmlPath_finds_a_classic_iTunes_export_when_thats_all_that_exists()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Automatic Music.app and iTunes XML path resolution is macOS-only.");

        // Regression coverage for the actual bug: a real classic-iTunes export
        // lives at "~/Music/iTunes/iTunes Music Library.xml" - a different
        // folder ("iTunes", not "Music") AND filename ("iTunes Music Library.xml",
        // not "iTunes Library.xml") than originally guessed, so this silently
        // found nothing on a real machine that actually had one.
        var iTunesDir = Path.Combine(Home, "Music", "iTunes");
        Directory.CreateDirectory(iTunesDir);
        var iTunesPath = Path.Combine(iTunesDir, "iTunes Music Library.xml");
        File.WriteAllText(iTunesPath, "<plist/>");

        Assert.Equal(iTunesPath, ITunesPlayCountImporter.ResolveLibraryXmlPath());
    }
}
