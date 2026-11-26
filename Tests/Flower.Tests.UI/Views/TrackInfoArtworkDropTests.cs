using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using TabStrip = Avalonia.Controls.Primitives.TabStrip;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.Views;

using Xunit;

namespace Flower.Tests.Views;

// A cover dropped on Track Info - the header's thumbnail from any tab, or the
// Artwork tab's frame - goes into the song's file when Save is clicked, and
// not at all on Cancel. Which controls take a drop lives in the window alone,
// so this drives a real one.
[Collection("PlatformDataDirectory")]
public class TrackInfoArtworkDropTests : PinnedDataDirectory
{
    public TrackInfoArtworkDropTests() => TestIoc.EnsureConfigured();

    [AvaloniaFact]
    public async Task An_image_file_dropped_on_the_header_is_written_by_Save()
    {
        var (song, window, parts) = OpenOn("debaser.wav");
        using var cleanup = parts;
        var png = SyntheticPng.Build(64, 48);
        var transfer = await FileTransfer(window, "cover.png", png);

        Assert.Equal(DragDropEffects.Copy, Drop(window, "HeaderArtDropTarget", transfer));
        await UntilStaged(window);
        Assert.Null(LocalAlbumArtReader.ForFile(song.Path));
        Assert.Equal(0, window.FindControl<TabStrip>("Tabs")!.SelectedIndex);

        Click(window, "OkButton");
        await UiWait.Until(() => !window.IsVisible, "Save closes the window once the cover is written");
        Assert.Equal(png, LocalAlbumArtReader.ForFile(song.Path)!.Bytes);
    }

    [AvaloniaFact]
    public async Task Cancel_leaves_the_file_without_the_dropped_cover()
    {
        var (song, window, parts) = OpenOn("gigantic.wav");
        using var cleanup = parts;
        var transfer = await FileTransfer(window, "cover.png", SyntheticPng.Build(64, 48));

        Drop(window, "HeaderArtDropTarget", transfer);
        await UntilStaged(window);
        Click(window, "CancelButton");
        Dispatcher.UIThread.RunJobs();

        Assert.False(window.IsVisible);
        Assert.Null(LocalAlbumArtReader.ForFile(song.Path));
        Assert.Equal((0, 0), (song.ArtWidth, song.ArtHeight));
    }

    // A picture dragged out of a browser or Photos comes as a bitmap, with no
    // file behind it.
    [AvaloniaFact]
    public async Task A_picture_with_no_file_dropped_on_the_Artwork_tab_is_written_by_Save()
    {
        var (song, window, parts) = OpenOn("hey.wav");
        using var cleanup = parts;
        window.FindControl<TabStrip>("Tabs")!.SelectedIndex = 4;
        Dispatcher.UIThread.RunJobs();
        using var bitmap = new Bitmap(new MemoryStream(SyntheticPng.Build(80, 60)));

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(DataFormat.Bitmap, bitmap));
        Assert.Equal(DragDropEffects.Copy, Drop(window, "ArtDropTarget", transfer));
        await UntilStaged(window);
        Click(window, "OkButton");

        await UiWait.Until(() => song.ArtWidth != 0, "Save writes the dropped picture");
        Assert.Equal((80, 60), (song.ArtWidth, song.ArtHeight));
    }

    // Something that is no picture is refused before it is let go.
    [AvaloniaFact]
    public void A_file_that_is_no_image_is_refused()
    {
        var (_, window, parts) = OpenOn("wave.wav");
        using var cleanup = parts;
        var transfer = FileTransfer(window, "notes.txt", "not a cover"u8.ToArray()).GetAwaiter().GetResult();

        Assert.Equal(DragDropEffects.None, DragOver(window, "HeaderArtDropTarget", transfer));
    }

    private async Task<IDataTransfer> FileTransfer(Window window, string name, byte[] contents)
    {
        // Not beside the song, where a cover.png is its folder's cover.
        var folder = Directory.CreateDirectory(Path.Combine(DataDirectory, "Downloads")).FullName;
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, contents);
        var file = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(path));
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateFile(file!));
        return transfer;
    }

    // The picture is decoded before it is taken, off the UI thread.
    private static Task UntilStaged(Window window) =>
        UiWait.Until(() => window.FindControl<TextBlock>("ArtworkInfoText")!.Text?.Contains("Not saved yet") == true,
            "the dropped cover is taken, to be written on Save");

    private static void Click(Window window, string button) =>
        window.FindControl<Button>(button)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private (Track Song, TrackInfoWindow Window, IDisposable Parts) OpenOn(string fileName)
    {
        var path = SyntheticWav.CreateFile(DataDirectory, fileName, TimeSpan.FromMilliseconds(200), SyntheticWav.Ramp());
        var song = new Track { Title = "Debaser", Album = "Doolittle", Artists = "Pixies", Path = path };
        var tracks = new List<Track> { song };
        var parts = MainViewModelHarness.BuildParts(new Library(tracks), new MainPlaylist(tracks));
        var window = new TrackInfoWindow(tracks, 0, parts.Main);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (song, window, parts);
    }

    // What the drop target said it would do, read on the way up past the
    // window: the headless drag itself reports nothing back.
    private static DragDropEffects DragOver(Window window, string target, IDataTransfer transfer)
    {
        var effects = DragDropEffects.None;
        EventHandler<DragEventArgs> seen = (_, e) => effects = e.DragEffects;
        window.AddHandler(DragDrop.DragOverEvent, seen, RoutingStrategies.Bubble, handledEventsToo: true);
        var point = Centre(window, target);
        window.DragDrop(point, RawDragEventType.DragEnter, transfer, DragDropEffects.Copy);
        window.DragDrop(point, RawDragEventType.DragOver, transfer, DragDropEffects.Copy);
        window.RemoveHandler(DragDrop.DragOverEvent, seen);
        return effects;
    }

    private static DragDropEffects Drop(Window window, string target, IDataTransfer transfer)
    {
        var effects = DragOver(window, target, transfer);
        window.DragDrop(Centre(window, target), RawDragEventType.Drop, transfer, DragDropEffects.Copy);
        return effects;
    }

    private static Point Centre(Window window, string name)
    {
        var control = window.FindControl<Control>(name)!;
        return control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
    }
}
