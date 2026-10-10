using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Interactivity;

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

    // Each box and the tag it edits (TrackTagFields, shared with the desktop
    // window), with what it showed when the sheet opened - a box whose text
    // still matches is not saved, which is what leaves "Multiple values"
    // alone across several songs.
    private (TextBox Box, TrackTagField Field)[] _fields = [];
    private readonly Dictionary<TextBox, string> _shown = new();

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

        _suggestions.Add(new TagSuggestions(ArtistBox, ArtistSuggestions));
        _suggestions.Add(new TagSuggestions(AlbumBox, AlbumSuggestions));
        _suggestions.Add(new TagSuggestions(AlbumArtistBox, AlbumArtistSuggestions));
        _suggestions.Add(new TagSuggestions(ComposerBox, ComposerSuggestions));
        _suggestions.Add(new TagSuggestions(GenreBox, GenreSuggestions));

        TitleBox.TextChanged += (_, _) => UpdateTitleHeading();

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
            if (vm.IsShowingTrackInfo && vm.TrackInfoTracks.Count > 0 && !ReferenceEquals(vm.TrackInfoTracks, _tracks))
                Populate(vm.TrackInfoTracks);
        }
    }

    // The title, or - for several songs whose titles differ - how many there
    // are, rather than nothing.
    private void UpdateTitleHeading() =>
        TitleHeading.Text = !string.IsNullOrEmpty(TitleBox.Text) || _tracks.Count < 2
            ? TitleBox.Text ?? ""
            : $"{_tracks.Count} songs";

    // One value for the whole set when they agree, "Multiple values" when not.
    private string UniformOrMixed(Func<Track, string> display)
    {
        var values = _tracks.Select(display).Distinct().Take(2).ToList();
        return values.Count == 1 ? values[0] : "Multiple values";
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
            box.PlaceholderText = common == null ? "Multiple values" : null;
            _shown[box] = common ?? "";
        }
        UpdateTitleHeading();

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
        PasteArtButton.IsVisible = ArtEditor() is { CanWrite: true };
        PasteArtButton.IsEnabled = true;
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

    // The clipboard's image, as the song's cover - written straight into the
    // file (or by the server, for one of its songs), and the card's cover
    // redrawn from it.
    private async void PasteArt_Click(object? sender, RoutedEventArgs e)
    {
        if (_track is not { } track || ArtEditor() is not { CanWrite: true } editor)
            return;

        ArtMessage.IsVisible = false;
        var png = await ClipboardArt.TryReadPngAsync(TopLevel.GetTopLevel(this)?.Clipboard);
        if (png is null)
        {
            ShowArtMessage("There's no image on the clipboard. Copy one, then tap Paste again.");
            return;
        }

        PasteArtButton.IsEnabled = false;
        try
        {
            if (await editor.WriteAsync(png, ClipboardArt.MimeType) is { } message)
                ShowArtMessage(message);
        }
        finally
        {
            PasteArtButton.IsEnabled = true;
        }
        if (_track == track)
            await LoadAlbumArtAsync(track);
    }

    private void ShowArtMessage(string message)
    {
        ArtMessage.Text = message;
        ArtMessage.IsVisible = true;
    }

    private async Task LoadAlbumArtAsync(Track track)
    {
        var requestId = ++_artRequestId;
        ArtView.AlbumArt = null;
        var bitmap = await AlbumArtLoader.Current.LoadAsync(track);
        if (requestId == _artRequestId)
            ArtView.AlbumArt = bitmap;
    }

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
