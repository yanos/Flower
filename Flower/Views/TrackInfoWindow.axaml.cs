using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

using CommunityToolkit.Mvvm.DependencyInjection;

using Material.Icons;

using Microsoft.Extensions.Logging;

using Flower.Converters;
using Flower.Importer;
using Flower.Logging;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;

namespace Flower.Views;

public partial class TrackInfoWindow : Window
{
    private static readonly DurationConverter _durationConverter = new();

    // Only meaningful in single-track/navigable mode (see the two constructors below).
    private readonly IReadOnlyList<Track> _tracks = Array.Empty<Track>();
    private readonly Library _library;
    // Handed in by whoever opens this window, alongside the Library it edits -
    // both come off the MainViewModel that caller already has, rather than
    // from a second lookup in the container (docs/ARCHITECTURE-REVIEW.md
    // Tier 2.3). The logger is the one exception: a Window has no constructor
    // the container reaches, which is exactly the case AppLogging's
    // typed-logger helper exists for.
    private readonly ILogger<TrackInfoWindow> _logger = AppLogging.CreateTypedLogger<TrackInfoWindow>();
    private int _index;

    // The set of tracks being edited: exactly one in navigable mode (re-seeded
    // on every Navigate()), or the whole multi-selection in batch mode.
    private IReadOnlyList<Track> _editTracks = Array.Empty<Track>();
    private List<EditableField> _fields = null!;
    private int _artRequestId; // guards against a stale Navigate()'s art load winning a race

    // The raw bytes behind whatever the Artwork tab is currently showing (see
    // LoadAlbumArtAsync) - kept because the displayed Bitmap is downscaled to
    // AlbumArtLoader.MaxArtPixels, so it can answer neither "what size is this
    // really" nor "open it full size".
    private LocalAlbumArt? _art;
    private PixelSize? _artPixelSize;

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
    public TrackInfoWindow(IReadOnlyList<Track> tracks, int index, Library library)
    {
        InitializeComponent();
        _tracks    = tracks;
        _library   = library;
        _index     = index;
        PopulateSuggestions();
        BuildFields();
        SetUpArtworkDropTarget();
        _editTracks = [_track];
        Populate();
        UpdateNavButtons();
        NativeMenuHelper.InheritFromMainWindow(this);
    }

    // Batch mode: edit this exact set of tracks together. No Prev/Next - there's
    // no "next" when editing a fixed set as one.
    public TrackInfoWindow(IReadOnlyList<Track> editTracks, Library library)
    {
        InitializeComponent();
        _library    = library;
        _editTracks = editTracks;
        PopulateSuggestions();
        BuildFields();
        SetUpArtworkDropTarget();
        Populate();
        PrevButton.IsVisible = false;
        NextButton.IsVisible = false;
        if (editTracks.Count > 1)
            Title = $"Track Info ({editTracks.Count} tracks)";
        NativeMenuHelper.InheritFromMainWindow(this);
    }

    // Computed once per window open from whatever the library holds right now,
    // not live-updated while the window stays open - see TagSuggestionSource.
    // Artists and AlbumArtists share one suggestion pool (see its own doc
    // comment); Album gets its own.
    private void PopulateSuggestions()
    {
        var artistSuggestions = TagSuggestionSource.DistinctArtists(_library.Tracks);
        ArtistBox.ItemsSource      = artistSuggestions;
        AlbumArtistBox.ItemsSource = artistSuggestions;
        AlbumBox.ItemsSource       = TagSuggestionSource.DistinctAlbums(_library.Tracks);
    }

    private async void PrevButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await Navigate(-1);
    private async void NextButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => await Navigate(+1);

    private void UpdateNavButtons()
    {
        PrevButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index < _tracks.Count - 1;
    }

    private async Task Navigate(int delta)
    {
        var next = _index + delta;
        if (next < 0 || next >= _tracks.Count)
            return;
        await SaveChanges();
        _index = next;
        _editTracks = [_track];
        Populate();
        UpdateNavButtons();
        TrackNavigated?.Invoke(this, _track);
    }

    // One row per editable field: how to read a display string off a Track, and
    // how to apply an edited string to both a Track and a TagLib tag. Built once
    // so the same field list drives population and save uniformly - the
    // alternative is ~23 fields each hand-written twice over.
    //
    // Dirty-tracking is done by comparing GetText() against OriginalText at save
    // time, NOT via TextChanged + a "currently populating" guard flag - Avalonia's
    // TextBox.TextChanged doesn't fire synchronously with a `.Text =` assignment,
    // it's deferred, so by the time it actually fires Populate() has already
    // returned and reset any such guard, incorrectly marking every field dirty
    // (confirmed via logging: every TextChanged during population fired with the
    // guard already back to false). Comparing final text state at save time
    // sidesteps that timing entirely.
    //
    // Backed by accessor delegates rather than a concrete control reference so
    // both plain TextBox fields and the AutoCompleteBox-backed Artist/Album/
    // Album Artist fields (see PopulateSuggestions) can share one field list -
    // AutoCompleteBox doesn't inherit from TextBox, so a single typed Box
    // reference can't hold either.
    private sealed class EditableField(
        Func<string> getText, Action<string> setText, Action<string?> setWatermark,
        Func<Track, string> display, Action<Track, TagLib.Tag, string?> apply)
    {
        public readonly Func<string> GetText = getText;
        public readonly Action<string> SetText = setText;
        public readonly Action<string?> SetWatermark = setWatermark;
        public readonly Func<Track, string> Display = display;
        public readonly Action<Track, TagLib.Tag, string?> Apply = apply;
        public string OriginalText = "";
        public bool IsDirty => GetText() != OriginalText;
    }

    private static EditableField FromTextBox(TextBox box, Func<Track, string> display, Action<Track, TagLib.Tag, string?> apply) =>
        new(() => box.Text ?? "", v => box.Text = v, w => box.PlaceholderText = w, display, apply);

    private static EditableField FromAutoComplete(AutoCompleteBox box, Func<Track, string> display, Action<Track, TagLib.Tag, string?> apply) =>
        new(() => box.Text ?? "", v => box.Text = v, w => box.PlaceholderText = w, display, apply);

    private static EditableField SimpleField(
        TextBox box, Func<Track, string?> get, Action<Track, string?> setTrack, Action<TagLib.Tag, string?> setTag) =>
        FromTextBox(box, t => get(t) ?? "", (t, tag, v) =>
        {
            var n = NullIfEmpty(v);
            setTrack(t, n);
            setTag(tag, n);
        });

    private void BuildFields()
    {
        _fields =
        [
            SimpleField(TitleBox, t => t.Title, (t, v) => t.Title = v, (tag, v) => tag.Title = v),
            FromAutoComplete(ArtistBox, t => t.Artists ?? "", (t, tag, v) => { t.Artists = NullIfEmpty(v); tag.Performers = SplitArray(v); }),
            FromAutoComplete(AlbumBox, t => t.Album ?? "", (t, tag, v) => { t.Album = NullIfEmpty(v); tag.Album = v; }),

            FromTextBox(TrackNumBox, t => t.TrackNumber > 0 ? t.TrackNumber.ToString() : "", (t, tag, v) => { var n = ParseUInt(v); t.TrackNumber = n; tag.Track = n; }),
            FromTextBox(TrackTotalBox, t => t.TrackCount > 0 ? t.TrackCount.ToString() : "", (t, tag, v) => { var n = ParseUInt(v); t.TrackCount = n; tag.TrackCount = n; }),
            FromTextBox(DiscNumBox, t => t.DiscNumber > 0 ? t.DiscNumber.ToString() : "", (t, tag, v) => { var n = ParseUInt(v); t.DiscNumber = n; tag.Disc = n; }),
            FromTextBox(DiscTotalBox, t => t.DiscCount > 0 ? t.DiscCount.ToString() : "", (t, tag, v) => { var n = ParseUInt(v); t.DiscCount = n; tag.DiscCount = n; }),
            // Track.Year stays a raw string while tag.Year is parsed to uint - an
            // existing asymmetry in how this field was already handled, preserved as-is.
            FromTextBox(YearBox, t => t.Year ?? "", (t, tag, v) => { t.Year = NullIfEmpty(v); tag.Year = ParseUInt(v); }),
            FromTextBox(GenreBox, t => t.Genre ?? "", (t, tag, v) => { var g = NullIfEmpty(v); t.Genre = g; tag.Genres = g is string gg ? [gg] : []; }),
            FromTextBox(BpmBox, t => t.BeatsPerMinute > 0 ? t.BeatsPerMinute.ToString() : "", (t, tag, v) => { var n = ParseUInt(v); t.BeatsPerMinute = n; tag.BeatsPerMinute = n; }),
            SimpleField(KeyBox, t => t.InitialKey, (t, v) => t.InitialKey = v, (tag, v) => tag.InitialKey = v),
            SimpleField(GroupingBox, t => t.Grouping, (t, v) => t.Grouping = v, (tag, v) => tag.Grouping = v),

            FromAutoComplete(AlbumArtistBox, t => t.AlbumArtists ?? "", (t, tag, v) => { t.AlbumArtists = NullIfEmpty(v); tag.AlbumArtists = SplitArray(v); }),
            FromTextBox(ComposerBox, t => t.Composers ?? "", (t, tag, v) => { t.Composers = NullIfEmpty(v); tag.Composers = SplitArray(v); }),
            SimpleField(ConductorBox, t => t.Conductor, (t, v) => t.Conductor = v, (tag, v) => tag.Conductor = v),
            SimpleField(RemixedByBox, t => t.RemixedBy, (t, v) => t.RemixedBy = v, (tag, v) => tag.RemixedBy = v),

            SimpleField(SubtitleBox, t => t.Subtitle, (t, v) => t.Subtitle = v, (tag, v) => tag.Subtitle = v),
            SimpleField(DescriptionBox, t => t.Description, (t, v) => t.Description = v, (tag, v) => tag.Description = v),
            SimpleField(CommentBox, t => t.Comment, (t, v) => t.Comment = v, (tag, v) => tag.Comment = v),
            SimpleField(PublisherBox, t => t.Publisher, (t, v) => t.Publisher = v, (tag, v) => tag.Publisher = v),
            SimpleField(CopyrightBox, t => t.Copyright, (t, v) => t.Copyright = v, (tag, v) => tag.Copyright = v),
            SimpleField(ISRCBox, t => t.ISRC, (t, v) => t.ISRC = v, (tag, v) => tag.ISRC = v),

            SimpleField(LyricsBox, t => t.Lyrics, (t, v) => t.Lyrics = v, (tag, v) => tag.Lyrics = v),

            // Options tab. The sort tags are ordinary staged tag edits like
            // everything above - TSOT/TSOP/TSOA/TSOC and their MP4/Xiph
            // equivalents, which TagLib does expose on the generic Tag. (The
            // compilation flag on the same tab does not, and is handled
            // separately in SaveChanges.)
            SimpleField(TitleSortBox, t => t.TitleSort, (t, v) => t.TitleSort = v, (tag, v) => tag.TitleSort = v),
            FromTextBox(ArtistSortBox, t => t.ArtistsSort ?? "", (t, tag, v) => { t.ArtistsSort = NullIfEmpty(v); tag.PerformersSort = SplitArray(v); }),
            SimpleField(AlbumSortBox, t => t.AlbumSort, (t, v) => t.AlbumSort = v, (tag, v) => tag.AlbumSort = v),
            FromTextBox(ComposerSortBox, t => t.ComposersSort ?? "", (t, tag, v) => { t.ComposersSort = NullIfEmpty(v); tag.ComposersSort = SplitArray(v); }),
        ];
    }

    private void Populate()
    {
        foreach (var field in _fields)
        {
            var values = _editTracks.Select(field.Display).Distinct().ToList();
            if (values.Count == 1)
            {
                field.SetText(values[0]);
                field.SetWatermark(null);
                field.OriginalText = values[0];
            }
            else
            {
                field.SetText("");
                field.SetWatermark("Multiple values");
                field.OriginalText = ""; // untouched sentinel for a mixed field
            }
        }

        // Persistent header display (read-only - editing happens via the
        // Title/Artist/Album fields under the Info tab, part of _fields above).
        TitleDisplay.Text  = UniformOrMixed(t => t.Title   ?? "");
        ArtistDisplay.Text = UniformOrMixed(t => t.Artists ?? "");
        AlbumDisplay.Text  = UniformOrMixed(t => t.Album   ?? "");

        // Technical (read-only)
        DurationValue.Text   = UniformOrMixed(t => _durationConverter.Convert(t.Duration, typeof(string), null, CultureInfo.CurrentCulture) as string ?? "—");
        CodecValue.Text      = UniformOrMixed(t => t.Codec ?? "—");
        BitrateValue.Text    = UniformOrMixed(t => t.Bitrate > 0 ? $"{t.Bitrate} kbps" : "—");
        SampleRateValue.Text = UniformOrMixed(t => t.SampleRate > 0 ? $"{t.SampleRate / 1000.0:0.###} kHz" : "—");
        ChannelsValue.Text   = UniformOrMixed(t => t.Channels switch { 1 => "Mono", 2 => "Stereo", > 2 => $"{t.Channels} channels", _ => "—" });
        DateAddedValue.Text  = UniformOrMixed(t => t.DateAdded.LocalDateTime.ToString("MMM d, yyyy"));

        BitDepthValue.Text   = UniformOrMixed(t => t.BitsPerSample > 0 ? $"{t.BitsPerSample}-bit" : "—");
        // Read at import like every other technical field, not parsed here on
        // demand - see Track.EncoderProfile. Which also means a server-only
        // track has one, and it can be a column.
        EncodingValue.Text   = UniformOrMixed(t => t.EncoderProfile ?? "—");

        // "File" only names a file when there is one; for a track that lives
        // only on the paired server, the source is the server itself.
        var serverName = PairedServerSourceName();
        ServerSourcePanel.IsVisible = serverName != null;
        PathValue.IsVisible = serverName == null;
        FileLabel.Text = serverName != null ? "Source" : "File";
        if (serverName != null)
            ServerSourceName.Text = serverName;
        else
            PathValue.Text = UniformOrMixed(t => t.Path ?? "—");

        UpdateListening();
        UpdateOptions();

        _ = LoadAlbumArtAsync();
    }

    // Plays and the star - Flower's own record of what has been listened to,
    // not tags, so this reads straight off the Track rather than through
    // _fields. Refreshed on its own (rather than only from Populate) because
    // the star writes as soon as it is clicked and has to redraw itself.
    private void UpdateListening()
    {
        // The sum of this device's own count, whatever was imported from iTunes
        // and every other device's - see Track.TotalPlayCount, which is the same
        // number the track list's Plays column shows.
        PlayCountValue.Text = UniformOrMixed(t => t.TotalPlayCount.ToString(CultureInfo.CurrentCulture));

        // Recorded on play-start rather than on finishing a track (see
        // Track.LastPlayedAt), and null for anything that has never been
        // played - including a track whose whole play count was imported from
        // iTunes, which is why a "—" here next to a non-zero Plays above is
        // correct rather than a bug. The time is shown as well as the date: it
        // is the only field in this window that answers "was that just now?".
        LastPlayedValue.Text = UniformOrMixed(t =>
            t.LastPlayedAt is { } at
                ? $"{at.LocalDateTime.ToString("MMM d, yyyy", CultureInfo.CurrentCulture)} {at.LocalDateTime.ToShortTimeString()}"
                : "—");

        var starred = _editTracks.Count(t => t.Starred);
        var all = _editTracks.Count > 0 && starred == _editTracks.Count;
        var some = starred > 0 && !all;

        StarIcon.Kind = all ? MaterialIconKind.Star : some ? MaterialIconKind.StarHalfFull : MaterialIconKind.StarOutline;
        StarIcon.Opacity = all || some ? 1 : 0.45;
        StarText.Text = all
            ? _editTracks.Count > 1 ? "Starred" : StarredOnText(_editTracks[0])
            : some ? $"{starred} of {_editTracks.Count} starred"
            : "Not starred";
        StarText.Opacity = all || some ? 1 : 0.45;
        StarButton.IsEnabled = _editTracks.Count > 0;
        ToolTip.SetTip(StarButton, all ? "Remove the star" : "Star");
    }

    // ── Options tab ────────────────────────────────────────────────────────
    //
    // Two halves with different save rules, which is the one thing worth
    // keeping straight here. Compilation and the four sort boxes are tags:
    // staged, written on OK, undone by Cancel, exactly like the Info tab. The
    // three playback options underneath are Flower's own per-track state (see
    // Track's "Per-track playback options"), so they write the moment they are
    // changed, like the star - there is no file write to batch them with, and
    // no OK to wait for.

    // The compilation flag as the track(s) had it when this window opened, so
    // save can tell an untouched checkbox from one deliberately set to the same
    // value. Null for a mixed selection, which is also what the three-state
    // checkbox shows - and leaving it in that state is what "don't change it"
    // means.
    private bool? _compilationOriginal;

    // Suppresses the immediate writes below while Populate is filling the
    // controls in. Unlike the tag fields (see EditableField's remarks on why
    // those compare text at save time instead), a CheckBox/Slider raises its
    // change event synchronously from the assignment, so a plain guard flag is
    // both sufficient and reliable here.
    private bool _populatingOptions;

    private void UpdateOptions()
    {
        _populatingOptions = true;
        try
        {
            // Three-state so a mixed selection has somewhere to sit. Turned off
            // again once the value is uniform, so a single track's checkbox
            // cannot be cycled into an indeterminate state that means nothing.
            var compilation = UniformFlag(t => t.IsCompilation);
            CompilationBox.IsThreeState = compilation is null;
            CompilationBox.IsChecked = compilation;
            _compilationOriginal = compilation;

            // The real value as the watermark, so an empty box reads as "sorts
            // under its own name" rather than as a field nobody filled in.
            // Skipped where Populate already put "Multiple values" there.
            SetSortWatermark(TitleSortBox, t => t.Title);
            SetSortWatermark(ArtistSortBox, t => t.Artists);
            SetSortWatermark(AlbumSortBox, t => t.Album);
            SetSortWatermark(ComposerSortBox, t => t.Composers);

            RememberPositionBox.IsThreeState = UniformFlag(t => t.RememberPlaybackPosition) is null;
            RememberPositionBox.IsChecked = UniformFlag(t => t.RememberPlaybackPosition);
            IgnoreInShuffleBox.IsThreeState = UniformFlag(t => t.IgnoreWhenShuffling) is null;
            IgnoreInShuffleBox.IsChecked = UniformFlag(t => t.IgnoreWhenShuffling);

            var adjustments = _editTracks.Select(t => t.VolumeAdjustment).Distinct().ToList();
            VolumeAdjustmentSlider.Value = adjustments.Count == 1 ? adjustments[0] : 0;
            UpdateVolumeAdjustmentText();
            UpdateResumePositionText();

            // The playback half works for anything (it is library state); the
            // tag half needs a file to write into.
            var writable = _editTracks.Count > 0 && _editTracks.All(t => t.Path != null);
            CompilationBox.IsEnabled = writable;
            TitleSortBox.IsEnabled = writable;
            ArtistSortBox.IsEnabled = writable;
            AlbumSortBox.IsEnabled = writable;
            ComposerSortBox.IsEnabled = writable;
            OptionsUnavailableText.IsVisible = !writable;
            OptionsUnavailableText.Text = writable
                ? ""
                : "Compilation and the sort tags live in the file's own tags, and this track has no local file to write. "
                  + "The playback options still apply - those are Flower's own, not tags.";
        }
        finally
        {
            _populatingOptions = false;
        }
    }

    // True/false when every track agrees, null when they don't.
    private bool? UniformFlag(Func<Track, bool> get)
    {
        if (_editTracks.Count == 0)
            return false;

        var first = get(_editTracks[0]);
        return _editTracks.All(t => get(t) == first) ? first : null;
    }

    private void SetSortWatermark(TextBox box, Func<Track, string?> display)
    {
        if (box.PlaceholderText != null)
            return;

        var values = _editTracks.Select(t => display(t) ?? "").Distinct().ToList();
        box.PlaceholderText = values.Count == 1 && values[0].Length > 0 ? values[0] : null;
    }

    private void UpdateResumePositionText()
    {
        var positions = _editTracks.Select(t => t.ResumePosition).Distinct().ToList();
        ResumePositionText.Text = positions is [{ } position] && position > TimeSpan.Zero
            ? $@"— currently at {position:h\:mm\:ss}"
            : "";
    }

    private void UpdateVolumeAdjustmentText()
    {
        var value = (int)Math.Round(VolumeAdjustmentSlider.Value);
        VolumeAdjustmentText.Text = value == 0 ? "None" : value.ToString("+0;-0", CultureInfo.CurrentCulture) + "%";
        VolumeAdjustmentText.Opacity = value == 0 ? 0.45 : 1;
    }

    // The three immediate writes. Each one mutates the tracks and persists
    // through the library in the same breath - see the region comment above.
    private void RememberPositionBox_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_populatingOptions || RememberPositionBox.IsChecked is not { } value)
            return;

        foreach (var track in _editTracks)
        {
            track.RememberPlaybackPosition = value;
            // Turning it off throws away where the track had got to, rather
            // than parking it for a later re-tick to resurrect. Half a
            // listening session remembered from an unknown time in the past is
            // worse than starting over.
            if (!value)
                track.ResumePosition = null;
        }

        RememberPositionBox.IsThreeState = false;
        UpdateResumePositionText();
        PersistOptions();
    }

    private void IgnoreInShuffleBox_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_populatingOptions || IgnoreInShuffleBox.IsChecked is not { } value)
            return;

        foreach (var track in _editTracks)
            track.IgnoreWhenShuffling = value;

        IgnoreInShuffleBox.IsThreeState = false;
        PersistOptions();
    }

    private void VolumeAdjustmentSlider_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateVolumeAdjustmentText();
        if (_populatingOptions)
            return;

        var value = (int)Math.Round(VolumeAdjustmentSlider.Value);
        foreach (var track in _editTracks)
            track.VolumeAdjustment = value;

        PersistOptions();
    }

    // One upsert per edited track, no TracksUpdated: none of the three shows up
    // in a track list, so there is nothing for a view to rebuild - and
    // rebuilding it would mean a peer library sync per drag of the volume
    // slider. Same reasoning as Library.RecordResumePosition's own comment.
    private void PersistOptions()
    {
        foreach (var track in _editTracks)
            _library.PersistTrackOptions(track);
    }

    private static string StarredOnText(Track track) =>
        track.StarredAt is { } at ? $"Starred {at.LocalDateTime:MMM d, yyyy}" : "Starred";

    // Immediate, like the artwork below and unlike every tag field in this
    // window: there is no file write to batch it with, and Library.SetStarred
    // is the one path that both mutates the track and persists the change (it
    // is what the Subsonic /star route calls too), so routing it through this
    // window's own OK would mean a second way to do the same thing.
    private void StarButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_editTracks.Count == 0)
            return;

        var starred = _editTracks.All(t => t.Starred);
        foreach (var track in _editTracks)
            _library.SetStarred(StarTarget.Song, track.Id.ToString(), !starred);

        UpdateListening();
    }

    // The paired server's sidebar name, but only when every track being shown
    // is one of its placeholders (Path == null and the origin is that same
    // server). Anything else - a local file, a placeholder from a peer this
    // device is no longer paired with, a mixed selection - falls back to the
    // plain path display, which already has a "Multiple values" answer for the
    // mixed case.
    //
    // Read off AppSettings through the container rather than handed in, for the
    // same reason the logger above is: a Window has no constructor the
    // container reaches, and threading a fourth argument through all six call
    // sites to name one label is not worth it.
    private string? PairedServerSourceName()
    {
        if (_editTracks.Count == 0)
            return null;

        var settings = Ioc.Default.GetService<AppSettings>();
        if (settings?.PairedServerFingerprint is not { Length: > 0 } fingerprint)
            return null;

        foreach (var track in _editTracks)
        {
            if (track.Path != null || track.OriginDeviceFingerprint != fingerprint)
                return null;
        }

        return string.IsNullOrWhiteSpace(settings.PairedServerAlias) ? "Server" : settings.PairedServerAlias;
    }

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

        // The undecoded bytes, plus one full-size decode purely to learn the
        // real pixel dimensions - bmp above is capped at MaxArtPixels and would
        // report the cap, not the art. Off the UI thread and dropped
        // immediately; it happens once per shown track, not once per paint.
        var (art, pixelSize) = await Task.Run(() =>
        {
            var found = AlbumArtLoader.TryGetArt(track);
            return (found, found is null ? null : MeasureArt(found.Bytes));
        });

        if (requestId != _artRequestId)
            return;

        AlbumArtView.AlbumArt = bmp;
        ArtworkLargeView.AlbumArt = bmp;
        _art = art;
        _artPixelSize = pixelSize;
        UpdateArtworkTab();
    }

    private static PixelSize? MeasureArt(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var full = new Bitmap(ms);
            return full.PixelSize;
        }
        catch
        {
            // Undecodable art is already handled everywhere else by falling
            // back to the placeholder; here it just means no dimensions line.
            return null;
        }
    }

    private string UniformOrMixed(Func<Track, string> display)
    {
        var values = _editTracks.Select(display).Distinct().ToList();
        return values.Count == 1 ? values[0] : "Multiple values";
    }

    private async void OkButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await SaveChanges();
        Close();
    }

    private void CancelButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }

    private async Task SaveChanges()
    {
        var dirty = _fields.Where(f => f.IsDirty).ToList();

        // The Options tab's compilation checkbox, which cannot ride along in
        // _fields: the flag has no home on the generic TagLib.Tag those all
        // write through, and needs a per-format write instead (see
        // CompilationFlag). Null means either "still mixed" or "untouched" -
        // both of which mean leave every track's flag as it is.
        var compilation = CompilationBox.IsChecked == _compilationOriginal ? null : CompilationBox.IsChecked;

        if (dirty.Count == 0 && compilation == null)
            return;

        foreach (var track in _editTracks)
        {
            if (track.Path is not string path)
                continue;

            try
            {
                using var tagFile = TagLib.File.Create(path);
                var tag = tagFile.Tag;
                foreach (var field in dirty)
                    field.Apply(track, tag, field.GetText());
                if (compilation is { } isCompilation && CompilationFlag.Write(tagFile, isCompilation))
                    track.IsCompilation = isCompilation;
                tagFile.Save();
            }
            catch (Exception ex)
            {
                // field.Apply above already mutated the in-memory Track object
                // itself (it's an Action<Track, TagLib.Tag, string?>, not just
                // the TagLib tag) before this Save() was attempted - so a failed
                // save here leaves the UI showing the edit as if it took, while
                // the actual file on disk still has the old value. Not fixed
                // here (would need reordering: apply to the tag only, save,
                // *then* mutate the Track - a separate, real bug worth its own
                // fix), but at least surfaced now instead of failing silently.
                _logger.LogWarning(ex, "Could not save tag edits to {Path}; the in-memory track was updated but the file on disk was not", path);
                continue;
            }
        }

        // Refreshes views bound to the library and persists the edited tracks
        // - one upsert each, where this used to rewrite the whole library to
        // push a handful of changed rows.
        //
        // NotifyTracksChanged, not UpdateTracks(_library.Tracks) - see
        // MainViewModel.SyncITunesPlayCountAsync's comment on why passing
        // Tracks back into UpdateTracks as a "fresh scan" silently doubles
        // every placeholder track.
        _library.NotifyTracksChanged(_editTracks);
    }

    // ── Artwork tab ────────────────────────────────────────────────────────
    //
    // Unlike every other field in this window, artwork is not staged and
    // applied on OK: a change here writes the picture into the tag immediately
    // (ApplyArtChangeAsync). Two reasons. The tag write is a whole-file rewrite
    // of megabytes, not a string assignment, so batching it behind OK would make
    // Cancel look free while the expensive part still had to happen; and the
    // preview the user is looking at *is* the file's art once it is written, so
    // there is nothing left for OK to confirm. Cancel therefore does not undo an
    // artwork change - which is why the buttons name actions ("Change…",
    // "Remove") rather than reading as pending edits.
    //
    // "The file" is not always one of this device's: a track that only exists on
    // the paired server is written by asking that server to do it - see
    // ServerArtAlbumIds.

    // The tracks a write can land on directly: a file on this disk to embed a
    // picture into. A placeholder that only exists on the paired server has
    // none, and is handled by ServerArtAlbumIds below instead.
    private List<Track> ArtWriteTargets() =>
        [.. _editTracks.Where(AlbumArtLoader.IsLocalFile)];

    // ── Artwork on the paired server ───────────────────────────────────────
    //
    // The other half of "replace this cover": the track being looked at has no
    // local file, because it lives on the server this device is paired with.
    // Rather than refuse (which is what the tab did until now - "there's no
    // local file to write artwork into"), an admin device asks the server to do
    // the write, over the same signed admin surface the settings screen uses.
    // See AdminEndpoints' /cover-art routes for why that is an admin route and
    // not a Subsonic one.
    //
    // Addressed by album id, not by track: art is served per album on the way
    // out (SubsonicMapper's CoverArt field, and PeerCoverArtUrlResolver asks
    // for exactly this id), so writing into one track's file would leave the
    // album still serving whichever other file the read path reached first.
    // Distinct, because a batch selection can span albums.
    private List<string> ServerArtAlbumIds()
    {
        if (_editTracks.Count == 0 || ArtWriteTargets().Count > 0 || PairedServerSourceName() == null)
            return [];

        // PeerTrackResolver owns "may this device still ask that peer for this
        // track" - the same rule the art fetch goes through. No resolver, no
        // credentials, or a peer that is not reachable right now all mean the
        // same thing here: nothing to send the picture to.
        if (Ioc.Default.GetService<PeerTrackResolver>()?.Resolve(_editTracks[0]) == null)
            return [];
        if (Ioc.Default.GetService<IPeerCredentials>() == null)
            return [];

        return [.. _editTracks.Select(LibraryOpenSubsonicMapper.AlbumIdFor).Distinct(StringComparer.Ordinal)];
    }

    // Runs one admin call per album the selection covers, and reports how many
    // files the server said it rewrote. A ServerAdminException carries the
    // server's own words ("This device is paired, but is not an administrator
    // of that server.") which is exactly what the tab should show.
    private async Task<(int Written, string? Error)> ApplyServerArtAsync(
        IReadOnlyList<string> albumIds, Func<ServerAdminClient, string, Task<CoverArtWriteDto>> apply)
    {
        var device = Ioc.Default.GetService<PeerTrackResolver>()?.Resolve(_editTracks[0]);
        var credentials = Ioc.Default.GetService<IPeerCredentials>();
        if (device == null || credentials == null)
            return (0, "That server isn't reachable right now.");

        // PeerHttpClient rather than a bare one, for the same reason the art
        // fetch uses it: this is the same peer on the same origin, under the
        // same accepted-certificate rule. Generous timeout - the server is
        // rewriting whole audio files, one per track on the album.
        using var http = PeerHttpClient.Create(TimeSpan.FromMinutes(2));
        var client = new ServerAdminClient(http, device.BaseUri, ServerAdminClient.SignWith(credentials), logger: _logger);

        var written = 0;
        foreach (var albumId in albumIds)
        {
            try
            {
                written += (await apply(client, albumId)).Written;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not change the album art for {AlbumId} on {Server}", albumId, device.BaseUri);
                return (written, ex is ServerAdminException ? ex.Message : "Could not reach that server.");
            }
        }

        return (written, null);
    }

    private void SetUpArtworkDropTarget()
    {
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

    // Embeds a picture in the selected tracks' own files, or - when they only
    // exist on the paired server - asks that server to do the same to its
    // (AlbumArtWriter is the shared implementation of the write itself, so the
    // two paths cannot disagree about how a picture goes into a tag).
    private Task WriteArtAsync(byte[] bytes, string mimeType) =>
        ApplyArtChangeAsync(
            path => AlbumArtWriter.TryWrite(path, bytes, mimeType, _logger),
            (client, albumId) => client.SetCoverArtAsync(albumId, bytes, mimeType),
            "The artwork couldn't be written to");

    // The counterpart: takes the picture back out. Not a confirmation-guarded
    // action even though it destroys data, for the same reason Change is not -
    // it is one explicit click on a button that says what it does, on a file
    // the user is already editing the tags of, and the way back is to drop the
    // old image in again (which is why Open Full Size and dragging the art out
    // both exist before this button does).
    private Task RemoveArtAsync() =>
        ApplyArtChangeAsync(
            path => AlbumArtWriter.TryRemove(path, _logger),
            (client, albumId) => client.RemoveCoverArtAsync(albumId),
            "The artwork couldn't be removed from");

    private async Task ApplyArtChangeAsync(
        Func<string, bool> writeLocal,
        Func<ServerAdminClient, string, Task<CoverArtWriteDto>> writeServer,
        string failureVerb)
    {
        var targets = ArtWriteTargets();
        var albumIds = ServerArtAlbumIds();
        if (targets.Count == 0 && albumIds.Count == 0)
            return;

        ChangeArtButton.IsEnabled = false;
        RemoveArtButton.IsEnabled = false;

        string? message = null;
        if (targets.Count > 0)
        {
            var failed = await Task.Run(() => targets.Count(track => !writeLocal(track.Path!)));
            if (failed > 0)
                message = failed == targets.Count
                    ? $"{failureVerb} the file."
                    : $"{failureVerb} {failed} of {targets.Count} files.";
        }
        else
        {
            var (written, error) = await ApplyServerArtAsync(albumIds, writeServer);
            message = error ?? (written == 0 ? "The server didn't change any files." : null);
        }

        // The cached bitmap was decoded from bytes that are no longer what the
        // file (or the server) holds; without this every other view in the app
        // keeps painting the old cover until the process restarts.
        foreach (var track in _editTracks)
            AlbumArtLoader.Invalidate(track);

        _library.NotifyTracksChanged(_editTracks);
        await LoadAlbumArtAsync();

        if (message != null)
            ArtworkInfoText.Text = message;
    }

    private async void RemoveArtButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await RemoveArtAsync();

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
            Title = UniformOrMixed(t => t.Album ?? t.Title ?? "Artwork"),
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
        RemoveArtButton.IsEnabled = canWrite && hasArt;
        OpenArtButton.IsEnabled = hasArt;

        ArtworkInfoText.Text = DescribeArtwork(hasArt, writable, serverAlbums);
    }

    private string DescribeArtwork(bool hasArt, int writable, int serverAlbums)
    {
        if (_editTracks.Count == 0)
            return "";

        if (writable == 0 && serverAlbums == 0)
            return _art?.Bytes is { Length: > 0 }
                // Art there is - it came from the server's own copy - but no
                // way to change it: either this device is not an admin of that
                // server, or the server is not reachable from here right now.
                ? "Only on the server, which can't be reached to change this."
                : "Only on the server — there's nothing here to write artwork into.";

        var parts = new List<string>();
        if (hasArt)
        {
            if (_artPixelSize is { } size)
                parts.Add($"{size.Width} × {size.Height}");
            if (FormatName(_art!.MimeType) is string format)
                parts.Add(format);
            parts.Add(FormatBytes(_art!.Bytes.Length));
        }
        else
        {
            parts.Add("No artwork");
        }

        // Scope first, because it is the part that can surprise: a batch edit
        // rewrites every selected file, and a change on the server lands on the
        // whole album (see ServerArtAlbumIds).
        if (serverAlbums > 0)
            parts.Add(serverAlbums == 1 ? "Changes apply to the whole album on the server" : $"Changes apply to {serverAlbums} albums on the server");
        else if (writable > 1)
            parts.Add($"Changes apply to all {writable} tracks");
        else
            parts.Add("Drop an image or click Change");

        return string.Join("  ·  ", parts);
    }

    // Null for a placeholder track's art, whose bytes come from the content-
    // addressed cache with no MIME type recorded (see AlbumArtLoader.TryGetArt).
    private static string? FormatName(string mimeType) =>
        mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && mimeType.Length > 6
            ? mimeType[6..].ToUpperInvariant()
            : null;

    private static string FormatBytes(int count) =>
        count >= 1024 * 1024 ? $"{count / (1024.0 * 1024.0):0.#} MB" : $"{count / 1024.0:0} KB";

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string[] SplitArray(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? []
            : [.. s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static uint ParseUInt(string? s) =>
        uint.TryParse(s?.Trim(), out var n) ? n : 0;
}
