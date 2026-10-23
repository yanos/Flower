using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels;

namespace Flower.Views;

// The desktop's Track Info: what it shows and edits is TrackInfoViewModel's,
// shared with the phone's sheet. What stays here is the window's own - the
// tabs, Prev/Next, the art bitmaps and every way a picture moves in and out.
public partial class TrackInfoWindow : Window
{
    // Only meaningful in single-track/navigable mode (see the two constructors below).
    private readonly IReadOnlyList<Track> _tracks = Array.Empty<Track>();
    // Handed in by whoever opens this window, which already has it, rather
    // than looked up in the container (docs/ARCHITECTURE-REVIEW.md Tier 2.3):
    // what Track Info is (TrackInfo) and what a cover change needs (ArtEditorFor). The
    // logger comes the same way, from the container's factory the view model
    // carries: a Window has no constructor the container reaches.
    private readonly MainViewModel _main;
    private readonly ILogger<TrackInfoWindow> _logger = NullLogger<TrackInfoWindow>.Instance;
    private int _index;

    // The songs being edited, and everything said about them: exactly one in
    // navigable mode (reloaded on every Navigate()), or the whole
    // multi-selection in batch mode.
    private readonly TrackInfoViewModel _info = null!;
    private IReadOnlyList<Track> _editTracks => _info.Tracks;
    private int _artRequestId; // guards against a stale Navigate()'s art load winning a race

    // The raw bytes behind whatever the Artwork tab is currently showing (see
    // LoadAlbumArtAsync) - kept because the displayed Bitmap is downscaled to
    // AlbumArtLoader.MaxArtPixels, so it can answer neither "what size is this
    // really" nor "open it full size".
    private LocalAlbumArt? _art;
    private PixelSize? _artPixelSize;

    // A Find Online still searching or downloading, and the covers it found
    // while they are on show - both dropped on Prev/Next and on closing.
    private CancellationTokenSource? _findArt;
    private ArtworkViewer? _viewer;

    private Track _track => _tracks[_index];

    public event EventHandler<Track>? TrackNavigated;

    // Satisfies Avalonia's runtime-XAML-loader/previewer check (AVLN3001) -
    // never called directly; the two real constructors below are what's
    // actually used.
#pragma warning disable CS8618
    public TrackInfoWindow() => InitializeComponent();
#pragma warning restore CS8618

    // Single-track mode: tracks/index is the full displayed list, so Prev/Next
    // can browse through it one at a time.
    public TrackInfoWindow(IReadOnlyList<Track> tracks, int index, MainViewModel main)
    {
        InitializeComponent();
        _tracks    = tracks;
        _main      = main;
        _logger    = main.LoggerFactory.CreateLogger<TrackInfoWindow>();
        _index     = index;
        _info      = main.TrackInfo();
        DataContext = _info;
        SetUpArtworkDropTarget();
        Populate([_track]);
        UpdateNavButtons();
        NativeMenuHelper.InheritFromMainWindow(this);
    }

    // Batch mode: edit this exact set of tracks together. No Prev/Next - there's
    // no "next" when editing a fixed set as one.
    public TrackInfoWindow(IReadOnlyList<Track> editTracks, MainViewModel main)
    {
        InitializeComponent();
        _main       = main;
        _logger     = main.LoggerFactory.CreateLogger<TrackInfoWindow>();
        _info       = main.TrackInfo();
        DataContext = _info;
        SetUpArtworkDropTarget();
        Populate(editTracks);
        PrevButton.IsVisible = false;
        NextButton.IsVisible = false;
        if (editTracks.Count > 1)
            Title = $"Info ({editTracks.Count} tracks)";
        NativeMenuHelper.InheritFromMainWindow(this);
    }

    // The pages under the header's tabs - one showing at a time, in the
    // TabStrip's order.
    private void Tabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Raised once while the XAML is still loading (SelectedIndex="0"),
        // before the pages are there - Info is the one showing by default.
        if (InfoPage is null)
            return;
        Control[] pages = [InfoPage, DescriptionsPage, LyricsPage, ArtworkPage, OptionsPage, TechnicalPage];
        for (var i = 0; i < pages.Length; i++)
            pages[i].IsVisible = i == Tabs.SelectedIndex;
    }

    private void PrevButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Navigate(-1);
    private void NextButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Navigate(+1);

    private void UpdateNavButtons()
    {
        PrevButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index < _tracks.Count - 1;
    }

    // Saved before moving on: stepping to the next song is not leaving, so
    // what was typed for this one is kept.
    private void Navigate(int delta)
    {
        var next = _index + delta;
        if (next < 0 || next >= _tracks.Count)
            return;
        _info.Save();
        _index = next;
        Populate([_track]);
        UpdateNavButtons();
        TrackNavigated?.Invoke(this, _track);
    }

    private void Populate(IReadOnlyList<Track> tracks)
    {
        StopFindingArt();
        _info.Load(tracks);
        GenreMenuButton.IsVisible = _info.IsEditable && _info.GenreSuggestions.Count > 0;
        _ = LoadAlbumArtAsync();
    }

    // Every genre the library has, behind the chevron beside the box.
    private void GenreMenu_Click(object? sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var genre in _info.GenreSuggestions)
        {
            var item = new MenuItem { Header = genre };
            item.Click += (_, _) => _info.Genre.Text = genre;
            flyout.Items.Add(item);
        }

        flyout.ShowAt(GenreMenuButton);
    }

    private void StarButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _info.ToggleStar();

    // Shows the first selected track's art (embedded tag picture, falling back
    // to a cover/folder image file - see AlbumArtLoader). For a batch selection
    // spanning multiple albums this is necessarily just one representative
    // thumbnail, not a "mixed" indicator - album art has no text form to show
    // "Multiple values" with.
    private async Task LoadAlbumArtAsync()
    {
        var requestId = ++_artRequestId;
        AlbumArtView.AlbumArt = null;
        ArtworkLargeView.AlbumArt = null;
        _art = null;
        _artPixelSize = null;
        UpdateArtworkTab();
        if (_editTracks.Count == 0)
            return;

        var track = _editTracks[0];
        var bmp = await AlbumArtLoader.Current.LoadAsync(track);

        // The undecoded bytes, and the real pixel dimensions read from them -
        // bmp above is capped at MaxArtPixels and would report the cap, not
        // the art. Off the UI thread, since the bytes come off the disk.
        var (art, pixelSize) = await Task.Run(() =>
        {
            var found = AlbumArtLoader.TryGetArt(track);
            return (found, found is null ? null : AlbumArtLoader.MeasureArt(found.Bytes));
        });

        if (requestId != _artRequestId)
            return;

        AlbumArtView.AlbumArt = bmp;
        ArtworkLargeView.AlbumArt = bmp;
        _art = art;
        _artPixelSize = pixelSize;
        UpdateArtworkTab();
    }

    private void OkButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _info.Save();
        Close();
    }

    private void CancelButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }

    // ── Artwork tab ────────────────────────────────────────────────────────
    //
    // Unlike every other field in this window, artwork is not staged and
    // applied on OK: a change here writes the picture into the tag immediately
    // (AlbumArtEditor). Two reasons. The tag write is a whole-file rewrite
    // of megabytes, not a string assignment, so batching it behind OK would make
    // Cancel look free while the expensive part still had to happen; and the
    // preview the user is looking at *is* the file's art once it is written, so
    // there is nothing left for OK to confirm. Cancel therefore does not undo an
    // artwork change - which is why the buttons name actions ("Change…",
    // "Remove") rather than reading as pending edits.
    //
    // "The file" is not always one of this device's: a track that only exists on
    // the paired server is written by asking that server to do it - see
    // AlbumArtEditor.ServerAlbumIds.

    // Rebuilt per call because _editTracks is replaced on every Navigate();
    // it holds nothing but the list it is given.
    private AlbumArtEditor ArtEditor() => _main.ArtEditorFor(_editTracks, _logger);

    private List<Track> ArtWriteTargets() => ArtEditor().LocalTargets();

    private List<string> ServerArtAlbumIds() => ArtEditor().ServerAlbumIds();

    private void SetUpArtworkDropTarget()
    {
        Closed += (_, _) => StopFindingArt();
        DragDrop.SetAllowDrop(ArtDropTarget, true);
        ArtDropTarget.AddHandler(DragDrop.DragOverEvent, ArtDropTarget_DragOver);
        ArtDropTarget.AddHandler(DragDrop.DropEvent, ArtDropTarget_Drop);
    }

    // The dropped file's path, but only if it is an image format the read side
    // already understands - LocalAlbumArtReader owns that list, so a drop can
    // never embed a picture the app would then fail to load back.
    private static string? DroppedImagePath(IDataTransfer data)
    {
        if (data.TryGetFiles()?.FirstOrDefault()?.TryGetLocalPath() is not string path)
            return null;

        return LocalAlbumArtReader.MimeTypeForExtension(Path.GetExtension(path)) != null ? path : null;
    }

    private void ArtDropTarget_DragOver(object? sender, DragEventArgs e)
    {
        var accepted = ArtWriteTargets().Count > 0 && DroppedImagePath(e.DataTransfer) != null;
        e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void ArtDropTarget_Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DroppedImagePath(e.DataTransfer) is string path)
            await ApplyArtFromFileAsync(path);
    }

    private async void ChangeArtButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storageProvider)
            return;

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose Album Art",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images")
                {
                    Patterns = [.. LocalAlbumArtReader.ImageExtensions.Select(ext => "*" + ext)],
                    MimeTypes = ["image/*"],
                    AppleUniformTypeIdentifiers = ["public.image"],
                },
            ],
        });

        if (files.FirstOrDefault()?.TryGetLocalPath() is string path)
            await ApplyArtFromFileAsync(path);
    }

    private async Task ApplyArtFromFileAsync(string path)
    {
        if (LocalAlbumArtReader.MimeTypeForExtension(Path.GetExtension(path)) is not string mimeType)
        {
            ArtworkInfoText.Text = "That file isn't an image format Flower can read.";
            return;
        }

        byte[] bytes;
        try
        {
            bytes = await Task.Run(() => File.ReadAllBytes(path));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the picked album art file {Path}", path);
            ArtworkInfoText.Text = "That file couldn't be read.";
            return;
        }

        await WriteArtAsync(bytes, mimeType);
    }

    private Task WriteArtAsync(byte[] bytes, string mimeType) =>
        ApplyArtChangeAsync(editor => editor.WriteAsync(bytes, mimeType));

    // Not a confirmation-guarded action even though it destroys data, for the
    // same reason Change is not - it is one explicit click on a button that
    // says what it does, on a file the user is already editing the tags of,
    // and the way back is to drop the old image in again (which is why Open
    // Full Size and dragging the art out both exist before this button does).
    private Task RemoveArtAsync() =>
        ApplyArtChangeAsync(editor => editor.RemoveAsync());

    private async Task ApplyArtChangeAsync(Func<AlbumArtEditor, Task<string?>> change)
    {
        var editor = ArtEditor();
        if (!editor.CanWrite)
            return;

        ChangeArtButton.IsEnabled = false;
        PasteArtButton.IsEnabled = false;
        FindArtButton.IsEnabled = false;
        RemoveArtButton.IsEnabled = false;

        var message = await change(editor);
        await LoadAlbumArtAsync();

        if (message != null)
            ArtworkInfoText.Text = message;
    }

    private async void RemoveArtButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await RemoveArtAsync();

    // The clipboard's image - a screenshot, one copied from a browser - as
    // the cover, written at once like Change. See ClipboardArt.
    private async void PasteArtButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (await ClipboardArt.TryReadPngAsync(Clipboard) is not { } png)
        {
            ArtworkInfoText.Text = "There's no image on the clipboard.";
            return;
        }
        await WriteArtAsync(png, ClipboardArt.MimeType);
    }

    // A cover found online for the album the Info tab's boxes name - the
    // boxes rather than the song, so a misspelt album can be corrected and
    // searched again before OK. Chosen over the whole window
    // (OnlineCoverPicker, shared with the phone's sheet), and written at once
    // like Change, once Use This Cover is pressed.
    private async void FindArtButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_editTracks.Count == 0 || !ArtEditor().CanWrite)
            return;

        var track = _editTracks[0];
        var album = OnlineCoverPicker.FirstNonBlank(_info.Album.Text, track.Album);
        var artist = OnlineCoverPicker.FirstNonBlank(_info.AlbumArtists.Text, _info.Artists.Text, track.EffectiveAlbumArtist);
        var title = OnlineCoverPicker.FirstNonBlank(_info.Title.Text, track.Title);

        StopFindingArt();
        var find = _findArt = new CancellationTokenSource();
        FindArtButton.IsEnabled = false;
        FindArtButton.Content = "Searching…";

        var finder = new OnlineCoverArt(_logger);
        IReadOnlyList<OnlineCoverArt.Candidate>? found;
        try
        {
            found = await finder.SearchAsync(album, artist, title, find.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            FindArtButton.Content = "Find Online…";
            FindArtButton.IsEnabled = ArtEditor().CanWrite;
        }

        if (find.IsCancellationRequested)
            return;
        if (OnlineCoverPicker.NothingFound(found, album, title) is { } nothing)
        {
            ArtworkInfoText.Text = nothing;
            return;
        }

        var viewer = _viewer = new ArtworkViewer { OffersChoice = true };
        viewer.Closed += (_, _) =>
        {
            find.Cancel();
            if (_viewer == viewer)
                _viewer = null;
        };
        viewer.Open(this);
        var message = await OnlineCoverPicker.ChooseAsync(viewer, finder, found!,
            WriteArtAsync,
            UpdatingFilesText,
            find.Token);
        if (message is not null && !find.IsCancellationRequested)
            ArtworkInfoText.Text = message;
    }

    private void StopFindingArt()
    {
        _findArt?.Cancel();
        _findArt = null;
        _viewer?.Close();
    }

    // "Updating 12 files…": how many songs will have the picture rewritten
    // into them, which for a song of the server's is its whole album
    // (AlbumArtEditor.FileCount).
    private string UpdatingFilesText()
    {
        var count = ArtEditor().FileCount;
        return count == 1 ? "Updating 1 file\u2026" : $"Updating {count} files\u2026";
    }

    private void OpenArtButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        OpenArtworkFullSize();

    // Double-clicking the art does what the button does, from either place the
    // art is shown - the header thumbnail as well as the Artwork tab's preview.
    // Both are too small to judge a cover by, and double-click is what opening
    // an image means everywhere else.
    private void Artwork_DoubleTapped(object? sender, TappedEventArgs e)
    {
        e.Handled = true;
        OpenArtworkFullSize();
    }

    private void OpenArtworkFullSize()
    {
        if (_art?.Bytes is not { Length: > 0 } bytes)
            return;

        Bitmap full;
        try
        {
            using var ms = new MemoryStream(bytes);
            full = new Bitmap(ms);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not decode album art at full size");
            return;
        }

        // Opened at the art's own pixel size, shrunk only as far as the screen
        // forces - the whole point of this window is to see detail the 140pt
        // header thumbnail and the tab's letterboxed preview both throw away.
        var screen = Screens.ScreenFromWindow(this);
        var scaling = screen?.Scaling ?? 1;
        var maxWidth = (screen?.WorkingArea.Width ?? 1600) / scaling * 0.9;
        var maxHeight = (screen?.WorkingArea.Height ?? 1000) / scaling * 0.9;
        var factor = Math.Min(1, Math.Min(maxWidth / full.PixelSize.Width, maxHeight / full.PixelSize.Height));

        var viewer = new Window
        {
            Title = _info.ArtworkTitle,
            Width = Math.Max(160, full.PixelSize.Width * factor),
            Height = Math.Max(160, full.PixelSize.Height * factor),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Content = new Image { Source = full, Stretch = Stretch.Uniform },
        };
        viewer.Closed += (_, _) => full.Dispose();
        viewer.Show(this);
    }

    // ── Dragging the art out ───────────────────────────────────────────────
    //
    // The reverse of the drop handler above: drag the preview onto a Finder
    // window (or anything else that takes a file) and get the full-size
    // original, not the downscaled bitmap on screen. Embedded art has no file
    // of its own to hand over, so one is written into the temp directory on the
    // way out - which is also why this offers a *copy* and never a move.
    //
    // Started from a pointer move past a threshold rather than from the press
    // itself, which is what the drop-source samples do: beginning a drag on
    // press swallows the second click of a double-click, and double-clicking
    // the art is how it opens full size.
    private Point? _artDragOrigin;
    private PointerPressedEventArgs? _artDragPress;
    private bool _artDragStarted;

    private void ArtDropTarget_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ArtDropTarget).Properties.IsLeftButtonPressed)
            return;

        _artDragOrigin = e.GetPosition(ArtDropTarget);
        _artDragPress = e;
        _artDragStarted = false;
    }

    private void ArtDropTarget_PointerReleased(object? sender, PointerReleasedEventArgs e) =>
        ResetArtDrag();

    private void ResetArtDrag()
    {
        _artDragOrigin = null;
        _artDragPress = null;
        _artDragStarted = false;
    }

    private async void ArtDropTarget_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_artDragOrigin is not { } origin || _artDragPress is not { } press || _artDragStarted)
            return;
        if (_art?.Bytes is not { Length: > 0 })
            return;

        var moved = e.GetPosition(ArtDropTarget) - origin;
        if (Math.Abs(moved.X) < DragThreshold && Math.Abs(moved.Y) < DragThreshold)
            return;

        // Set before the await, not after: the drag is modal on some platforms
        // and further moves keep arriving here until it ends.
        _artDragStarted = true;

        try
        {
            if (await ExportArtworkFileAsync() is not { } file)
                return;

            // Not disposed here by contract - DoDragDropAsync owns the
            // IDataTransfer once it is handed over and disposes it itself when
            // the drag ends.
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateFile(file));
            await DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            // A drag that cannot start is not worth interrupting the window
            // over: the Open Full Size button and the file it wrote are both
            // still there.
            _logger.LogDebug(ex, "Could not start a drag of the album art");
        }
        finally
        {
            ResetArtDrag();
        }
    }

    private const double DragThreshold = 4;

    // The art written out as a real file for the drag to carry, named after the
    // album so it does not land on someone's desktop as a hex string. In the
    // temp directory rather than anywhere of ours: the copy exists only to be
    // dragged somewhere, and the OS cleans up after us if it never is.
    private async Task<IStorageFile?> ExportArtworkFileAsync()
    {
        if (_art?.Bytes is not { Length: > 0 } bytes)
            return null;
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storageProvider)
            return null;

        var mimeType = string.IsNullOrEmpty(_art.MimeType)
            ? LocalAlbumArtReader.MimeTypeForBytes(bytes)
            : _art.MimeType;
        var name = SafeFileName(_editTracks.FirstOrDefault()?.Album ?? _editTracks.FirstOrDefault()?.Title ?? "Cover");
        var path = Path.Combine(Path.GetTempPath(), name + LocalAlbumArtReader.ExtensionForMimeType(mimeType));

        await File.WriteAllBytesAsync(path, bytes);
        return await storageProvider.TryGetFileFromPathAsync(path);
    }

    private static string SafeFileName(string name)
    {
        var cleaned = new string([.. name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)]).Trim();
        return cleaned.Length == 0 ? "Cover" : cleaned;
    }

    private void UpdateArtworkTab()
    {
        var writable = ArtWriteTargets().Count;
        var serverAlbums = writable == 0 ? ServerArtAlbumIds().Count : 0;
        var canWrite = writable > 0 || serverAlbums > 0;
        var hasArt = _art?.Bytes is { Length: > 0 };

        ArtDropHint.IsVisible = !hasArt && canWrite;
        ChangeArtButton.IsEnabled = canWrite;
        PasteArtButton.IsEnabled = canWrite;
        FindArtButton.IsEnabled = canWrite;
        RemoveArtButton.IsEnabled = canWrite && hasArt;
        OpenArtButton.IsEnabled = hasArt;

        ArtworkInfoText.Text = _info.DescribeArtwork(_art, _artPixelSize, writable, serverAlbums);
    }
}
