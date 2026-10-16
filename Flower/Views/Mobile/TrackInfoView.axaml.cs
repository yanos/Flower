using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
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

using CommunityToolkit.Mvvm.DependencyInjection;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Flower.Converters;
using Flower.Logging;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile;

// Mobile counterpart to the desktop TrackInfoWindow: same TagLib-backed fields,
// but rendered as a sheet within MobileMainView rather than a child Window (mobile
// has no concept of secondary windows). Populated whenever the host view model's
// ActionTarget becomes the track shown by an open Track Info sheet.
public partial class TrackInfoView : UserControl
{
    private static readonly DurationConverter _durationConverter = new();
    // Resolved via the service locator - see desktop TrackInfoWindow's own
    // field for the same pattern.
    // This screen is instantiated by XAML and reaches its dependencies through
    // the ViewModel it is given, not through the container - see
    // docs/ARCHITECTURE-REVIEW.md Tier 2.3. The logger too, from the
    // container's factory the main view model carries.
    private ILogger _logger =>
        (DataContext as MobileMainViewModel)?.Main.LoggerFactory.CreateLogger<TrackInfoView>()
        ?? NullLogger<TrackInfoView>.Instance;
    // The songs being edited - one, or every song of an album - and the first
    // of them, whose cover the card shows.
    private IReadOnlyList<Track> _tracks = [];
    private Track? _track => _tracks.Count > 0 ? _tracks[0] : null;
    private int _artRequestId; // guards against a stale track's art load winning a race
    // The cover's own bytes, undecoded - what the resolution under it is
    // measured from and what tapping it opens at full size.
    private byte[]? _artBytes;
    // The full-screen cover, while it is open - see ArtworkViewer.
    private ArtworkViewer? _viewer;
    // A Find Online still searching or downloading, dropped with the sheet.
    private CancellationTokenSource? _findArt;

    // Each box and the tag it edits (TrackTagFields, shared with the desktop
    // window), with what it showed when the sheet opened - a box whose text
    // still matches is not saved, which is what leaves "Multiple values"
    // alone across several songs.
    private (TextBox Box, TrackTagField Field)[] _fields = [];
    private readonly Dictionary<TextBox, string> _shown = new();
    // What a box says when empty, as the XAML gave it - the lyrics box, which
    // has no name beside it - put back after "Multiple values".
    private readonly Dictionary<TextBox, string?> _placeholders = new();

    // The library's own spellings, offered under a box as it is typed in.
    private readonly List<TagSuggestions> _suggestions = [];

    public TrackInfoView()
    {
        InitializeComponent();
        _fields =
        [
            (TitleBox, TrackTagFields.Title),
            (ArtistBox, TrackTagFields.Artists),
            (AlbumBox, TrackTagFields.Album),
            (TrackNumBox, TrackTagFields.TrackNumber),
            (TrackTotalBox, TrackTagFields.TrackCount),
            (DiscNumBox, TrackTagFields.DiscNumber),
            (DiscTotalBox, TrackTagFields.DiscCount),
            (YearBox, TrackTagFields.Year),
            (GenreBox, TrackTagFields.Genre),
            (BpmBox, TrackTagFields.BeatsPerMinute),
            (KeyBox, TrackTagFields.InitialKey),
            (GroupingBox, TrackTagFields.Grouping),
            (AlbumArtistBox, TrackTagFields.AlbumArtists),
            (ComposerBox, TrackTagFields.Composers),
            (ConductorBox, TrackTagFields.Conductor),
            (RemixedByBox, TrackTagFields.RemixedBy),
            (SubtitleBox, TrackTagFields.Subtitle),
            (DescriptionBox, TrackTagFields.Description),
            (CommentBox, TrackTagFields.Comment),
            (PublisherBox, TrackTagFields.Publisher),
            (CopyrightBox, TrackTagFields.Copyright),
            (ISRCBox, TrackTagFields.ISRC),
            (LyricsBox, TrackTagFields.Lyrics),
        ];

        foreach (var (box, _) in _fields)
            _placeholders[box] = box.PlaceholderText;

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
                Populate(vm.TrackInfoTracks);
        }
    }

    // One value for the whole set when they agree, "Multiple values" when not.
    private const string MixedValues = "Multiple values";

    private string UniformOrMixed(Func<Track, string> display)
    {
        var values = _tracks.Select(display).Distinct().Take(2).ToList();
        return values.Count == 1 ? values[0] : MixedValues;
    }

    private void Populate(IReadOnlyList<Track> tracks)
    {
        _tracks = tracks;
        var vm = DataContext as MobileMainViewModel;

        // Shown but not editable when the songs are not this phone's to edit,
        // rather than editable and ignored on Save.
        var editable = vm != null && tracks.All(vm.Main.CanEditTagsOf);
        SaveButton.IsVisible = editable;

        _shown.Clear();
        foreach (var (box, field) in _fields)
        {
            box.IsEnabled = editable;
            var common = TrackTagFields.CommonValue(field, tracks);
            box.Text = common ?? "";
            box.PlaceholderText = common == null ? MixedValues : _placeholders.GetValueOrDefault(box);
            _shown[box] = common ?? "";
        }

        if (vm != null)
        {
            var library = vm.Main.Library.Tracks;
            var artists = TagSuggestionSource.DistinctArtists(library);
            _suggestions[0].Pool = artists;
            _suggestions[1].Pool = TagSuggestionSource.DistinctAlbums(library);
            _suggestions[2].Pool = artists;
            _suggestions[3].Pool = TagSuggestionSource.DistinctComposers(library);
            _suggestions[4].Pool = TagSuggestionSource.DistinctGenres(library);
        }

        // The same two numbers the desktop window shows - see its
        // UpdateListening for why a play count with no last-played date is
        // right (an iTunes import brings the count and no date).
        PlayCountValue.Text  = UniformOrMixed(t => t.TotalPlayCount.ToString(CultureInfo.CurrentCulture));
        // A count is a number at display size; the words for there being
        // several are not, and at that size they do not fit the card.
        PlayCountValue.FontSize = PlayCountValue.Text == MixedValues ? 15 : 22;
        LastPlayedValue.Text = UniformOrMixed(t => t.LastPlayedAt is { } at
            ? $"{at.LocalDateTime.ToString("MMM d, yyyy", CultureInfo.CurrentCulture)} {at.LocalDateTime.ToShortTimeString()}"
            : "—");

        DurationValue.Text   = UniformOrMixed(t => _durationConverter.Convert(t.Duration, typeof(string), null, CultureInfo.CurrentCulture) as string ?? "—");
        CodecValue.Text      = UniformOrMixed(t => t.Codec ?? "—");
        BitrateValue.Text    = UniformOrMixed(t => t.Bitrate > 0 ? $"{t.Bitrate} kbps" : "—");
        SampleRateValue.Text = UniformOrMixed(t => t.SampleRate > 0 ? $"{t.SampleRate / 1000.0:0.###} kHz" : "—");
        ChannelsValue.Text   = UniformOrMixed(t => t.Channels switch { 1 => "Mono", 2 => "Stereo", > 2 => $"{t.Channels} channels", _ => "—" });
        BitDepthValue.Text   = UniformOrMixed(t => t.BitsPerSample > 0 ? $"{t.BitsPerSample}-bit" : "—");

        // A song that lives on the paired server has no file here to name, so
        // it names the server instead, as the desktop window does.
        var serverName = vm?.Main.PairedServerSourceName(tracks);
        FileLabel.Text = serverName != null ? "Source" : "File";
        PathValue.Text = serverName ?? UniformOrMixed(t => t.Path ?? "—");
        DateAddedValue.Text = UniformOrMixed(t => t.DateAdded.LocalDateTime.ToString("MMM d, yyyy"));

        GenreMenuButton.IsVisible = editable && vm != null
            && TagSuggestionSource.DistinctGenres(vm.Main.Library.Tracks).Count > 0;

        // Only where a cover can be written: a file on this phone, or - for a
        // song of the paired server's - an admin's right to have the server
        // write it (AlbumArtEditor.CanWrite).
        _findArt?.Cancel();
        CloseArtwork();
        ArtButtons.IsVisible = ArtEditor() is { CanWrite: true };
        SetArtButtonsEnabled(true);
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

        var album = FirstNonBlank(AlbumBox.Text, track.Album);
        var artist = FirstNonBlank(AlbumArtistBox.Text, ArtistBox.Text, track.EffectiveAlbumArtist);
        var title = FirstNonBlank(TitleBox.Text, track.Title);

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
            viewer.ShowStatus("Downloading…");

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
                Resolution(picture.Size) + position);
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
            viewer.SetBusy(true);
            await WriteArtAsync(track, bytes, LocalAlbumArtReader.MimeTypeForBytes(bytes) ?? "image/jpeg");
            viewer.Close();
        };
        viewer.Closed += (_, _) => find.Cancel();

        await ShowCandidateAsync();
    }

    private static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    // Every way of changing the cover ends here: written into every song
    // being edited, and the card's cover read back from what was written.
    private async Task WriteArtAsync(Track track, byte[] bytes, string mimeType)
    {
        if (ArtEditor() is not { CanWrite: true } editor)
            return;

        SetArtButtonsEnabled(false);
        try
        {
            if (await editor.WriteAsync(bytes, mimeType) is { } message)
                ShowArtMessage(message);
        }
        finally
        {
            SetArtButtonsEnabled(true);
        }
        if (_track == track)
            await LoadAlbumArtAsync(track);
    }

    private void SetArtButtonsEnabled(bool enabled)
    {
        ChoosePhotoButton.IsEnabled = enabled;
        FindArtButton.IsEnabled = enabled;
        PasteArtButton.IsEnabled = enabled;
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
        _artBytes = null;

        var bitmap = await AlbumArtLoader.Current.LoadAsync(track);
        var (bytes, size) = await Task.Run(() =>
        {
            var art = AlbumArtLoader.TryGetArt(track);
            return (art?.Bytes, art is null ? null : MeasureArt(art.Bytes));
        });
        if (requestId != _artRequestId)
            return;

        ArtView.AlbumArt = bitmap;
        _artBytes = bytes;
        ArtResolution.Text = size is { } pixels ? Resolution(pixels) : "";
        ArtResolution.IsVisible = size is not null;
    }

    private static string Resolution(PixelSize size) =>
        $"{size.Width.ToString(CultureInfo.CurrentCulture)} \u00d7 {size.Height.ToString(CultureInfo.CurrentCulture)}";

    private static PixelSize? MeasureArt(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            using var full = new Bitmap(stream);
            return full.PixelSize;
        }
        catch
        {
            // Art that will not decode shows as the placeholder everywhere
            // else; here it just means no size under it.
            return null;
        }
    }

    // A picture for the full screen: its own size, or 2048 wide where it is
    // wider - sharp on any phone, without holding a 3000-pixel decode.
    private const int ScreenPixels = 2048;

    private static (Bitmap Bitmap, PixelSize Size)? DecodeForScreen(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
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
        viewer.ShowPicture(picture.Bitmap, Resolution(picture.Size));
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

    // Built on each open rather than once: a genre typed into another song a
    // minute ago belongs in the list now.
    private void GenreMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MobileMainViewModel vm)
            return;

        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var genre in TagSuggestionSource.DistinctGenres(vm.Main.Library.Tracks))
        {
            var item = new MenuItem { Header = genre };
            item.Click += (_, _) => GenreBox.Text = genre;
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
        await SaveChanges();
        (DataContext as MobileMainViewModel)?.CloseSheetCommand.Execute(null);
    }

    private async void Previous_Click(object? sender, RoutedEventArgs e) => await Step(-1);
    private async void Next_Click(object? sender, RoutedEventArgs e) => await Step(+1);

    // Saved before moving on, as the desktop window's Prev/Next do: stepping
    // to the next song is not leaving, so what was typed for this one is kept.
    private async Task Step(int delta)
    {
        await SaveChanges();
        (DataContext as MobileMainViewModel)?.StepTrackInfo(delta);
    }

    // The boxes whose text changed since the sheet opened, applied to every
    // song this phone may edit - a song of its own, or one of a server it
    // administers (MainViewModel.CanEditTagsOf) - file first, by the same
    // code the desktop window saves with (TrackTagEdits). A song this phone
    // only streams is edited on the song and the edit sent up.
    private Task SaveChanges()
    {
        if (_tracks.Count == 0 || DataContext is not MobileMainViewModel vm)
            return Task.CompletedTask;

        var edits = _fields
            .Where(f => (f.Box.Text ?? "") != _shown.GetValueOrDefault(f.Box, ""))
            .Select(f => (f.Field, f.Box.Text ?? ""))
            .ToList();
        if (edits.Count == 0)
            return Task.CompletedTask;

        var edited = TrackTagEdits.Apply(_tracks, edits, vm.Main.CanEditTagsOf, _logger);

        // NotifyTracksChanged, not UpdateTracks(Library.Tracks) - see
        // MainViewModel.SyncITunesPlayCountAsync's comment on why passing
        // Tracks back into UpdateTracks as a "fresh scan" silently doubles
        // every placeholder track. Persists just these rows.
        vm.Main.Library.NotifyTracksChanged(edited, TrackChange.Tags);
        return Task.CompletedTask;
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
