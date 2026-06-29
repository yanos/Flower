using System.Globalization;

using Avalonia.Controls;

using Flower.Converters;
using Flower.Models;

namespace Flower.Views;

public partial class TrackInfoWindow : Window
{
    private static readonly DurationConverter _durationConverter = new();

    public TrackInfoWindow(Track track)
    {
        InitializeComponent();
        Populate(track);
    }

    private void Populate(Track track)
    {
        // Header
        TitleLabel.Text  = track.Title ?? "(Unknown title)";
        ArtistLabel.Text = track.Artists ?? "(Unknown artist)";
        AlbumLabel.Text  = track.Album ?? "";

        // Album section
        TrackNumValue.Text  = FormatNumber(track.TrackNumber, track.TrackCount);
        DiscValue.Text      = FormatNumber(track.DiscNumber, track.DiscCount);
        YearValue.Text      = track.Year ?? "—";
        GenreValue.Text     = track.Genre ?? "—";
        BpmValue.Text       = track.BeatsPerMinute > 0 ? track.BeatsPerMinute.ToString() : "—";
        KeyValue.Text       = track.InitialKey ?? "—";
        GroupingValue.Text  = track.Grouping ?? "—";

        // People section
        AlbumArtistValue.Text = track.AlbumArtists ?? "—";
        ComposerValue.Text    = track.Composers ?? "—";
        ConductorValue.Text   = track.Conductor ?? "—";
        RemixedByValue.Text   = track.RemixedBy ?? "—";

        // Descriptions section
        SubtitleValue.Text    = track.Subtitle ?? "—";
        DescriptionValue.Text = track.Description ?? "—";
        CommentValue.Text     = track.Comment ?? "—";
        PublisherValue.Text   = track.Publisher ?? "—";
        CopyrightValue.Text   = track.Copyright ?? "—";
        ISRCValue.Text        = track.ISRC ?? "—";

        // Technical section
        DurationValue.Text   = _durationConverter.Convert(track.Duration, typeof(string), null, CultureInfo.CurrentCulture) as string ?? "—";
        CodecValue.Text      = track.Codec ?? "—";
        BitrateValue.Text    = track.Bitrate > 0 ? $"{track.Bitrate} kbps" : "—";
        SampleRateValue.Text = track.SampleRate > 0 ? $"{track.SampleRate / 1000.0:0.###} kHz" : "—";
        ChannelsValue.Text   = track.Channels switch { 1 => "Mono", 2 => "Stereo", > 2 => $"{track.Channels} channels", _ => "—" };
        BitDepthValue.Text   = track.BitsPerSample > 0 ? $"{track.BitsPerSample}-bit" : "—";

        // Lyrics (only shown if present)
        if (!string.IsNullOrWhiteSpace(track.Lyrics))
        {
            LyricsValue.Text      = track.Lyrics;
            LyricsSection.IsVisible = true;
        }

        // File
        PathValue.Text = track.Path ?? "—";
    }

    private static string FormatNumber(uint number, uint total) =>
        (number, total) switch
        {
            (0, _)   => "—",
            (_, 0)   => number.ToString(),
            var (n, t) => $"{n} of {t}"
        };
}
