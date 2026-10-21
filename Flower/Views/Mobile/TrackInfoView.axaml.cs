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
    // of them, whose cover the card shows. A new one each time the sheet shows
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
            // it would outlive a sheet closed under it (Android's back).
            if (!vm.IsShowingTrackInfo)
            {
                _findArt?.Cancel();
                CloseArtwork();
            }

            if (vm.IsShowingTrackInfo && vm.TrackInfoTracks.Count > 0 && !ReferenceEquals(vm.TrackInfoTracks, _tracks))
                Populate(vm, vm.TrackInfoTracks);
        }
    }

    private void Populate(MobileMainViewModel vm, IReadOnlyList<Track> tracks)
    {
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
        ArtButtons.IsVisible = ArtEditor() is { CanWrite: true };
        SetArtBusy(false);
        ArtMessage.IsVisible = false;

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

    // A picture from the phone: the photo library on iOS, which Avalonia opens
    // for a picker asking for images alone, and the system picker narrowed to
    // images on Android. Read through the stream rather than a path, which an
    // Android content URI does not have.
    private async void ChoosePhoto_Click(object? sender, RoutedEventArgs e)
    {
        if (_track is not { } track || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage)
            return;

        ArtMessage.IsVisible = false;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a Photo",
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
            await WriteArtAsync(track, bytes, mimeType);
        else if (await Task.Run(() => ReencodeAsPng(bytes)) is { } png)
            await WriteArtAsync(track, png, ClipboardArt.MimeType);
        else
            ShowArtMessage("That photo isn't in a format Flower can read.");
    }

    // The clipboard's image, as the song's cover - written straight into the
    // file (or by the server, for one of its songs), and the card's cover
    // redrawn from it.
    private async void PasteArt_Click(object? sender, RoutedEventArgs e)
    {
        if (_track is not { } track)
            return;

        ArtMessage.IsVisible = false;
        var png = await ClipboardArt.TryReadPngAsync(TopLevel.GetTopLevel(this)?.Clipboard);
        if (png is null)
        {
            ShowArtMessage("There's no image on the clipboard. Copy one, then tap Paste again.");
            return;
        }

        await WriteArtAsync(track, png, ClipboardArt.MimeType);
    }

    // A cover found online for the album the boxes name (OnlineCoverArt) -
    // the boxes rather than the song, so a misspelt album can be corrected
    // and searched again before Save. What comes back is shown on the whole
    // screen, one candidate at a time, and only Use This Cover writes it.
    private async void FindArt_Click(object? sender, RoutedEventArgs e)
    {
        if (_track is not { } track)
            return;

        var album = FirstNonBlank(_info?.Album.Text, track.Album);
        var artist = FirstNonBlank(_info?.AlbumArtists.Text, _info?.Artists.Text, track.EffectiveAlbumArtist);
        var title = FirstNonBlank(_info?.Title.Text, track.Title);

        _findArt?.Cancel();
        var find = _findArt = new CancellationTokenSource();
        ArtMessage.IsVisible = false;
        FindArtButton.IsEnabled = false;
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
            FindArtLabel.Text = "Find Online";
        }

        if (find.IsCancellationRequested || _track != track)
            return;
        if (found is null)
        {
            ShowArtMessage("Couldn't search online. Check the connection, then try again.");
            return;
        }
        if (found.Count == 0)
        {
            var named = string.IsNullOrWhiteSpace(album) ? title : album;
            ShowArtMessage($"No cover found online for \u201c{named}\u201d. Check the album and artist below, then try again.");
            return;
        }

        var viewer = OpenArtwork(offersChoice: true);
        viewer.SetHasNext(found.Count > 1);
        var index = 0;
        var shown = 0;
        byte[]? picked = null;

        async Task ShowCandidateAsync()
        {
            var showing = ++shown;
            var candidate = found[index];
            picked = null;
            viewer.ShowStatus("Downloading…", busy: true);

            byte[] bytes;
            try
            {
                bytes = await finder.DownloadAsync(candidate, find.Token);
            }
            catch (Exception ex) when (!find.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not download the cover found for {Album}", candidate.Album);
                if (showing == shown)
                    viewer.ShowStatus("This cover couldn't be downloaded.");
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var decoded = await Task.Run(() => DecodeForScreen(bytes));
            if (showing != shown || !viewer.IsOpen)
            {
                decoded?.Bitmap.Dispose();
                return;
            }
            if (decoded is not { } picture)
            {
                viewer.ShowStatus("This cover couldn't be read.");
                return;
            }

            picked = bytes;
            var position = found.Count > 1 ? $" \u00b7 {index + 1} of {found.Count}" : "";
            viewer.ShowPicture(picture.Bitmap,
                string.IsNullOrEmpty(candidate.Artist) ? candidate.Album : $"{candidate.Album} \u2014 {candidate.Artist}",
                TrackInfoViewModel.Resolution(picture.Size) + position);
        }

        viewer.NextRequested += async (_, _) =>
        {
            index = (index + 1) % found.Count;
            await ShowCandidateAsync();
        };
        viewer.Accepted += async (_, _) =>
        {
            if (picked is not { } bytes)
                return;
            viewer.SetBusy(true, UpdatingFilesText());
            await WriteArtAsync(track, bytes, LocalAlbumArtReader.MimeTypeForBytes(bytes) ?? "image/jpeg");
            viewer.Close();
        };
        viewer.Closed += (_, _) => find.Cancel();

        await ShowCandidateAsync();
    }

    private static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    private Task WriteArtAsync(Track track, byte[] bytes, string mimeType) =>
        ChangeArtAsync(track, editor => editor.WriteAsync(bytes, mimeType));

    // The bin on the cover: the picture taken out of every song being edited.
    private async void RemoveArt_Click(object? sender, RoutedEventArgs e)
    {
        if (_track is { } track)
            await ChangeArtAsync(track, editor => editor.RemoveAsync());
    }

    // Every way of changing the cover ends here: written into every song
    // being edited, and the card's cover read back from what was written.
    // Rewriting the files - or the server rewriting its own - takes a while
    // for an album, so the cover spins under the count of files until it is
    // done, and nothing else that would change it can be pressed meanwhile.
    private async Task ChangeArtAsync(Track track, Func<AlbumArtEditor, Task<string?>> change)
    {
        if (ArtEditor() is not { CanWrite: true } editor)
            return;

        SetArtBusy(true);
        ShowArtMessage(UpdatingFilesText(editor));
        string? message;
        try
        {
            message = await change(editor);
        }
        finally
        {
            SetArtBusy(false);
        }

        if (message is not null)
            ShowArtMessage(message);
        else
            ArtMessage.IsVisible = false;
        if (_track == track)
            await LoadAlbumArtAsync(track);
    }

    // "Updating 12 files…": how many songs will have the picture rewritten
    // into them, which for a song of the server's is its whole album
    // (AlbumArtEditor.FileCount).
    private string UpdatingFilesText(AlbumArtEditor? editor = null)
    {
        var count = (editor ?? ArtEditor())?.FileCount ?? _tracks.Count;
        return count == 1 ? "Updating 1 file\u2026" : $"Updating {count} files\u2026";
    }

    private void SetArtBusy(bool busy)
    {
        ArtBusy.IsVisible = busy;
        ChoosePhotoButton.IsEnabled = !busy;
        FindArtButton.IsEnabled = !busy;
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
    private async Task LoadAlbumArtAsync(Track track)
    {
        var requestId = ++_artRequestId;
        ArtView.AlbumArt = null;
        ArtResolution.IsVisible = false;
        RemoveArtPill.IsVisible = false;
        _artBytes = null;

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
        ArtResolution.Text = size is { } pixels ? TrackInfoViewModel.Resolution(pixels) : "";
        ArtResolution.IsVisible = size is not null;
        RemoveArtPill.IsVisible = bitmap is not null && ArtButtons.IsVisible;
    }

    // A picture for the full screen: its own size, or 2048 wide where it is
    // wider - sharp on any phone, without holding a 3000-pixel decode.
    private const int ScreenPixels = 2048;

    private static (Bitmap Bitmap, PixelSize Size)? DecodeForScreen(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            // The size from the header where it can be read, so the picture is
            // decoded once, at the size it is shown.
            if (ImageHeader.TryReadSize(stream) is { } header)
            {
                var wanted = new PixelSize(header.Width, header.Height);
                return (wanted.Width <= ScreenPixels ? new Bitmap(stream) : Bitmap.DecodeToWidth(stream, ScreenPixels), wanted);
            }

            var full = new Bitmap(stream);
            var size = full.PixelSize;
            if (size.Width <= ScreenPixels)
                return (full, size);

            full.Dispose();
            stream.Position = 0;
            return (Bitmap.DecodeToWidth(stream, ScreenPixels), size);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? ReencodeAsPng(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            using var bitmap = new Bitmap(stream);
            using var png = new MemoryStream();
            bitmap.Save(png);
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
        if (await Task.Run(() => DecodeForScreen(bytes)) is not { } picture)
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

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        _info?.Save();
        (DataContext as MobileMainViewModel)?.CloseSheetCommand.Execute(null);
    }

    private void Previous_Click(object? sender, RoutedEventArgs e) => Step(-1);
    private void Next_Click(object? sender, RoutedEventArgs e) => Step(+1);

    // Saved before moving on, as the desktop window's Prev/Next do: stepping
    // to the next song is not leaving, so what was typed for this one is kept.
    private void Step(int delta)
    {
        _info?.Save();
        (DataContext as MobileMainViewModel)?.StepTrackInfo(delta);
    }

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
