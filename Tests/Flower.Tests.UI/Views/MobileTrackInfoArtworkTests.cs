using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;
using Flower.Views.Mobile;

using Xunit;

namespace Flower.Tests.Views;

// A cover chosen on the phone's Track Info is written by Save, and not when
// the sheet is closed. Pasted, since headless has no picker.
[Collection("PlatformDataDirectory")]
public class MobileTrackInfoArtworkTests : PinnedDataDirectory
{
    public MobileTrackInfoArtworkTests() => TestIoc.EnsureConfigured();

    [AvaloniaFact]
    public async Task A_pasted_cover_is_written_by_Save()
    {
        using var h = new Harness(DataDirectory, "debaser.wav");
        await h.PasteAsync(SyntheticPng.Build(64, 48));

        Assert.Null(LocalAlbumArtReader.ForFile(h.Song.Path));
        h.Click("SaveButton");
        await UiWait.Until(() => h.Vm.ActiveSheet == MobileSheet.None, "Save closes the sheet once the cover is written");
        // Re-encoded by the clipboard, so compared by size.
        Assert.Equal((64, 48), (h.Song.ArtWidth, h.Song.ArtHeight));
    }

    [AvaloniaFact]
    public async Task Closing_the_sheet_leaves_the_file_without_the_pasted_cover()
    {
        using var h = new Harness(DataDirectory, "gigantic.wav");
        await h.PasteAsync(SyntheticPng.Build(64, 48));

        h.Click("CancelButton");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(MobileSheet.None, h.Vm.ActiveSheet);
        Assert.Null(LocalAlbumArtReader.ForFile(h.Song.Path));
        Assert.Equal((0, 0), (h.Song.ArtWidth, h.Song.ArtHeight));
    }

    private sealed class Harness : IDisposable
    {
        private readonly Window _window;
        private readonly MainViewModelHarness.MobileParts _parts;
        public Track Song { get; }
        public MobileMainViewModel Vm => _parts.Mobile;
        private TrackInfoView Sheet =>
            _window.GetVisualDescendants().OfType<TrackInfoView>().First();

        public Harness(string folder, string fileName)
        {
            var path = SyntheticWav.CreateFile(folder, fileName, TimeSpan.FromMilliseconds(200), SyntheticWav.Ramp());
            Song = new Track { Title = "Debaser", Album = "Doolittle", Artists = "Pixies", Path = path };
            var tracks = new List<Track> { Song };
            _parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));

            _window = new Window { Width = 390, Height = 844 };
            _window.Styles.Add(new FluentTheme());
            _window.Content = new MobileMainView { DataContext = Vm };
            _window.Show();
            Pump(300);

            Vm.OpenAlbumActionsCommand.Execute(new AlbumTileViewModel
            {
                Name = "Doolittle", Artist = "Pixies", Tracks = tracks, RepresentativeTrack = Song,
            });
            Vm.ViewAlbumActionTargetTrackInfoCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
        }

        // The picture is decoded off the UI thread before it is taken.
        public async Task PasteAsync(byte[] png)
        {
            using var bitmap = new Bitmap(new MemoryStream(png));
            await _window.Clipboard!.SetBitmapAsync(bitmap);
            Click("PasteArtButton");
            await UiWait.Until(() => Sheet.FindControl<TextBlock>("ArtMessage")!.Text == "Not saved yet",
                "the pasted cover is taken, to be written on Save");
        }

        public void Click(string button) =>
            Sheet.FindControl<Button>(button)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        public void Dispose()
        {
            Pump(300);
            _window.Close();
            _parts.Dispose();
        }

        private static void Pump(int milliseconds)
        {
            using var cts = new CancellationTokenSource(milliseconds);
            Dispatcher.UIThread.MainLoop(cts.Token);
        }
    }
}
