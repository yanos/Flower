using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Flower.Models;
using Flower.Services;

using Xunit;

namespace Flower.Tests.MusicLibrary;

// A download deleted by hand - in Finder, out of Flower's folder - and the
// rescan the folder watcher runs straight after. The song goes back to being
// the server's, streamed, as Remove Download leaves it; it used to stay a row
// marked downloaded that played nothing. What it must not do is read an
// unplugged drive as every download deleted.
public class DeletedDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flower-deleted-download-" + Guid.NewGuid().ToString("N"));

    public DeletedDownloadTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private Library LibraryWith(Track download)
    {
        var library = new Library(new List<Track> { download });
        library.UsePathRules(PathRules.For([_root]));
        return library;
    }

    private Track Download(string relative) => new()
    {
        Title = "Summer Babe", Artists = "Pavement", Album = "Slanted and Enchanted",
        Path = Path.Combine(_root, relative),
        OriginDeviceFingerprint = "server-1", OriginTrackId = "t-1", IsLocallyDownloaded = true,
    };

    [Fact]
    public void A_download_deleted_from_its_folder_is_streamed_again()
    {
        File.WriteAllText(Path.Combine(_root, "other.mp3"), "");
        var download = Download("Pavement/01 Summer Babe.mp3");
        var library = LibraryWith(download);

        library.UpdateTracks([]);

        var song = Assert.Single(library.Tracks);
        Assert.Equal(download.Id, song.Id);
        Assert.Null(song.Path);
        Assert.False(song.IsLocallyDownloaded);
        Assert.Equal("t-1", song.OriginTrackId);
    }

    [Fact]
    public void A_download_on_a_drive_that_is_not_there_stays_a_download()
    {
        var download = Download("Pavement/01 Summer Babe.mp3");
        var library = LibraryWith(download);
        Directory.Delete(_root);

        library.UpdateTracks([]);

        var song = Assert.Single(library.Tracks);
        Assert.Equal(download.Path, song.Path);
        Assert.True(song.IsLocallyDownloaded);
    }

    // Android's downloads sit in a folder the MediaStore scan never reads:
    // the scan finding nothing there is not the files going.
    [Fact]
    public void A_download_the_scan_never_looked_at_stays_a_download()
    {
        var download = Download("Pavement/01 Summer Babe.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(download.Path)!);
        File.WriteAllText(download.Path!, "");
        var library = LibraryWith(download);

        library.UpdateTracks([]);

        Assert.True(Assert.Single(library.Tracks).IsLocallyDownloaded);
    }
}
