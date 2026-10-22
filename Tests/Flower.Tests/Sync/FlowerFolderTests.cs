using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests.Sync;

// Flower's own folder is written to, moved within and deleted from, so it has
// to be Flower's - see FlowerFolder. These pin the line between a folder Flower
// may take and one somebody else made.
[Collection("PlatformDataDirectory")]
public class FlowerFolderTests : IDisposable
{
    private readonly string _music = Directory.CreateTempSubdirectory("flower-folder-music").FullName;
    private readonly string _folder;
    private readonly AppSettings _settings = new();

    public FlowerFolderTests()
    {
        _folder = Path.Combine(_music, "Flower");
        FlowerFolder.Override = _folder;
    }

    public void Dispose()
    {
        FlowerFolder.Override = AssemblySetup.DefaultFlowerFolder;
        TempDirectory.DeleteWhenReleased(_music);
    }

    private FlowerFolder Folder(Func<string, Task<bool>>? ask = null) =>
        new(_settings, () => Task.CompletedTask, NullLogger<FlowerFolder>.Instance) { AskToTakeOver = ask };

    [Fact]
    public async Task A_folder_that_is_not_there_is_made_and_marked_without_asking()
    {
        var asked = false;

        var claimed = await Folder(_ => { asked = true; return Task.FromResult(true); }).ClaimAsync();

        Assert.Equal(_folder, claimed);
        Assert.False(asked);
        Assert.True(File.Exists(Path.Combine(_folder, FlowerFolder.MarkerName)));
        Assert.Equal(_folder, FlowerFolder.UsablePath(_settings));
    }

    [Fact]
    public async Task A_folder_with_somebody_elses_files_is_not_written_to_with_nobody_to_ask()
    {
        TaggedAudioFile.Create(_folder, "theirs.wav", "Theirs");

        var claimed = await Folder(ask: null).ClaimAsync();

        Assert.Null(claimed);
        Assert.False(File.Exists(Path.Combine(_folder, FlowerFolder.MarkerName)));
        Assert.Null(FlowerFolder.UsablePath(_settings));
    }

    [Fact]
    public async Task A_folder_with_somebody_elses_files_is_taken_over_on_a_yes()
    {
        TaggedAudioFile.Create(_folder, "theirs.wav", "Theirs");

        var claimed = await Folder(_ => Task.FromResult(true)).ClaimAsync();

        Assert.Equal(_folder, claimed);
        Assert.True(File.Exists(Path.Combine(_folder, FlowerFolder.MarkerName)));
    }

    // A no is remembered as a folder of Flower's own beside it, so the
    // question is not asked again.
    [Fact]
    public async Task A_no_puts_Flowers_folder_beside_it_and_remembers_where()
    {
        TaggedAudioFile.Create(_folder, "theirs.wav", "Theirs");
        var asks = 0;
        var folder = Folder(_ => { asks++; return Task.FromResult(false); });

        var claimed = await folder.ClaimAsync();
        var again = await folder.ClaimAsync();

        Assert.Equal(Path.Combine(_music, "Flower 2"), claimed);
        Assert.Equal(claimed, again);
        Assert.Equal(claimed, _settings.FlowerFolderPath);
        Assert.Equal(1, asks);
        Assert.False(File.Exists(Path.Combine(_folder, FlowerFolder.MarkerName)));
    }

    // Finder's bookkeeping in an otherwise empty folder is not somebody's music.
    [Fact]
    public async Task A_folder_holding_only_a_DS_Store_is_free()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, ".DS_Store"), "");

        Assert.Equal(_folder, await Folder(ask: null).ClaimAsync());
    }

    // Downloads from before the folder existed sat straight in ~/Music. They
    // move in at the same relative paths; nothing else there is touched.
    [Fact]
    public void Old_downloads_move_into_the_folder_and_nothing_else_does()
    {
        var download = TaggedAudioFile.Create(_music, "Sonic Youth/Daydream Nation/01 Teen Age Riot.wav", "Teen Age Riot");
        var own = TaggedAudioFile.Create(_music, "Music/Media.localized/Music/a.wav", "Own");
        var library = new Library([
            new Track { Title = "Teen Age Riot", Path = download, IsLocallyDownloaded = true },
            new Track { Title = "Own", Path = own },
        ]);
        Directory.CreateDirectory(_folder);

        var moved = FlowerFolder.MoveDownloads(library, _music, _folder, NullLogger.Instance);

        Assert.Equal(1, moved);
        var target = Path.Combine(_folder, "Sonic Youth", "Daydream Nation", "01 Teen Age Riot.wav");
        Assert.True(File.Exists(target));
        Assert.Equal(target, library.Tracks.Single(t => t.Title == "Teen Age Riot").Path);
        Assert.False(Directory.Exists(Path.Combine(_music, "Sonic Youth")));
        Assert.True(File.Exists(own));
    }

    [Fact]
    public void The_folder_is_scanned_only_on_a_paired_device_and_once_it_is_Flowers()
    {
        _settings.PairedServerFingerprint = null;
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, FlowerFolder.MarkerName), "");
        Assert.DoesNotContain(_folder, DeviceLibraryFolders.ScanPaths(_settings));

        _settings.PairedServerFingerprint = "server";
        Assert.Contains(_folder, DeviceLibraryFolders.ScanPaths(_settings));

        File.Delete(Path.Combine(_folder, FlowerFolder.MarkerName));
        Assert.DoesNotContain(_folder, DeviceLibraryFolders.ScanPaths(_settings));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void MusicApps_music_is_scanned_by_a_paired_admin_that_asked_for_it(bool admin, bool switchedOn, bool scanned)
    {
        var media = Path.Combine(_music, "Media.localized");
        Directory.CreateDirectory(Path.Combine(media, "Music"));
        DeviceLibraryFolders.MusicAppMediaFolderOverride = media;
        try
        {
            _settings.PairedServerFingerprint = "server";
            _settings.PairedServerGrantsAdmin = admin;
            _settings.AddSongsFromMusicApp = switchedOn;

            Assert.Equal(scanned, DeviceLibraryFolders.ScanPaths(_settings).Contains(Path.Combine(media, "Music")));
        }
        finally
        {
            DeviceLibraryFolders.MusicAppMediaFolderOverride = null;
        }
    }
}
