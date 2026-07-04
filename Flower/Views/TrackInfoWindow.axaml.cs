using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Controls;

using Flower.Converters;
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
    private int _index;

    // The set of tracks being edited: exactly one in navigable mode (re-seeded
    // on every Navigate()), or the whole multi-selection in batch mode.
    private IReadOnlyList<Track> _editTracks = Array.Empty<Track>();
    private List<EditableField> _fields = null!;
    private int _artRequestId; // guards against a stale Navigate()'s art load winning a race

    private Track _track => _tracks[_index];

    public event EventHandler<Track>? TrackNavigated;

    // Single-track mode: tracks/index is the full displayed list, so Prev/Next
    // can browse through it one at a time.
    public TrackInfoWindow(IReadOnlyList<Track> tracks, int index, Library library)
    {
        InitializeComponent();
        _tracks    = tracks;
        _library   = library;
        _index     = index;
        BuildFields();
        _editTracks = [_track];
        Populate();
        UpdateNavButtons();
    }

    // Batch mode: edit this exact set of tracks together. No Prev/Next - there's
    // no "next" when editing a fixed set as one.
    public TrackInfoWindow(IReadOnlyList<Track> editTracks, Library library)
    {
        InitializeComponent();
        _library    = library;
        _editTracks = editTracks;
        BuildFields();
        Populate();
        PrevButton.IsVisible = false;
        NextButton.IsVisible = false;
        if (editTracks.Count > 1)
            Title = $"Track Info ({editTracks.Count} tracks)";
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
    // Dirty-tracking is done by comparing Box.Text against OriginalText at save
    // time, NOT via TextChanged + a "currently populating" guard flag - Avalonia's
    // TextBox.TextChanged doesn't fire synchronously with a `.Text =` assignment,
    // it's deferred, so by the time it actually fires Populate() has already
    // returned and reset any such guard, incorrectly marking every field dirty
    // (confirmed via logging: every TextChanged during population fired with the
    // guard already back to false). Comparing final text state at save time
    // sidesteps that timing entirely.
    private sealed class EditableField(TextBox box, Func<Track, string> display, Action<Track, TagLib.Tag, string?> apply)
    {
        public readonly TextBox Box = box;
        public readonly Func<Track, string> Display = display;
        public readonly Action<Track, TagLib.Tag, string?> Apply = apply;
        public string OriginalText = "";
        public bool IsDirty => Box.Text != OriginalText;
    }

    private static EditableField SimpleField(
        TextBox box, Func<Track, string?> get, Action<Track, string?> setTrack, Action<TagLib.Tag, string?> setTag) =>
        new(box, t => get(t) ?? "", (t, tag, v) =>
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
            new(ArtistBox, t => t.Artists ?? "", (t, tag, v) => { t.Artists = NullIfEmpty(v); tag.Performers = SplitArray(v); }),
            SimpleField(AlbumBox, t => t.Album, (t, v) => t.Album = v, (tag, v) => tag.Album = v),

            new(TrackNumBox, t => t.TrackNumber > 0 ? t.TrackNumber.ToString() : "", (t, tag, v) => { var n = ParseUInt(v); t.TrackNumber = n; tag.Track = n; }),
            new(TrackTotalBox, t => t.TrackCount > 0 ? t.TrackCount.ToString() : "", (t, tag, v) => { var n = ParseUInt(v); t.TrackCount = n; tag.TrackCount = n; }),
            new(DiscNumBox, t => t.DiscNumber > 0 ? t.DiscNumber.ToString() : "", (t, tag, v) => { var n = ParseUInt(v); t.DiscNumber = n; tag.Disc = n; }),
            new(DiscTotalBox, t => t.DiscCount > 0 ? t.DiscCount.ToString() : "", (t, tag, v) => { var n = ParseUInt(v); t.DiscCount = n; tag.DiscCount = n; }),
            // Track.Year stays a raw string while tag.Year is parsed to uint - an
            // existing asymmetry in how this field was already handled, preserved as-is.
            new(YearBox, t => t.Year ?? "", (t, tag, v) => { t.Year = NullIfEmpty(v); tag.Year = ParseUInt(v); }),
            new(GenreBox, t => t.Genre ?? "", (t, tag, v) => { var g = NullIfEmpty(v); t.Genre = g; tag.Genres = g is string gg ? [gg] : []; }),
            new(BpmBox, t => t.BeatsPerMinute > 0 ? t.BeatsPerMinute.ToString() : "", (t, tag, v) => { var n = ParseUInt(v); t.BeatsPerMinute = n; tag.BeatsPerMinute = n; }),
            SimpleField(KeyBox, t => t.InitialKey, (t, v) => t.InitialKey = v, (tag, v) => tag.InitialKey = v),
            SimpleField(GroupingBox, t => t.Grouping, (t, v) => t.Grouping = v, (tag, v) => tag.Grouping = v),

            new(AlbumArtistBox, t => t.AlbumArtists ?? "", (t, tag, v) => { t.AlbumArtists = NullIfEmpty(v); tag.AlbumArtists = SplitArray(v); }),
            new(ComposerBox, t => t.Composers ?? "", (t, tag, v) => { t.Composers = NullIfEmpty(v); tag.Composers = SplitArray(v); }),
            SimpleField(ConductorBox, t => t.Conductor, (t, v) => t.Conductor = v, (tag, v) => tag.Conductor = v),
            SimpleField(RemixedByBox, t => t.RemixedBy, (t, v) => t.RemixedBy = v, (tag, v) => tag.RemixedBy = v),

            SimpleField(SubtitleBox, t => t.Subtitle, (t, v) => t.Subtitle = v, (tag, v) => tag.Subtitle = v),
            SimpleField(DescriptionBox, t => t.Description, (t, v) => t.Description = v, (tag, v) => tag.Description = v),
            SimpleField(CommentBox, t => t.Comment, (t, v) => t.Comment = v, (tag, v) => tag.Comment = v),
            SimpleField(PublisherBox, t => t.Publisher, (t, v) => t.Publisher = v, (tag, v) => tag.Publisher = v),
            SimpleField(CopyrightBox, t => t.Copyright, (t, v) => t.Copyright = v, (tag, v) => tag.Copyright = v),
            SimpleField(ISRCBox, t => t.ISRC, (t, v) => t.ISRC = v, (tag, v) => tag.ISRC = v),

            SimpleField(LyricsBox, t => t.Lyrics, (t, v) => t.Lyrics = v, (tag, v) => tag.Lyrics = v),
        ];
    }

    private void Populate()
    {
        foreach (var field in _fields)
        {
            var values = _editTracks.Select(field.Display).Distinct().ToList();
            if (values.Count == 1)
            {
                field.Box.Text = values[0];
                field.Box.Watermark = null;
                field.OriginalText = values[0];
            }
            else
            {
                field.Box.Text = "";
                field.Box.Watermark = "Multiple values";
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
        BitDepthValue.Text   = UniformOrMixed(t => t.BitsPerSample > 0 ? $"{t.BitsPerSample}-bit" : "—");
        PathValue.Text       = UniformOrMixed(t => t.Path ?? "—");

        _ = LoadAlbumArtAsync();
    }

    // Shows the first selected track's art (embedded tag picture, falling back
    // to a cover/folder image file - see AlbumArtLoader). For a batch selection
    // spanning multiple albums this is necessarily just one representative
    // thumbnail, not a "mixed" indicator - album art has no text form to show
    // "Multiple values" with.
    private async Task LoadAlbumArtAsync()
    {
        var requestId = ++_artRequestId;
        AlbumArtImage.Source = null;
        if (_editTracks.Count == 0)
            return;

        var bmp = await AlbumArtLoader.LoadAsync(_editTracks[0]);
        if (requestId == _artRequestId)
            AlbumArtImage.Source = bmp;
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
        if (dirty.Count == 0)
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
                    field.Apply(track, tag, field.Box.Text);
                tagFile.Save();
            }
            catch (Exception)
            {
                continue;
            }
        }

        // Refresh views bound to the library and persist the change to disk
        _library.UpdateTracks(_library.Tracks);
        await new LibraryStore().SaveAsync(_library.Tracks);
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string[] SplitArray(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? []
            : [.. s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static uint ParseUInt(string? s) =>
        uint.TryParse(s?.Trim(), out var n) ? n : 0;
}
