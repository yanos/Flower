using System;
using System.ComponentModel;
using System.Globalization;

using Avalonia.Controls;
using Avalonia.Interactivity;

using Flower.Converters;
using Flower.Models;
using Flower.Persistence;
using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile;

// Mobile counterpart to the desktop TrackInfoWindow: same TagLib-backed fields,
// but rendered as a sheet within MobileMainView rather than a child Window (mobile
// has no concept of secondary windows). Populated whenever the host view model's
// ActionTarget becomes the track shown by an open Track Info sheet.
public partial class TrackInfoView : UserControl
{
    private static readonly DurationConverter _durationConverter = new();
    private Track? _track;

    public TrackInfoView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MobileMainViewModel vm)
                vm.PropertyChanged += Vm_PropertyChanged;
        };
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MobileMainViewModel vm) return;
        if (e.PropertyName is nameof(MobileMainViewModel.ActionTarget) or nameof(MobileMainViewModel.ActiveSheet))
        {
            if (vm.IsShowingTrackInfo && vm.ActionTarget != null && vm.ActionTarget != _track)
                Populate(vm.ActionTarget);
        }
    }

    private void Populate(Track track)
    {
        _track = track;

        TitleBox.Text  = track.Title ?? "";
        ArtistBox.Text = track.Artists ?? "";
        AlbumBox.Text  = track.Album ?? "";

        TrackNumBox.Text   = track.TrackNumber > 0 ? track.TrackNumber.ToString() : "";
        TrackTotalBox.Text = track.TrackCount  > 0 ? track.TrackCount.ToString()  : "";
        DiscNumBox.Text    = track.DiscNumber  > 0 ? track.DiscNumber.ToString()  : "";
        DiscTotalBox.Text  = track.DiscCount   > 0 ? track.DiscCount.ToString()   : "";
        YearBox.Text       = track.Year ?? "";
        GenreBox.Text      = track.Genre ?? "";
        BpmBox.Text        = track.BeatsPerMinute > 0 ? track.BeatsPerMinute.ToString() : "";
        KeyBox.Text        = track.InitialKey ?? "";
        GroupingBox.Text   = track.Grouping ?? "";

        AlbumArtistBox.Text = track.AlbumArtists ?? "";
        ComposerBox.Text    = track.Composers ?? "";
        ConductorBox.Text   = track.Conductor ?? "";
        RemixedByBox.Text   = track.RemixedBy ?? "";

        SubtitleBox.Text    = track.Subtitle ?? "";
        DescriptionBox.Text = track.Description ?? "";
        CommentBox.Text     = track.Comment ?? "";
        PublisherBox.Text   = track.Publisher ?? "";
        CopyrightBox.Text   = track.Copyright ?? "";
        ISRCBox.Text        = track.ISRC ?? "";

        LyricsBox.Text = track.Lyrics ?? "";

        DurationValue.Text   = _durationConverter.Convert(track.Duration, typeof(string), null, CultureInfo.CurrentCulture) as string ?? "—";
        CodecValue.Text      = track.Codec ?? "—";
        BitrateValue.Text    = track.Bitrate > 0 ? $"{track.Bitrate} kbps" : "—";
        SampleRateValue.Text = track.SampleRate > 0 ? $"{track.SampleRate / 1000.0:0.###} kHz" : "—";
        ChannelsValue.Text   = track.Channels switch { 1 => "Mono", 2 => "Stereo", > 2 => $"{track.Channels} channels", _ => "—" };
        BitDepthValue.Text   = track.BitsPerSample > 0 ? $"{track.BitsPerSample}-bit" : "—";

        PathValue.Text = track.Path ?? "—";
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

    private async System.Threading.Tasks.Task SaveChanges()
    {
        if (_track?.Path is not string path || DataContext is not MobileMainViewModel vm) return;

        try
        {
            using var tagFile = TagLib.File.Create(path);
            var tag = tagFile.Tag;

            tag.Title        = NullIfEmpty(TitleBox.Text);
            tag.Performers    = SplitArray(ArtistBox.Text);
            tag.Album         = NullIfEmpty(AlbumBox.Text);
            tag.AlbumArtists  = SplitArray(AlbumArtistBox.Text);
            tag.Track         = ParseUInt(TrackNumBox.Text);
            tag.TrackCount    = ParseUInt(TrackTotalBox.Text);
            tag.Disc          = ParseUInt(DiscNumBox.Text);
            tag.DiscCount     = ParseUInt(DiscTotalBox.Text);
            tag.Year          = ParseUInt(YearBox.Text);
            tag.Genres        = NullIfEmpty(GenreBox.Text) is string g ? [g] : [];
            tag.BeatsPerMinute = ParseUInt(BpmBox.Text);
            tag.InitialKey    = NullIfEmpty(KeyBox.Text);
            tag.Grouping      = NullIfEmpty(GroupingBox.Text);
            tag.Composers     = SplitArray(ComposerBox.Text);
            tag.Conductor     = NullIfEmpty(ConductorBox.Text);
            tag.RemixedBy     = NullIfEmpty(RemixedByBox.Text);
            tag.Subtitle      = NullIfEmpty(SubtitleBox.Text);
            tag.Description   = NullIfEmpty(DescriptionBox.Text);
            tag.Comment       = NullIfEmpty(CommentBox.Text);
            tag.Publisher     = NullIfEmpty(PublisherBox.Text);
            tag.Copyright     = NullIfEmpty(CopyrightBox.Text);
            tag.ISRC          = NullIfEmpty(ISRCBox.Text);
            tag.Lyrics        = NullIfEmpty(LyricsBox.Text);

            tagFile.Save();
        }
        catch (Exception)
        {
            return;
        }

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

        vm.Main.Library.UpdateTracks(vm.Main.Library.Tracks);
        await new LibraryStore().SaveAsync(vm.Main.Library.Tracks);
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
