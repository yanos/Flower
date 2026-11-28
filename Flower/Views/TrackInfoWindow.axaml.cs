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

    // The songs edited together do not all carry one cover, so there is none
    // to show - see AlbumArtLoader.ShareOneCover.
    private bool _multipleArt;

    // A cover chosen but not yet written - see "Artwork tab" below - and
    // whether Save is writing one now.
    private StagedArt? _staged;
    private bool _writingArt;

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
        // before the pages are there - Details is the one showing by default.
        if (DetailsPage is null)
            return;
        var pages = TabPagesInOrder;
        for (var i = 0; i < pages.Length; i++)
            pages[i].IsVisible = i == Tabs.SelectedIndex;
    }

    private Control[] TabPagesInOrder => [DetailsPage, ExtraPage, SortingPage, LyricsPage, ArtworkPage, FilePage];

    private void PrevButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Navigate(-1);
    private void NextButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Navigate(+1);

    private void UpdateNavButtons()
    {
        PrevButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index < _tracks.Count - 1;
    }

    // Saved before moving on: stepping to the next song is not leaving, so
    // what was typed for this one, and a cover chosen for it, are kept.
    private async void Navigate(int delta)
    {
        var next = _index + delta;
        if (next < 0 || next >= _tracks.Count)
            return;
        if (!await SaveAsync())
            return;

        _index = next;
        Populate([_track]);
        UpdateNavButtons();
        TrackNavigated?.Invoke(this, _track);
    }

    private void Populate(IReadOnlyList<Track> tracks)
    {
        StopFindingArt();
        ArtSearchRow.IsVisible = false;
        _staged = null;
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

    // Shows the songs' art (embedded tag picture, falling back to a
    // cover/folder image file - see AlbumArtLoader) where they share one, and
    // "Multiple Album Art" where they do not: the first song's cover would
    // read as everyone's.
    private async Task LoadAlbumArtAsync()
    {
        var requestId = ++_artRequestId;
        AlbumArtView.AlbumArt = null;
        ArtworkLargeView.AlbumArt = null;
        _art = null;
        _artPixelSize = null;
        _multipleArt = false;
        UpdateArtworkTab();
        if (_editTracks.Count == 0)
            return;

        var tracks = _editTracks;
        if (tracks.Count > 1 && !await Task.Run(() => AlbumArtLoader.ShareOneCover(tracks)))
        {
            if (requestId != _artRequestId)
                return;
            _multipleArt = true;
            UpdateArtworkTab();
            return;
        }

        var track = tracks[0];
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

    private async void OkButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (await SaveAsync())
            Close();
    }

    // The tags, as they always were, then a cover chosen since Load. False
    // when either did not reach every file, leaving the window open to say
    // why and to try again.
    private async Task<bool> SaveAsync()
    {
        _info.Save();
        if (_info.SaveError != null)
            return false;

        return await WriteStagedArtAsync();
    }

    private void CancelButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }

    // ── Artwork tab ────────────────────────────────────────────────────────
    //
    // Like every other field in this window, a new cover waits for Save:
    // dropping, choosing, pasting, finding one online or removing it only
    // changes what is shown (StageArtAsync), and Cancel leaves the files as
    // they were. Save writes it (WriteStagedArtAsync, through AlbumArtEditor)
    // after the tags, under a spinner and the count of files, since it is a
    // whole-file rewrite of every song edited - longer still for songs the
    // server rewrites. The window stays open until it is done, and says why
    // when it is not.
    //
    // "The file" is not always one of this device's: a track that only exists on
    // the paired server is written by asking that server to do it - see
    // AlbumArtEditor.ServerArtIds. Either way only the songs being edited
    // get the new cover, never the rest of their album.

    // Rebuilt per call because _editTracks is replaced on every Navigate();
    // it holds nothing but the list it is given.
    private AlbumArtEditor ArtEditor() => _main.ArtEditorFor(_editTracks, _logger);

    // Two drop targets for one cover: the Artwork tab's frame, and the
    // header's thumbnail, which is there whichever tab is showing.
    private void SetUpArtworkDropTarget()
    {
        Closed += (_, _) => StopFindingArt();
        foreach (var target in new Control[] { ArtDropTarget, HeaderArtDropTarget })
        {
            DragDrop.SetAllowDrop(target, true);
            target.AddHandler(DragDrop.DragOverEvent, ArtDropTarget_DragOver);
            target.AddHandler(DragDrop.DropEvent, ArtDropTarget_Drop);
        }
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

    // An image file, or a picture with no file behind it - one dragged out of
    // a browser or Photos, which the platform hands over as a bitmap.
    private static bool HoldsImage(IDataTransfer data) =>
        DroppedImagePath(data) != null || data.Contains(DataFormat.Bitmap);

    private void ArtDropTarget_DragOver(object? sender, DragEventArgs e)
    {
        var accepted = !_writingArt && ArtEditor().CanWrite && HoldsImage(e.DataTransfer);
        e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // A drop on the header from another tab that is refused shows the
    // Artwork tab, since that is where it says why.
    private async void ArtDropTarget_Drop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_writingArt || !ArtEditor().CanWrite)
            return;

        bool applied;
        if (DroppedImagePath(e.DataTransfer) is string path)
        {
            applied = await StageArtFromFileAsync(path);
        }
        else if (e.DataTransfer.Contains(DataFormat.Bitmap))
        {
            using var bitmap = e.DataTransfer.TryGetBitmap();
            if (ClipboardArt.EncodePng(bitmap) is { } png)
            {
                applied = await StageArtAsync(png, ClipboardArt.MimeType);
            }
            else
            {
                ArtworkInfoText.Text = "That image couldn't be read.";
                applied = false;
            }
        }
        else
        {
            return;
        }

        if (!applied && sender == HeaderArtDropTarget)
            Tabs.SelectedIndex = Array.IndexOf(TabPagesInOrder, ArtworkPage);
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
            await StageArtFromFileAsync(path);
    }

    // These return whether the picture was taken, and say why on the
    // Artwork tab when it was not.
    private async Task<bool> StageArtFromFileAsync(string path)
    {
        if (LocalAlbumArtReader.MimeTypeForExtension(Path.GetExtension(path)) is not string mimeType)
        {
            ArtworkInfoText.Text = "That file isn't an image format Flower can read.";
            return false;
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
            return false;
        }

        return await StageArtAsync(bytes, mimeType);
    }

    // Not a confirmation-guarded action even though it destroys data: like
    // every other change of cover it is only shown until Save, and Cancel
    // takes it back.
    private Task<bool> RemoveArtAsync() => StageArtAsync(null, null);

    // Every way of changing the cover ends here: the picture, or null for
    // none, shown in place of the cover until Save writes it. Decoded first,
    // so a file that only looks like an image is refused now rather than
    // written. Counts as a load of its own (_artRequestId), so one still
    // running from before cannot paint the old cover over it.
    private async Task<bool> StageArtAsync(byte[]? bytes, string? mimeType)
    {
        if (_writingArt || !ArtEditor().CanWrite)
            return false;

        // A cover chosen some other way leaves nothing to search for again.
        ArtSearchRow.IsVisible = false;
        var requestId = ++_artRequestId;
        var decoded = bytes is null ? null : await Task.Run(() => ArtworkViewer.DecodeForScreen(bytes));
        if (requestId != _artRequestId)
            return false;
        if (bytes is not null && decoded is null)
        {
            ArtworkInfoText.Text = "That image couldn't be read.";
            return false;
        }

        _staged = new StagedArt(bytes, mimeType);
        _art = bytes is null ? null : new LocalAlbumArt(bytes, mimeType!);
        _artPixelSize = decoded?.Size;
        _multipleArt = false;
        AlbumArtView.AlbumArt = decoded?.Bitmap;
        ArtworkLargeView.AlbumArt = decoded?.Bitmap;
        UpdateArtworkTab();
        return true;
    }

    // Kept staged when it fails, so Save tries again; writing the songs that
    // took it a second time changes nothing.
    private async Task<bool> WriteStagedArtAsync()
    {
        if (_staged is not { } staged)
            return true;

        var editor = ArtEditor();
        var total = editor.FileCount;
        void ShowProgress(int done) =>
            HeaderArtProgress.Text = ArtProgress.Text = TrackInfoViewModel.FilesWritten(done, total);
        ShowProgress(0);
        SetArtBusy(true);
        var progress = new Progress<int>(ShowProgress);
        string? message;
        try
        {
            message = staged.Bytes is { } bytes
                ? await editor.WriteAsync(bytes, staged.MimeType!, progress)
                : await editor.RemoveAsync(progress);
        }
        finally
        {
            SetArtBusy(false);
        }

        if (message != null)
        {
            ArtworkInfoText.Text = message;
            Tabs.SelectedIndex = Array.IndexOf(TabPagesInOrder, ArtworkPage);
            return false;
        }

        _staged = null;
        return true;
    }

    // The cover dims under a spinner, in the header and on the Artwork tab,
    // and nothing that would change it, save again or leave can be pressed
    // until the files are written.
    private void SetArtBusy(bool busy)
    {
        _writingArt = busy;
        HeaderArtBusy.IsVisible = busy;
        ArtBusy.IsVisible = busy;
        OkButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        if (busy)
        {
            PrevButton.IsEnabled = false;
            NextButton.IsEnabled = false;
        }
        else
        {
            UpdateNavButtons();
        }
        UpdateArtworkTab();
    }

    private async void RemoveArtButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await RemoveArtAsync();

    // The clipboard's image - a screenshot, one copied from a browser - as
    // the cover, staged like Change. See ClipboardArt.
    private async void PasteArtButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (await ClipboardArt.TryReadPngAsync(Clipboard) is not { } png)
        {
            ArtworkInfoText.Text = "There's no image on the clipboard.";
            return;
        }
        await StageArtAsync(png, ClipboardArt.MimeType);
    }

    // A cover found online for the album the Details tab's boxes name - the
    // boxes rather than the song, so a misspelt album can be corrected and
    // searched again before Save. Chosen over the whole window
    // (OnlineCoverPicker, shared with the phone's sheet), and staged like
    // Change once Use This Cover is pressed.
    private async void FindArtButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_editTracks.Count == 0)
            return;

        var track = _editTracks[0];
        await FindArtAsync(
            OnlineCoverPicker.FirstNonBlank(_info.Album.Text, track.Album),
            OnlineCoverPicker.FirstNonBlank(_info.AlbumArtists.Text, _info.Artists.Text, track.EffectiveAlbumArtist));
    }

    // The search row's words instead of the tags' - see ArtSearchRow.
    private async void ArtSearchButton_Click(object? sender, RoutedEventArgs e) =>
        await FindArtAsync(ArtSearchAlbum.Text?.Trim() ?? "", ArtSearchArtist.Text?.Trim() ?? "");

    private async void ArtSearchRow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await FindArtAsync(ArtSearchAlbum.Text?.Trim() ?? "", ArtSearchArtist.Text?.Trim() ?? "");
    }

    // A search that finds nothing offers the row, holding what it searched
    // for, so the words can be changed and tried again.
    private async Task FindArtAsync(string album, string artist)
    {
        if (_editTracks.Count == 0 || !ArtEditor().CanWrite || !FindArtButton.IsEnabled)
            return;

        var title = OnlineCoverPicker.FirstNonBlank(_info.Title.Text, _editTracks[0].Title);
        var year = OnlineCoverPicker.FirstNonBlank(_info.Year.Text, _editTracks[0].Year);

        StopFindingArt();
        var find = _findArt = new CancellationTokenSource();
        FindArtButton.IsEnabled = false;
        ArtSearchButton.IsEnabled = false;
        FindArtButton.Content = "Searching…";

        var finder = new OnlineCoverArt(_logger);
        IReadOnlyList<OnlineCoverArt.Candidate>? found;
        try
        {
            found = await finder.SearchAsync(album, artist, title, year, find.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            FindArtButton.Content = "Find online…";
            FindArtButton.IsEnabled = ArtEditor().CanWrite;
            ArtSearchButton.IsEnabled = true;
        }

        if (find.IsCancellationRequested)
            return;
        if (OnlineCoverPicker.NothingFound(found, album, title) is { } nothing)
        {
            ArtworkInfoText.Text = nothing;
            if (found is { Count: 0 })
            {
                ArtSearchAlbum.Text = album;
                ArtSearchArtist.Text = artist;
                ArtSearchRow.IsVisible = true;
            }
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
            StageArtAsync,
            () => "Using this cover\u2026",
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
        var editor = ArtEditor();
        var writable = editor.LocalTargets().Count;
        var serverTargets = writable == 0 ? editor.ServerArtIds().Count : 0;
        var canWrite = writable > 0 || serverTargets > 0;
        var hasArt = _art?.Bytes is { Length: > 0 };

        var changeable = canWrite && !_writingArt;

        ArtDropHint.IsVisible = !hasArt && changeable && !_multipleArt;
        MultipleArtLabel.IsVisible = _multipleArt;
        MultipleArtLargeLabel.IsVisible = _multipleArt;
        AlbumArtView.Classes.Set("multipleArt", _multipleArt);
        ChangeArtButton.IsEnabled = changeable;
        PasteArtButton.IsEnabled = changeable;
        FindArtButton.IsEnabled = changeable;
        RemoveArtButton.IsEnabled = changeable && (hasArt || _multipleArt);
        OpenArtButton.IsEnabled = hasArt;

        ArtworkInfoText.Text = _info.DescribeArtwork(_art, _artPixelSize, writable, serverTargets, _multipleArt);
    }

    // Bytes null for taking the cover out.
    private sealed record StagedArt(byte[]? Bytes, string? MimeType);
}
