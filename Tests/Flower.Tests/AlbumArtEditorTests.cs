using System;
using System.IO;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests;

// The cover change Track Info makes, against a real file: the
// picture lands in the tag, the cached bitmap is let go, and the library says
// the art changed so every view showing that cover repaints it.
public class AlbumArtEditorTests
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static string NewTrackFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"flower-art-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return SyntheticWav.CreateFile(directory, "track.wav", TimeSpan.FromMilliseconds(200), SyntheticWav.Ramp());
    }

    [Fact]
    public async Task Writing_a_cover_puts_it_in_the_file_and_says_the_art_changed()
    {
        var track = new Track { Title = "T", Path = NewTrackFile() };
        var library = new Library([track]);
        TrackChangedEventArgs? changed = null;
        library.TrackChanged += (_, e) => changed = e;
        var editor = new AlbumArtEditor([track], library, new AppSettings(), null, null, NullLogger.Instance);

        Assert.True(editor.CanWrite);
        var error = await editor.WriteAsync(Png, "image/png");

        Assert.Null(error);
        Assert.Equal(Png, LocalAlbumArtReader.ForFile(track.Path)!.Bytes);
        Assert.Equal(TrackChange.Artwork, changed?.Change);
        Assert.Same(track, Assert.Single(changed!.Tracks));
    }

    [Fact]
    public async Task A_file_that_cannot_be_written_is_reported_rather_than_thrown()
    {
        var track = new Track { Title = "T", Path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.mp3") };
        var editor = new AlbumArtEditor([track], new Library([track]), new AppSettings(), null, null, NullLogger.Instance);

        var error = await editor.WriteAsync(Png, "image/png");

        Assert.Equal("The artwork couldn't be written to the file.", error);
    }
}
