using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

using Avalonia.Controls;

using Flower.Converters;
using Flower.Models;
using Flower.Persistence;

namespace Flower.Views;

public partial class TrackInfoWindow : Window
{
    private static readonly DurationConverter _durationConverter = new();
    private readonly IReadOnlyList<Track> _tracks;
    private readonly Library _library;
    private int _index;

    private Track _track => _tracks[_index];

    public event EventHandler<Track>? TrackNavigated;

    public TrackInfoWindow(IReadOnlyList<Track> tracks, int index, Library library)
    {
        InitializeComponent();
        _tracks  = tracks;
        _library = library;
        _index   = index;
        Populate(_track);
        UpdateNavButtons();
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
        if (next < 0 || next >= _tracks.Count) return;
        await SaveChanges();
        _index = next;
        Populate(_track);
        UpdateNavButtons();
        TrackNavigated?.Invoke(this, _track);
    }

    private void Populate(Track track)
    {
        // Header
        TitleBox.Text  = track.Title ?? "";
        ArtistBox.Text = track.Artists ?? "";
        AlbumBox.Text  = track.Album ?? "";

        // Album section
        TrackNumBox.Text   = track.TrackNumber > 0 ? track.TrackNumber.ToString() : "";
        TrackTotalBox.Text = track.TrackCount  > 0 ? track.TrackCount.ToString()  : "";
        DiscNumBox.Text    = track.DiscNumber  > 0 ? track.DiscNumber.ToString()  : "";
        DiscTotalBox.Text  = track.DiscCount   > 0 ? track.DiscCount.ToString()   : "";
        YearBox.Text       = track.Year ?? "";
        GenreBox.Text      = track.Genre ?? "";
        BpmBox.Text        = track.BeatsPerMinute > 0 ? track.BeatsPerMinute.ToString() : "";
        KeyBox.Text        = track.InitialKey ?? "";
        GroupingBox.Text   = track.Grouping ?? "";

        // People section
        AlbumArtistBox.Text = track.AlbumArtists ?? "";
        ComposerBox.Text    = track.Composers ?? "";
        ConductorBox.Text   = track.Conductor ?? "";
        RemixedByBox.Text   = track.RemixedBy ?? "";

        // Descriptions section
        SubtitleBox.Text    = track.Subtitle ?? "";
        DescriptionBox.Text = track.Description ?? "";
        CommentBox.Text     = track.Comment ?? "";
        PublisherBox.Text   = track.Publisher ?? "";
        CopyrightBox.Text   = track.Copyright ?? "";
        ISRCBox.Text        = track.ISRC ?? "";

        // Lyrics
        LyricsBox.Text = track.Lyrics ?? "";

        // Technical (read-only)
        DurationValue.Text   = _durationConverter.Convert(track.Duration, typeof(string), null, CultureInfo.CurrentCulture) as string ?? "—";
        CodecValue.Text      = track.Codec ?? "—";
        BitrateValue.Text    = track.Bitrate > 0 ? $"{track.Bitrate} kbps" : "—";
        SampleRateValue.Text = track.SampleRate > 0 ? $"{track.SampleRate / 1000.0:0.###} kHz" : "—";
        ChannelsValue.Text   = track.Channels switch { 1 => "Mono", 2 => "Stereo", > 2 => $"{track.Channels} channels", _ => "—" };
        BitDepthValue.Text   = track.BitsPerSample > 0 ? $"{track.BitsPerSample}-bit" : "—";

        // File
        PathValue.Text = track.Path ?? "—";
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
        if (_track.Path is not string path) return;

        try
        {
            using var tagFile = TagLib.File.Create(path);
            var tag = tagFile.Tag;

            tag.Title       = NullIfEmpty(TitleBox.Text);
            tag.Performers  = SplitArray(ArtistBox.Text);
            tag.Album       = NullIfEmpty(AlbumBox.Text);
            tag.AlbumArtists = SplitArray(AlbumArtistBox.Text);
            tag.Track       = ParseUInt(TrackNumBox.Text);
            tag.TrackCount  = ParseUInt(TrackTotalBox.Text);
            tag.Disc        = ParseUInt(DiscNumBox.Text);
            tag.DiscCount   = ParseUInt(DiscTotalBox.Text);
            tag.Year        = ParseUInt(YearBox.Text);
            tag.Genres      = NullIfEmpty(GenreBox.Text) is string g ? [g] : [];
            tag.BeatsPerMinute = ParseUInt(BpmBox.Text);
            tag.InitialKey  = NullIfEmpty(KeyBox.Text);
            tag.Grouping    = NullIfEmpty(GroupingBox.Text);
            tag.AlbumArtists = SplitArray(AlbumArtistBox.Text);
            tag.Composers   = SplitArray(ComposerBox.Text);
            tag.Conductor   = NullIfEmpty(ConductorBox.Text);
            tag.RemixedBy   = NullIfEmpty(RemixedByBox.Text);
            tag.Subtitle    = NullIfEmpty(SubtitleBox.Text);
            tag.Description = NullIfEmpty(DescriptionBox.Text);
            tag.Comment     = NullIfEmpty(CommentBox.Text);
            tag.Publisher   = NullIfEmpty(PublisherBox.Text);
            tag.Copyright   = NullIfEmpty(CopyrightBox.Text);
            tag.ISRC        = NullIfEmpty(ISRCBox.Text);
            tag.Lyrics      = NullIfEmpty(LyricsBox.Text);

            tagFile.Save();
        }
        catch (Exception)
        {
            return;
        }

        // Mirror changes into the in-memory Track
        _track.Title          = NullIfEmpty(TitleBox.Text);
        _track.Artists        = NullIfEmpty(ArtistBox.Text);
        _track.Album          = NullIfEmpty(AlbumBox.Text);
        _track.AlbumArtists   = NullIfEmpty(AlbumArtistBox.Text);
        _track.TrackNumber    = ParseUInt(TrackNumBox.Text);
        _track.TrackCount     = ParseUInt(TrackTotalBox.Text);
        _track.DiscNumber     = ParseUInt(DiscNumBox.Text);
        _track.DiscCount      = ParseUInt(DiscTotalBox.Text);
        _track.Year           = NullIfEmpty(YearBox.Text);
        _track.Genre          = NullIfEmpty(GenreBox.Text);
        _track.BeatsPerMinute = ParseUInt(BpmBox.Text);
        _track.InitialKey     = NullIfEmpty(KeyBox.Text);
        _track.Grouping       = NullIfEmpty(GroupingBox.Text);
        _track.Composers      = NullIfEmpty(ComposerBox.Text);
        _track.Conductor      = NullIfEmpty(ConductorBox.Text);
        _track.RemixedBy      = NullIfEmpty(RemixedByBox.Text);
        _track.Subtitle       = NullIfEmpty(SubtitleBox.Text);
        _track.Description    = NullIfEmpty(DescriptionBox.Text);
        _track.Comment        = NullIfEmpty(CommentBox.Text);
        _track.Publisher      = NullIfEmpty(PublisherBox.Text);
        _track.Copyright      = NullIfEmpty(CopyrightBox.Text);
        _track.ISRC           = NullIfEmpty(ISRCBox.Text);
        _track.Lyrics         = NullIfEmpty(LyricsBox.Text);

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
