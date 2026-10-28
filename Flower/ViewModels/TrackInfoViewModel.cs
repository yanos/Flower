using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Avalonia;

using Material.Icons;

using Microsoft.Extensions.Logging;

using Flower.Converters;
using Flower.Importer;
using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels;

// What Track Info shows and edits, for the desktop window and the phone's
// sheet alike: the tag boxes, the rule for songs that disagree, how every
// read-only value is written out, who may edit, the staged edits and the
// writes that happen at once.
//
// Both heads used to do all of this in code-behind, each its own way, and had
// drifted: two definitions of "these songs disagree", two copies of every
// formatting lambda, and the phone without the encoding or the compilation
// flag - so on a phone that flag could be lost through a tag rewrite and
// never set. The views keep only what is theirs: layout, the art bitmaps,
// pickers, drag and drop.
//
// Two save rules, which is the one thing worth keeping straight. The tags -
// every box, and the compilation flag - are staged and written by Save, and
// leaving without saving drops them. The star and the three playback options
// are Flower's own record rather than tags, so each writes the moment it is
// changed: there is no file write to batch them with.
public sealed class TrackInfoViewModel : ViewModelBase
{
    public const string MixedValues = "Multiple values";

    private static readonly DurationConverter DurationText = new();

    private readonly Library _library;
    private readonly Func<Track, bool> _mayEdit;
    private readonly Func<IReadOnlyList<Track>, string?> _serverSourceName;
    private readonly ILogger _logger;

    public TrackInfoViewModel(
        Library library,
        Func<Track, bool> mayEdit,
        Func<IReadOnlyList<Track>, string?> serverSourceName,
        ILogger logger)
    {
        _library = library;
        _mayEdit = mayEdit;
        _serverSourceName = serverSourceName;
        _logger = logger;

        _artistSuggestions = new(() => TagSuggestionSource.DistinctArtists(_library.Tracks));
        _albumSuggestions = new(() => TagSuggestionSource.DistinctAlbums(_library.Tracks));
        _composerSuggestions = new(() => TagSuggestionSource.DistinctComposers(_library.Tracks));
        _genreSuggestions = new(() => TagSuggestionSource.DistinctGenres(_library.Tracks));

        Fields =
        [
            Title, Artists, Album, AlbumArtists,
            TrackNumber, TrackCount, DiscNumber, DiscCount,
            Year, Genre, BeatsPerMinute, InitialKey, Grouping,
            Composers, Conductor, RemixedBy,
            Subtitle, Description, Comment, Publisher, Copyright, ISRC,
            Lyrics,
            TitleSort, ArtistsSort, AlbumSort, ComposersSort,
        ];
    }

    // ── The songs ──────────────────────────────────────────────────────────

    // One song, or every song of a selection or an album, edited as one.
    public IReadOnlyList<Track> Tracks { get; private set; } = [];

    // Tags are for whoever may edit them (MainViewModel.CanEditTagsOf):
    // anyone, of a song of their own; an administrator, of the server's.
    // Shown but not editable otherwise, rather than editable and ignored.
    public bool IsEditable { get; private set; }

    public void Load(IReadOnlyList<Track> tracks)
    {
        Tracks = tracks;
        IsEditable = tracks.Count > 0 && tracks.All(_mayEdit);

        foreach (var field in Fields)
            field.Show(TrackTagFields.CommonValue(field.Tag, tracks), HintFor(field));

        _compilationShown = UniformFlag(t => t.IsCompilation);
        _compilation = _compilationShown;
        _rememberPosition = UniformFlag(t => t.RememberPlaybackPosition);
        RememberPositionIsThreeState = _rememberPosition is null;
        _ignoreWhenShuffling = UniformFlag(t => t.IgnoreWhenShuffling);
        IgnoreWhenShufflingIsThreeState = _ignoreWhenShuffling is null;
        var adjustments = tracks.Select(t => t.VolumeAdjustment).Distinct().ToList();
        _volumeAdjustment = adjustments.Count == 1 ? adjustments[0] : 0;

        // Every read-only value below is computed from Tracks on read, so one
        // notification for all of them is the whole of a reload.
        OnPropertyChanged(string.Empty);
    }

    // The sort tags show the value they sort, when empty: an empty box reads
    // as "sorts under its own name" rather than as a field nobody filled in.
    private string? HintFor(TrackInfoField field)
    {
        var sorted = field == TitleSort ? Title
            : field == ArtistsSort ? Artists
            : field == AlbumSort ? Album
            : field == ComposersSort ? Composers
            : null;
        return sorted is null ? null : TrackTagFields.CommonValue(sorted.Tag, Tracks) is { Length: > 0 } value ? value : null;
    }

    // ── Tag boxes ──────────────────────────────────────────────────────────

    public TrackInfoField Title { get; } = new(TrackTagFields.Title);
    public TrackInfoField Artists { get; } = new(TrackTagFields.Artists);
    public TrackInfoField Album { get; } = new(TrackTagFields.Album);
    public TrackInfoField AlbumArtists { get; } = new(TrackTagFields.AlbumArtists);
    public TrackInfoField TrackNumber { get; } = new(TrackTagFields.TrackNumber);
    public TrackInfoField TrackCount { get; } = new(TrackTagFields.TrackCount);
    public TrackInfoField DiscNumber { get; } = new(TrackTagFields.DiscNumber);
    public TrackInfoField DiscCount { get; } = new(TrackTagFields.DiscCount);
    public TrackInfoField Year { get; } = new(TrackTagFields.Year);
    public TrackInfoField Genre { get; } = new(TrackTagFields.Genre);
    public TrackInfoField BeatsPerMinute { get; } = new(TrackTagFields.BeatsPerMinute);
    public TrackInfoField InitialKey { get; } = new(TrackTagFields.InitialKey);
    public TrackInfoField Grouping { get; } = new(TrackTagFields.Grouping);
    public TrackInfoField Composers { get; } = new(TrackTagFields.Composers);
    public TrackInfoField Conductor { get; } = new(TrackTagFields.Conductor);
    public TrackInfoField RemixedBy { get; } = new(TrackTagFields.RemixedBy);
    public TrackInfoField Subtitle { get; } = new(TrackTagFields.Subtitle);
    public TrackInfoField Description { get; } = new(TrackTagFields.Description);
    public TrackInfoField Comment { get; } = new(TrackTagFields.Comment);
    public TrackInfoField Publisher { get; } = new(TrackTagFields.Publisher);
    public TrackInfoField Copyright { get; } = new(TrackTagFields.Copyright);
    public TrackInfoField ISRC { get; } = new(TrackTagFields.ISRC);
    public TrackInfoField Lyrics { get; } = new(TrackTagFields.Lyrics);
    public TrackInfoField TitleSort { get; } = new(TrackTagFields.TitleSort);
    public TrackInfoField ArtistsSort { get; } = new(TrackTagFields.ArtistsSort);
    public TrackInfoField AlbumSort { get; } = new(TrackTagFields.AlbumSort);
    public TrackInfoField ComposersSort { get; } = new(TrackTagFields.ComposersSort);

    public IReadOnlyList<TrackInfoField> Fields { get; }

    // The library's own spellings, offered under a box as it is typed in.
    // Read once per Track Info rather than live: see TagSuggestionSource.
    // Artists and album artists share one pool.
    private readonly Lazy<List<string>> _artistSuggestions;
    private readonly Lazy<List<string>> _albumSuggestions;
    private readonly Lazy<List<string>> _composerSuggestions;
    private readonly Lazy<List<string>> _genreSuggestions;

    public IReadOnlyList<string> ArtistSuggestions => _artistSuggestions.Value;
    public IReadOnlyList<string> AlbumSuggestions => _albumSuggestions.Value;
    public IReadOnlyList<string> ComposerSuggestions => _composerSuggestions.Value;
    public IReadOnlyList<string> GenreSuggestions => _genreSuggestions.Value;

    // ── Compilation ────────────────────────────────────────────────────────
    //
    // A tag, staged like the boxes, but not one of them: the generic TagLib
    // Tag has no place for it, so it is written per format (CompilationFlag).

    private bool? _compilationShown;
    private bool? _compilation;

    // Null while the songs disagree, which is also what the three-state
    // checkbox shows - and leaving it there is what "don't change it" means.
    public bool? IsCompilation
    {
        get => _compilation;
        set => SetProperty(ref _compilation, value);
    }

    // Three-state only for a mixed selection, so one song's checkbox cannot
    // be cycled into an indeterminate state that means nothing.
    public bool CompilationIsThreeState => _compilationShown is null;

    // ── Playback options (written at once) ─────────────────────────────────

    private bool? _rememberPosition;
    private bool? _ignoreWhenShuffling;
    private double _volumeAdjustment;
    private bool _rememberPositionIsThreeState;
    private bool _ignoreWhenShufflingIsThreeState;

    public bool? RememberPlaybackPosition
    {
        get => _rememberPosition;
        set
        {
            if (!SetProperty(ref _rememberPosition, value) || value is not { } remember)
                return;

            foreach (var track in Tracks)
            {
                track.RememberPlaybackPosition = remember;
                // Turning it off throws away where the song had got to, rather
                // than parking it for a later re-tick to resurrect. Half a
                // listening session remembered from an unknown time in the
                // past is worse than starting over.
                if (!remember)
                    track.ResumePosition = null;
            }

            RememberPositionIsThreeState = false;
            OnPropertyChanged(nameof(ResumePositionText));
            PersistOptions();
        }
    }

    public bool RememberPositionIsThreeState
    {
        get => _rememberPositionIsThreeState;
        private set => SetProperty(ref _rememberPositionIsThreeState, value);
    }

    public bool? IgnoreWhenShuffling
    {
        get => _ignoreWhenShuffling;
        set
        {
            if (!SetProperty(ref _ignoreWhenShuffling, value) || value is not { } ignore)
                return;

            foreach (var track in Tracks)
                track.IgnoreWhenShuffling = ignore;

            IgnoreWhenShufflingIsThreeState = false;
            PersistOptions();
        }
    }

    public bool IgnoreWhenShufflingIsThreeState
    {
        get => _ignoreWhenShufflingIsThreeState;
        private set => SetProperty(ref _ignoreWhenShufflingIsThreeState, value);
    }

    // Percentage points, -100..100 - see Track.VolumeAdjustment. A mixed
    // selection shows zero, and moving the slider sets them all.
    public double VolumeAdjustment
    {
        get => _volumeAdjustment;
        set
        {
            if (!SetProperty(ref _volumeAdjustment, value))
                return;

            var points = (int)Math.Round(value);
            foreach (var track in Tracks)
                track.VolumeAdjustment = points;

            OnPropertyChanged(nameof(VolumeAdjustmentText));
            OnPropertyChanged(nameof(IsVolumeAdjusted));
            PersistOptions();
        }
    }

    public bool IsVolumeAdjusted => (int)Math.Round(_volumeAdjustment) != 0;

    public string VolumeAdjustmentText
    {
        get
        {
            var points = (int)Math.Round(_volumeAdjustment);
            return points == 0 ? "None" : points.ToString("+0;-0", CultureInfo.CurrentCulture) + "%";
        }
    }

    public string ResumePositionText =>
        Tracks.Select(t => t.ResumePosition).Distinct().ToList() is [{ } position] && position > TimeSpan.Zero
            ? $@"— currently at {position:h\:mm\:ss}"
            : "";

    // The compilation flag and the sort tags live in the file's tags, so they
    // are as editable as the boxes are; the playback options work for any
    // song, since they are library state.
    public string OptionsUnavailableText => IsEditable
        ? ""
        : "Compilation and the sort tags live in the file's own tags, and this track has no file here or on a server this device administers. "
          + "The playback options still apply - those are Flower's own, not tags.";

    // One event for every edited song, per change. None of the three shows in
    // a track list, so TrackChanged's subscribers only refresh rows and smart
    // playlists (IgnoreWhenShuffling is a rule field) - no rebuild and no
    // catalog sync, which would otherwise be one per drag of the slider.
    private void PersistOptions() =>
        _library.NotifyTracksChanged(Tracks, TrackChange.Options);

    // ── The star (written at once) ─────────────────────────────────────────

    private int StarredCount => Tracks.Count(t => t.Starred);

    public bool IsAllStarred => Tracks.Count > 0 && StarredCount == Tracks.Count;

    public bool IsAnyStarred => StarredCount > 0;

    public MaterialIconKind StarIcon =>
        IsAllStarred ? MaterialIconKind.Star : IsAnyStarred ? MaterialIconKind.StarHalfFull : MaterialIconKind.StarOutline;

    public string StarToolTip => IsAllStarred ? "Remove the star" : "Star";

    // Library.SetStarred is the one path that both changes a song and
    // persists it, so this goes through it rather than staging for Save.
    public void ToggleStar()
    {
        if (Tracks.Count == 0)
            return;

        var star = !IsAllStarred;
        foreach (var track in Tracks)
            _library.SetStarred(StarTarget.Song, track.Id.ToString(), star);

        OnPropertyChanged(nameof(IsAllStarred));
        OnPropertyChanged(nameof(IsAnyStarred));
        OnPropertyChanged(nameof(StarIcon));
        OnPropertyChanged(nameof(StarToolTip));
    }

    // ── Read-only values ───────────────────────────────────────────────────

    // The one rule for songs that disagree, for everything that is not a box.
    private string Summary(Func<Track, string> display) =>
        Tracks.Select(display).Distinct().Take(2).ToList() is [var only] ? only : MixedValues;

    public string TitleText => Summary(t => t.Title ?? "");
    public string ArtistText => Summary(t => t.Artists ?? "");
    public string AlbumText => Summary(t => t.Album ?? "");

    // The full-size cover's title, for the window that has one.
    public string ArtworkTitle => Summary(t => t.Album ?? t.Title ?? "Artwork");

    public string DurationValue =>
        Summary(t => DurationText.Convert(t.Duration, typeof(string), null, CultureInfo.CurrentCulture) as string ?? "—");

    public string CodecValue => Summary(t => t.Codec ?? "—");
    public string BitrateValue => Summary(t => t.Bitrate > 0 ? $"{t.Bitrate} kbps" : "—");
    public string SampleRateValue => Summary(t => t.SampleRate > 0 ? $"{t.SampleRate / 1000.0:0.###} kHz" : "—");

    public string ChannelsValue => Summary(t => t.Channels switch
    {
        1 => "Mono",
        2 => "Stereo",
        > 2 => $"{t.Channels} channels",
        _ => "—",
    });

    public string BitDepthValue => Summary(t => t.BitsPerSample > 0 ? $"{t.BitsPerSample}-bit" : "—");

    // Read at import like every other technical value, not parsed here on
    // demand - see Track.EncoderProfile.
    public string EncodingValue => Summary(t => t.EncoderProfile ?? "—");

    public string DateAddedValue => Summary(t => t.DateAdded.LocalDateTime.ToString("MMM d, yyyy"));

    // The sum of this device's own count, whatever was imported from iTunes
    // and every other device's - see Track.TotalPlayCount, the same number
    // the track list's Plays column shows.
    public string PlayCountValue => Summary(t => t.TotalPlayCount.ToString(CultureInfo.CurrentCulture));

    public bool IsPlayCountMixed => PlayCountValue == MixedValues;

    // Recorded on play-start (Track.LastPlayedAt), and null for anything never
    // played here - including a song whose whole count was imported from
    // iTunes, which is why a "—" beside a non-zero count is right rather than
    // a bug. The time as well as the date: it is the one value here that
    // answers "was that just now?".
    public string LastPlayedValue => Summary(t => t.LastPlayedAt is { } at
        ? $"{at.LocalDateTime.ToString("MMM d, yyyy", CultureInfo.CurrentCulture)} {at.LocalDateTime.ToShortTimeString()}"
        : "—");

    // "File" names a file only when there is one; for songs that live only on
    // the paired server, the source is that server.
    public string? ServerSourceName => _serverSourceName(Tracks);
    public bool IsFromServer => ServerSourceName != null;
    public string SourceLabel => IsFromServer ? "Source" : "File";
    public string PathValue => Summary(t => t.Path ?? "—");
    public string SourceValue => ServerSourceName ?? PathValue;

    // ── Saving ─────────────────────────────────────────────────────────────

    // The boxes whose text changed since Load, and the compilation flag if it
    // was moved, applied to every song this device may edit, file first
    // (TrackTagEdits). A song this device only streams is edited on the song,
    // and the edit goes up to the server from there. Returns the songs edited.
    public IReadOnlyList<Track> Save()
    {
        var edits = Fields.Where(f => f.IsDirty).Select(f => (f.Tag, f.Text)).ToList();
        var compilation = IsCompilation == _compilationShown ? null : IsCompilation;
        if (edits.Count == 0 && compilation == null)
            return [];

        var edited = TrackTagEdits.Apply(
            Tracks,
            edits,
            _mayEdit,
            _logger,
            perFile: (file, _) => compilation is not { } isCompilation || CompilationFlag.Write(file, isCompilation),
            afterFile: track =>
            {
                if (compilation is { } isCompilation)
                    track.IsCompilation = isCompilation;
            });

        // NotifyTracksChanged, not UpdateTracks(Library.Tracks) - see
        // MainViewModel.SyncITunesPlayCountAsync on why passing Tracks back in
        // as a "fresh scan" silently doubles every placeholder. Persists just
        // these rows.
        _library.NotifyTracksChanged(edited, TrackChange.Tags);

        // What was written is now what is shown, so a second Save - Prev/Next
        // then OK - writes nothing twice.
        foreach (var field in Fields)
            field.Show(TrackTagFields.CommonValue(field.Tag, Tracks), HintFor(field));
        _compilationShown = UniformFlag(t => t.IsCompilation);
        _compilation = _compilationShown;
        OnPropertyChanged(nameof(IsCompilation));
        OnPropertyChanged(nameof(CompilationIsThreeState));

        return edited;
    }

    // ── Artwork ────────────────────────────────────────────────────────────

    // A cover's pixel size as Track Info writes it, on both heads.
    public static string Resolution(PixelSize size) =>
        $"{size.Width.ToString(CultureInfo.CurrentCulture)} × {size.Height.ToString(CultureInfo.CurrentCulture)}";

    // The line under the desktop's Artwork tab: what the cover is, and how it
    // can be changed - or why it cannot. writable is how many songs have a
    // file here to write into, serverAlbums how many of the paired server's
    // albums its administrator could have the server write instead, and
    // multiple that the songs do not share one cover (AlbumArtLoader.ShareOneCover).
    public string DescribeArtwork(LocalAlbumArt? art, PixelSize? size, int writable, int serverAlbums, bool multiple)
    {
        if (Tracks.Count == 0)
            return "";

        var hasArt = art?.Bytes is { Length: > 0 };
        if (writable == 0 && serverAlbums == 0)
            return hasArt
                // Art there is - it came from the server's own copy - but no
                // way to change it: either this device is not an admin of that
                // server, or the server is not reachable from here right now.
                ? "Only on the server, which can't be reached to change this."
                : "Only on the server — there's nothing here to write artwork into.";

        var parts = new List<string>();
        if (multiple)
        {
            parts.Add("Multiple album art");
        }
        else if (hasArt)
        {
            if (size is { } pixels)
                parts.Add(Resolution(pixels));
            if (FormatName(art!.MimeType) is string format)
                parts.Add(format);
            parts.Add(FileSize(art!.Bytes.Length));
        }
        else
        {
            parts.Add("No artwork");
        }

        parts.Add("Drop an image or choose a file");
        return string.Join("  ·  ", parts);
    }

    // Null for a placeholder's art, whose bytes come from the content-
    // addressed cache with no MIME type recorded (AlbumArtLoader.TryGetArt).
    private static string? FormatName(string mimeType) =>
        mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && mimeType.Length > 6
            ? mimeType[6..].ToUpperInvariant()
            : null;

    // A cover's size on disk as Track Info writes it, on both heads.
    public static string FileSize(int count) =>
        count >= 1024 * 1024 ? $"{count / (1024.0 * 1024.0):0.#} MB" : $"{count / 1024.0:0} KB";

    // True/false when every song agrees, null when they don't.
    private bool? UniformFlag(Func<Track, bool> get)
    {
        if (Tracks.Count == 0)
            return false;

        var first = get(Tracks[0]);
        return Tracks.All(t => get(t) == first) ? first : null;
    }
}
