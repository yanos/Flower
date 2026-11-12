using System;
using System.IO;
using System.Linq;

using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.MusicLibrary;

// Saving tags by replacing the song with an edited copy (TagFileSave), and
// clearing away the copies a save never finished. What failed before it: the
// server editing the cover of a song it was streaming to a phone on
// cellular, refused with "being used by another process" every time.
public sealed class TagFileSaveTests : IDisposable
{
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"flower-tag-save-{Guid.NewGuid():N}");

    public TagFileSaveTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string NewSong(string name = "song.wav") =>
        SyntheticWav.CreateFile(_folder, name, TimeSpan.FromMilliseconds(200), SyntheticWav.Ramp());

    // Opened the way the server's stream route opens a song it is sending
    // (ASP.NET's SendFileAsync), and held for as long as the send takes.
    private static FileStream OpenAsStreamed(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.SequentialScan);

    [Fact]
    public void A_song_being_streamed_takes_a_new_cover_and_the_stream_reads_on_undisturbed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows cannot replace a file another process holds open");
        var path = NewSong();
        var before = File.ReadAllBytes(path);

        using var streamed = OpenAsStreamed(path);
        var sent = new byte[100];
        streamed.ReadExactly(sent);

        Assert.True(AlbumArtWriter.TryWrite(path, Png, "image/png"));

        using var rest = new MemoryStream();
        streamed.CopyTo(rest);
        Assert.Equal(before, sent.Concat(rest.ToArray()).ToArray());
        Assert.Equal(Png, LocalAlbumArtReader.EmbeddedIn(path)!.Bytes);
    }

    [Fact]
    public void A_song_being_streamed_takes_edited_tags()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows cannot replace a file another process holds open");
        var path = NewSong();

        using var streamed = OpenAsStreamed(path);
        TrackTags.WriteToFile(path, new TrackTagsDto(Title: "Roygbiv"));

        using var read = TagLib.File.Create(path);
        Assert.Equal("Roygbiv", read.Tag.Title);
    }

    [Fact]
    public void A_save_leaves_only_the_song_in_its_folder_whether_it_worked_or_not()
    {
        var path = NewSong();
        var notAudio = Path.Combine(_folder, "broken.wav");
        File.WriteAllText(notAudio, "not audio");

        Assert.True(AlbumArtWriter.TryWrite(path, Png, "image/png"));
        Assert.False(AlbumArtWriter.TryWrite(notAudio, Png, "image/png"));

        Assert.Equal(["broken.wav", "song.wav"], Directory.EnumerateFileSystemEntries(_folder).Select(Path.GetFileName).Order());
        Assert.Equal("not audio", File.ReadAllText(notAudio));
    }

    // Replacing the link would leave the song it points at unedited and the
    // link a copy of it.
    [Fact]
    public void A_song_reached_through_a_link_is_edited_where_it_is()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "creating a symbolic link needs privileges on Windows");
        var song = NewSong();
        var link = Path.Combine(_folder, "link.wav");
        File.CreateSymbolicLink(link, song);

        TagFileSave.Edit(link, file => file.Tag.Title = "Dayvan Cowboy");

        Assert.NotNull(new FileInfo(link).LinkTarget);
        using var read = TagLib.File.Create(song);
        Assert.Equal("Dayvan Cowboy", read.Tag.Title);
    }

    // The copy a crashed save leaves is neither a song to a walk of the
    // folders nor kept by one for longer than a day; a copy younger than that
    // may be a save still under way in another process.
    [Fact]
    public void A_walk_of_the_folders_passes_over_a_saves_copy_and_deletes_it_once_it_is_a_day_old()
    {
        var song = NewSong();
        var stale = Path.Combine(_folder, $".flower-save-{Guid.NewGuid():N}.tmp");
        var fresh = Path.Combine(_folder, $".flower-save-{Guid.NewGuid():N}.tmp");
        File.Copy(song, stale);
        File.Copy(song, fresh);
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromDays(2));
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow);

        var found = Importer.Importer.ScannableFiles(_folder, _folder).ToList();

        Assert.Equal([song], found);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    // Only Flower's own copies: anything else, however old, is somebody's.
    [Fact]
    public void A_walk_of_the_folders_deletes_nothing_else()
    {
        var others = new[] { ".flower-save-notes.txt", "flower-save-1.tmp", "old.tmp", ".hidden.tmp" }
            .Select(name => Path.Combine(_folder, name)).ToList();
        foreach (var other in others)
        {
            File.WriteAllText(other, "");
            File.SetLastWriteTimeUtc(other, DateTime.UtcNow - TimeSpan.FromDays(30));
        }

        Importer.Importer.ScannableFiles(_folder, _folder).ToList();

        Assert.All(others, other => Assert.True(File.Exists(other), other));
    }
}
