using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Flower.Logging;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile;

// Mobile counterpart to the desktop TrackInfoWindow, rendered as a sheet
// within MobileMainView rather than a child Window (mobile has no concept of
// secondary windows). What it shows and edits is TrackInfoViewModel's, the
// same as the desktop's; what stays here is the sheet's own - the cover and
// the ways to change it, the suggestion chips, the genre menu.
public partial class TrackInfoView : UserControl
{
    // This screen is instantiated by XAML and reaches its dependencies through
    // the ViewModel it is given, not through the container - see
    // docs/ARCHITECTURE-REVIEW.md Tier 2.3. The logger too, from the
    // container's factory the main view model carries.
    private ILogger _logger =>
        (DataContext as MobileMainViewModel)?.Main.LoggerFactory.CreateLogger<TrackInfoView>()
        ?? NullLogger<TrackInfoView>.Instance;
    // The songs being edited - one, or every song of an album - and the first
    // of them, whose cover the card shows when they share one. A new one each time the sheet shows
    // songs, so the suggestion pools are read fresh: a genre typed into
    // another song a minute ago belongs in the list now.
    private TrackInfoViewModel? _info;
    private IReadOnlyList<Track> _tracks => _info?.Tracks ?? [];
    private Track? _track => _tracks.Count > 0 ? _tracks[0] : null;
    private int _artRequestId; // guards against a stale track's art load winning a race
    // The cover's own bytes, undecoded - what the resolution under it is
    // measured from and what tapping it opens at full size.
    private byte[]? _artBytes;
    // The full-screen cover, while it is open - see ArtworkViewer.
    private ArtworkViewer? _viewer;
    // A Find Online still searching or downloading, dropped with the sheet.
    private CancellationTokenSource? _findArt;
    // A cover chosen but not yet saved, and whether Save is writing it.
    private StagedArt? _staged;
    private bool _writingArt;

    // The library's own spellings, offered under a box as it is typed in.
    private readonly List<TagSuggestions> _suggestions = [];

    public TrackInfoView()
    {
        InitializeComponent();
        // Nothing until there are songs to show: left to inherit, the body
        // would bind its Track Info against the main view model.
        Body.DataContext = null;

        _suggestions.Add(new TagSuggestions(ArtistBox, ArtistSuggestions));
        _suggestions.Add(new TagSuggestions(AlbumBox, AlbumSuggestions));
        _suggestions.Add(new TagSuggestions(AlbumArtistBox, AlbumArtistSuggestions));
        _suggestions.Add(new TagSuggestions(ComposerBox, ComposerSuggestions));
        _suggestions.Add(new TagSuggestions(GenreBox, GenreSuggestions));

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MobileMainViewModel vm)
                vm.PropertyChanged += Vm_PropertyChanged;
        };
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MobileMainViewModel vm)
            return;
        if (e.PropertyName is nameof(MobileMainViewModel.TrackInfoTracks) or nameof(MobileMainViewModel.ActiveSheet))
        {
            // The full-screen cover sits above the sheet rather than in it, so
            // it would outlive a sheet closed under it (Android's back). An
            // unsaved cover goes with the sheet.
            if (!vm.IsShowingTrackInfo)
            {
                _findArt?.Cancel();
                CloseArtwork();
                _populatedFrom = null;
            }

            if (vm.IsShowingTrackInfo && vm.TrackInfoTracks.Count > 0 && !ReferenceEquals(vm.TrackInfoTracks, _populatedFrom))
                Populate(vm, vm.TrackInfoTracks);
        }
    }

    // The list this sheet was last filled from. Not _tracks: Load swaps in the
    // library's own songs, so that is a different list.
    private IReadOnlyList<Track>? _populatedFrom;

    private void Populate(MobileMainViewModel vm, IReadOnlyList<Track> tracks)
    {
        _populatedFrom = tracks;
        var info = _info = vm.Main.TrackInfo();
        info.Load(tracks);
        Body.DataContext = info;

        // Shown but not editable when the songs are not this phone's to edit,
        // rather than editable and ignored on Save.
        SaveButton.IsVisible = info.IsEditable;

        _suggestions[0].Pool = info.ArtistSuggestions;
        _suggestions[1].Pool = info.AlbumSuggestions;
        _suggestions[2].Pool = info.ArtistSuggestions;
        _suggestions[3].Pool = info.ComposerSuggestions;
        _suggestions[4].Pool = info.GenreSuggestions;

        GenreMenuButton.IsVisible = info.IsEditable && info.GenreSuggestions.Count > 0;

        // Only where a cover can be written: a file on this phone, or - for a
        // song of the paired server's - an admin's right to have the server
        // write it (AlbumArtEditor.CanWrite).
        _findArt?.Cancel();
        CloseArtwork();
        _staged = null;
        ArtButtons.IsVisible = ArtEditor() is { CanWrite: true };
        SetArtBusy(false);
        ArtMessage.IsVisible = false;
        ArtSearchRow.IsVisible = false;

        if (_track is { } first)
            _ = LoadAlbumArtAsync(first);
    }

    // Every song being edited: a cover pasted over an album's songs is the
    // album's cover.
    private AlbumArtEditor? ArtEditor() =>
        _tracks.Count > 0 && DataContext is MobileMainViewModel vm
            ? vm.Main.ArtEditorFor(_tracks, _logger)
            : null;

    // ── Cover ──────────────────────────────────────────────────────────────
    //
    // As on the desktop, a new cover is only shown (StageArtAsync) until Save
    // or Previous/Next writes it (WriteStagedArtAsync). Closing the sheet
    // leaves the files alone.

    // A picture from the phone: the photo library on iOS, which Avalonia opens
    // for a picker asking for images alone, and the system picker narrowed to
    // images on Android. Read through the stream rather than a path, which an
    // Android content URI does not have.
    private async void ChoosePhoto_Click(object? sender, RoutedEventArgs e)
    {
        if (_track is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage)
            return;

        ArtMessage.IsVisible = false;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a photo",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });
        if (files.FirstOrDefault() is not { } file)
            return;

        byte[] bytes;
        try
        {
            await using var stream = await file.OpenReadAsync();
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            bytes = copy.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the chosen photo {Name}", file.Name);
            ShowArtMessage("That photo couldn't be read.");
            return;
        }
        finally
        {
            file.Dispose();
        }

        // A format the tags have a name for goes in as it is; anything else
        // Skia can still decode goes in as PNG.
        if (LocalAlbumArtReader.MimeTypeForBytes(bytes) is { } mimeType)
            await StageArtAsync(bytes, mimeType);
        else if (await Task.Run(() => ReencodeAsPng(bytes)) is { } png)
            await StageArtAsync(png, ClipboardArt.MimeType);
        else
            ShowArtMessage("That photo isn't in a format Flower can read.");
    }

    // The clipboard's image, as the cover.
    private async void PasteArt_Click(object? sender, RoutedEventArgs e)
    {
        if (_track is null)
            return;

        ArtMessage.IsVisible = false;
        var png = await ClipboardArt.TryReadPngAsync(TopLevel.GetTopLevel(this)?.Clipboard);
        if (png is null)
        {
            ShowArtMessage("There's no image on the clipboard. Copy one, then tap Paste again.");
            return;
        }

        await StageArtAsync(png, ClipboardArt.MimeType);
    }

    // A cover found online for the album the boxes name (OnlineCoverArt),
    // chosen on the whole screen - see OnlineCoverPicker, which the desktop's
    // Track Info shares.
    private async void FindArt_Click(object? sender, RoutedEventArgs e)
    {
        if (_track is not { } track)
            return;

        await FindArtAsync(track,
            OnlineCoverPicker.FirstNonBlank(_info?.Album.Text, track.Album),
            OnlineCoverPicker.FirstNonBlank(_info?.AlbumArtists.Text, _info?.Artists.Text, track.EffectiveAlbumArtist));
    }

    // The search row's words instead of the tags' - see ArtSearchRow.
    private async void ArtSearch_Click(object? sender, RoutedEventArgs e)
    {
        if (_track is { } track)
            await FindArtAsync(track, ArtSearchAlbum.Text?.Trim() ?? "", ArtSearchArtist.Text?.Trim() ?? "");
    }

    private async void ArtSearchRow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _track is not { } track)
            return;
        e.Handled = true;
        await FindArtAsync(track, ArtSearchAlbum.Text?.Trim() ?? "", ArtSearchArtist.Text?.Trim() ?? "");
    }

    // A search that finds nothing offers the row, holding what it searched
    // for, so the words can be changed and tried again.
    private async Task FindArtAsync(Track track, string album, string artist)
    {
        if (!FindArtButton.IsEnabled)
            return;

        var title = OnlineCoverPicker.FirstNonBlank(_info?.Title.Text, track.Title);

        _findArt?.Cancel();
        var find = _findArt = new CancellationTokenSource();
        ArtMessage.IsVisible = false;
        FindArtButton.IsEnabled = false;
        ArtSearchButton.IsEnabled = false;
        FindArtLabel.Text = "Searching…";

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
            FindArtButton.IsEnabled = true;
            ArtSearchButton.IsEnabled = true;
            FindArtLabel.Text = "Find online";
        }

        if (find.IsCancellationRequested || _track != track)
            return;
        if (OnlineCoverPicker.NothingFound(found, album, title) is { } nothing)
        {
            ShowArtMessage(nothing);
            if (found is { Count: 0 })
            {
                ArtSearchAlbum.Text = album;
                ArtSearchArtist.Text = artist;
                ArtSearchRow.IsVisible = true;
            }
            return;
        }

        var viewer = OpenArtwork(offersChoice: true);
        viewer.Closed += (_, _) => find.Cancel();
        var message = await OnlineCoverPicker.ChooseAsync(viewer, finder, found!,
            (bytes, mimeType) => _track == track ? StageArtAsync(bytes, mimeType) : Task.CompletedTask,
            () => "Using this cover\u2026",
            find.Token);
        if (message is not null && _track == track)
            ShowArtMessage(message);
    }

    // The bin on the cover. Not confirmed: closing the sheet undoes it.
    private async void RemoveArt_Click(object? sender, RoutedEventArgs e) =>
        await StageArtAsync(null, null);

    // Every way of changing the cover ends here; null bytes for none.
    // Decoded first, so an unreadable picture is refused now, not on Save.
    // Bumps _artRequestId so an older load cannot paint over it.
    private async Task StageArtAsync(byte[]? bytes, string? mimeType)
    {
        if (_writingArt || ArtEditor() is not { CanWrite: true })
            return;

        // A cover chosen some other way leaves nothing to search for again.
        ArtSearchRow.IsVisible = false;
        var requestId = ++_artRequestId;
        var decoded = bytes is null ? null : await Task.Run(() => ArtworkViewer.DecodeForScreen(bytes));
        if (requestId != _artRequestId)
            return;
        if (bytes is not null && decoded is null)
        {
            ShowArtMessage("That picture couldn't be read.");
            return;
        }

        _staged = new StagedArt(bytes, mimeType);
        _artBytes = bytes;
        ShowMultipleArt(false);
        ArtView.AlbumArt = decoded?.Bitmap;
        DescribeArt(decoded?.Size, bytes);
        RemoveArtPill.IsVisible = bytes is not null;
        // Save may be hidden for tags this phone cannot edit.
        SaveButton.IsVisible = true;
        ArtMessage.IsVisible = false;
    }

    // The tags, then the staged cover. False, scrolled to the error, when
    // either fails; the cover stays staged so Save can retry.
    private async Task<bool> SaveAsync()
    {
        if (_writingArt || _info is not { } info)
            return false;

        info.Save();
        if (info.SaveError != null || !await WriteStagedArtAsync())
        {
            Body.ScrollToHome();
            return false;
        }
        return true;
    }

    private async Task<bool> WriteStagedArtAsync()
    {
        if (_staged is not { } staged || ArtEditor() is not { CanWrite: true } editor)
            return true;

        var sheet = _populatedFrom;
        var total = editor.FileCount;
        // A report can land after the sheet was refilled for another song.
        var progress = new Progress<int>(done =>
        {
            if (_populatedFrom == sheet)
                ArtProgress.Text = TrackInfoViewModel.FilesWritten(done, total);
        });
        ArtProgress.Text = TrackInfoViewModel.FilesWritten(0, total);
        ArtMessage.IsVisible = false;
        SetArtBusy(true);
        string? message;
        try
        {
            message = staged.Bytes is { } bytes
                ? await editor.WriteAsync(bytes, staged.MimeType!, progress)
                : await editor.RemoveAsync(progress);
        }
        finally
        {
            // The sheet was closed and refilled meanwhile; leave it be.
            if (_populatedFrom == sheet)
                SetArtBusy(false);
        }
        if (_populatedFrom != sheet)
            return false;

        if (message != null)
        {
            ShowArtMessage(message);
            return false;
        }

        _staged = null;
        ArtMessage.IsVisible = false;
        return true;
    }

    // A spinner over the cover, and nothing to change it or leave by.
    private void SetArtBusy(bool busy)
    {
        _writingArt = busy;
        ArtBusy.IsVisible = busy;
        SaveButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        PreviousSongButton.IsEnabled = !busy;
        NextSongButton.IsEnabled = !busy;
        ChoosePhotoButton.IsEnabled = !busy;
        FindArtButton.IsEnabled = !busy;
        ArtSearchButton.IsEnabled = !busy;
        PasteArtButton.IsEnabled = !busy;
        RemoveArtButton.IsEnabled = !busy;
    }

    private void ShowArtMessage(string message)
    {
        ArtMessage.Text = message;
        ArtMessage.IsVisible = true;
    }

    // The cover on the card, and under it its real size: measured from the
    // undecoded bytes (AlbumArtLoader.TryGetArt), since the bitmap the card
    // draws is capped at the loader's display size and would report the cap.
    // Songs that do not share one cover (AlbumArtLoader.ShareOneCover) show
    // "Multiple Album Art" instead - the first one's would read as everyone's.
    private async Task LoadAlbumArtAsync(Track track)
    {
        var requestId = ++_artRequestId;
        ArtView.AlbumArt = null;
        ArtResolution.IsVisible = false;
        RemoveArtPill.IsVisible = false;
        ShowMultipleArt(false);
        _artBytes = null;

        var tracks = _tracks;
        if (tracks.Count > 1 && !await Task.Run(() => AlbumArtLoader.ShareOneCover(tracks)))
        {
            if (requestId != _artRequestId)
                return;
            ShowMultipleArt(true);
            RemoveArtPill.IsVisible = ArtButtons.IsVisible;
            return;
        }

        var bitmap = await AlbumArtLoader.Current.LoadAsync(track);
        var (bytes, size) = await Task.Run(() =>
        {
            var art = AlbumArtLoader.TryGetArt(track);
            return (art?.Bytes, art is null ? null : AlbumArtLoader.MeasureArt(art.Bytes));
        });
        if (requestId != _artRequestId)
            return;

        ArtView.AlbumArt = bitmap;
        _artBytes = bytes;
        DescribeArt(size, bytes);
        RemoveArtPill.IsVisible = bitmap is not null && ArtButtons.IsVisible;
    }

    private void DescribeArt(PixelSize? size, byte[]? bytes)
    {
        var described = string.Join(" · ", new[]
        {
            size is { } pixels ? TrackInfoViewModel.Resolution(pixels) : null,
            bytes is { Length: > 0 } ? TrackInfoViewModel.FileSize(bytes.Length) : null,
        }.Where(part => part != null));
        ArtResolution.Text = described;
        ArtResolution.IsVisible = described.Length > 0;
    }

    private void ShowMultipleArt(bool multiple)
    {
        MultipleArtLabel.IsVisible = multiple;
        ArtView.Classes.Set("multipleArt", multiple);
    }

    private static byte[]? ReencodeAsPng(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            using var bitmap = new Bitmap(stream);
            using var png = new MemoryStream();
            bitmap.Save(png, PngBitmapEncoderOptions.Default);
            return png.ToArray();
        }
        catch
        {
            return null;
        }
    }

    // Tapping the cover shows it on the whole screen, at its own size.
    private async void Art_Tapped(object? sender, TappedEventArgs e)
    {
        if (_artBytes is not { } bytes)
            return;

        e.Handled = true;
        var viewer = OpenArtwork(offersChoice: false);
        if (await Task.Run(() => ArtworkViewer.DecodeForScreen(bytes)) is not { } picture)
        {
            viewer.ShowStatus("This cover couldn't be read.");
            return;
        }
        if (!viewer.IsOpen)
        {
            picture.Bitmap.Dispose();
            return;
        }
        viewer.ShowPicture(picture.Bitmap, TrackInfoViewModel.Resolution(picture.Size));
    }

    private ArtworkViewer OpenArtwork(bool offersChoice)
    {
        CloseArtwork();
        var viewer = _viewer = new ArtworkViewer { OffersChoice = offersChoice };
        viewer.Closed += (_, _) =>
        {
            if (_viewer == viewer)
                _viewer = null;
        };
        viewer.Open(this);
        return viewer;
    }

    private void CloseArtwork() => _viewer?.Close();

    // ── Genre ──────────────────────────────────────────────────────────────

    private void GenreMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (_info is not { } info)
            return;

        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var genre in info.GenreSuggestions)
        {
            var item = new MenuItem { Header = genre };
            item.Click += (_, _) => info.Genre.Text = genre;
            flyout.Items.Add(item);
        }

        flyout.ShowAt(GenreMenuButton);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        (DataContext as MobileMainViewModel)?.CloseSheetCommand.Execute(null);
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (await SaveAsync())
            (DataContext as MobileMainViewModel)?.CloseSheetCommand.Execute(null);
    }

    private void Previous_Click(object? sender, RoutedEventArgs e) => Step(-1);
    private void Next_Click(object? sender, RoutedEventArgs e) => Step(+1);

    // Saved before moving on, as the desktop window's Prev/Next do: stepping
    // to the next song is not leaving, so what was typed and chosen is kept.
    private async void Step(int delta)
    {
        if (await SaveAsync())
            (DataContext as MobileMainViewModel)?.StepTrackInfo(delta);
    }

    // Bytes null for taking the cover out.
    private sealed record StagedArt(byte[]? Bytes, string? MimeType);

    // The library's own spellings under a box, as it is typed in: names
    // starting with the text, then names containing it (TagSuggestionSource
    // .Matching), as a row of chips - the desktop's dropdown would open where
    // a phone's keyboard is. A tap puts the name in the box. Shown only while
    // the box has the keyboard.
    private sealed class TagSuggestions
    {
        private readonly TextBox _box;
        private readonly WrapPanel _chips;

        public IReadOnlyList<string> Pool { get; set; } = [];

        public TagSuggestions(TextBox box, WrapPanel chips)
        {
            _box = box;
            _chips = chips;
            box.TextChanged += (_, _) => Refresh();
            box.GotFocus += (_, _) => Refresh();
            box.LostFocus += (_, _) => _chips.IsVisible = false;
        }

        private void Refresh()
        {
            _chips.Children.Clear();
            if (!_box.IsFocused)
            {
                _chips.IsVisible = false;
                return;
            }

            foreach (var name in TagSuggestionSource.Matching(Pool, _box.Text))
            {
                var chip = new Button { Content = name };
                chip.Click += (_, _) =>
                {
                    _box.Text = name;
                    _box.CaretIndex = name.Length;
                    _chips.IsVisible = false;
                };
                _chips.Children.Add(chip);
            }
            _chips.IsVisible = _chips.Children.Count > 0;
        }
    }
}
