using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels.Mobile;

namespace Flower.ViewModels;

// The phone's Search tab: its query, the three sections of results, and the
// suggestions under the box. Its own class rather than a region of
// MobileMainViewModel (docs/CODE-REVIEW-2026-09.md E3): nothing in here needs
// the shell, and the shell needs only to know when the query or the results
// change (QueryChanged, ResultsChanged) and to ask for a fresh scan when the
// tab is entered (RefreshNow).
//
// The query is deliberately its own, not Main.FilterText. Search is a one-off
// lookup across the whole library, surfaced in its own results view - it was
// never meant to act as a filter that follows the user to other tabs. Sharing
// Main.FilterText used to do exactly that: the Search tab pointed at the same
// "Songs" scope Main.Rows uses, so a query typed here stayed live in
// Main.FilterText and kept narrowing the Songs tab's own list for a moment
// (or, in the worst case, until something else happened to clear it) after
// switching tabs.
public sealed class SearchViewModel : ViewModelBase
{
    // Albums matching by Album name, Artists matching by Artists (the same raw
    // per-track field the Artists tab's own picker groups by - see
    // MainViewModel.RebuildSubListItems), Songs through TrackListBuilder. All
    // three capped: unlike the Songs tab's own virtualized list, the results
    // view is a plain ScrollViewer with an ItemsControl per section (mixing
    // three different item shapes under one scroll, which a single
    // virtualizing list can't do) - realizing thousands of un-virtualized
    // track rows for a broad one-letter query froze the UI in practice.
    public const int MaxResultsPerSection = 40;

    // At most this many rows under the box - past it the dropdown would reach
    // down under the keyboard, where nobody can tap it.
    public const int MaxSuggestions = 5;

    private readonly MainViewModel _main;

    public SearchViewModel(MainViewModel main)
    {
        _main = main;
        ClearRecentSearchesCommand = new RelayCommand(_main.ClearRecentSearches);
        ApplySuggestionCommand = new RelayCommand<string>(query =>
        {
            Query = query;
            Remember();
        });
        RefreshSuggestions();
    }

    public event EventHandler? QueryChanged;
    public event EventHandler? ResultsChanged;

    private string? _query;

    public string? Query
    {
        get => _query;
        set
        {
            if (_query == value)
                return;
            _query = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasQuery));
            OnPropertyChanged(nameof(ShowsNoResults));
            QueryChanged?.Invoke(this, EventArgs.Empty);
            ScheduleSuggestions(SuggestionDelay);
            ScheduleRebuild();
        }
    }

    public bool HasQuery => !string.IsNullOrEmpty(_query);

    // A query coming back with the screen it was typed on (going back to
    // Search). No rebuild of its own: entering the tab runs one (RefreshNow)
    // against what is restored here.
    internal void Restore(string? query)
    {
        _query = query;
        OnPropertyChanged(nameof(Query));
    }

    // ── Results ──────────────────────────────────────────────────────────

    public ObservableCollection<AlbumTileViewModel> AlbumResults { get; } = new();

    // The Artists tab's own row, so a result carries the same collage of the
    // artist's covers and the same counts - built for the result rather than
    // looked up in that tab's list, which only exists while it is filled.
    public ObservableCollection<ArtistPickerRow> ArtistResults { get; } = new();

    // A separate row list from Main.Rows, so it keeps itself right when the
    // server's reachability changes (ApplyAvailability), as Main.Rows does.
    public ObservableCollection<TrackRowViewModel> SongResults { get; } = new();

    public bool HasAlbumResults => AlbumResults.Count > 0;
    public bool HasArtistResults => ArtistResults.Count > 0;
    public bool HasSongResults => SongResults.Count > 0;
    public bool HasAnyResults => HasAlbumResults || HasArtistResults || HasSongResults;

    // Something typed, and nothing found for it - the desktop page's empty
    // state.
    public bool ShowsNoResults => HasQuery && !HasAnyResults;

    // True if any section had more matches than the cap - one "refine your
    // search" caption rather than one per section, since usually either none
    // or all of them are far over it together.
    private bool _hasMoreResults;

    public bool HasMoreResults
    {
        get => _hasMoreResults;
        private set => SetProperty(ref _hasMoreResults, value);
    }

    private CancellationTokenSource? _rebuild;

    // Immediate - for entering the tab or the library changing, neither of
    // which happens once per keystroke.
    public void RefreshNow()
    {
        _rebuild?.Cancel();
        _rebuild = new CancellationTokenSource();
        _ = RebuildAsync(_rebuild.Token);
    }

    // Leaving the tab: a scan in flight is for a screen nobody is looking
    // at. The results themselves stay, so coming back shows them at once.
    public void CancelRebuild() => _rebuild?.Cancel();

    // Debounced, the same 250ms cancel-and-restart shape as
    // MainViewModel.ScheduleFilter - rebuilding on every keystroke discarded
    // and rebuilt every matching album's tile from scratch each time
    // (including any art already mid-load), seen as the app freezing while
    // typing.
    private void ScheduleRebuild()
    {
        _rebuild?.Cancel();
        _rebuild = new CancellationTokenSource();
        _ = DebouncedRebuildAsync(_rebuild.Token);
    }

    private async Task DebouncedRebuildAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(250, token);
        }
        catch (OperationCanceledException)
        {
            return; // A newer keystroke restarted the cooldown - that call's own delay will fire instead.
        }
        await RebuildAsync(token);
    }

    // All three sections matched directly against the whole library - a
    // self-contained scan, not a mirror of MainViewModel's Rows/FilterText
    // pipeline. Songs reuses TrackListBuilder.Build, the same filter, sort and
    // rows Main.Rows is built from, pointed at the whole library. Cleared once
    // the query is blank. The scan runs off the UI thread, since a broad
    // one-character query can match a large part of the library.
    private async Task RebuildAsync(CancellationToken token)
    {
        var text = _query;
        if (string.IsNullOrWhiteSpace(text))
        {
            AlbumResults.Clear();
            ArtistResults.Clear();
            SongResults.Clear();
            HasMoreResults = false;
            RaiseResultsChanged();
            return;
        }

        var tracks = _main.Library.Tracks;
        var playing = _main.CurrentlyPlayingTrack;
        var pairedServerFingerprint = _main.PairedServerFingerprint;
        var pairedServerReachable = _main.IsPairedServerReachable;
        var (albums, albumsTotal, artists, artistsTotal, songs, songsTotal) = await Task.Run(() =>
        {
            // SearchText, not a plain Contains, so these two agree with the
            // song rows below - those go through TrackListBuilder, which folds
            // accents. Searching "bjork" must not return the songs and then an
            // empty artist strip.
            var matchingAlbumTracks = tracks.Where(t => SearchText.Contains(t.Album, text));
            var allAlbums = AlbumGridBuilder.Build(matchingAlbumTracks);
            TrackAvailability.Apply(allAlbums, pairedServerFingerprint, pairedServerReachable);

            // Same raw per-track field the Artists tab's own picker groups by
            // (see MainViewModel.RebuildSubListItems) - not EffectiveAlbumArtist,
            // which AlbumGridBuilder uses for a different purpose (labeling a
            // same-named album spanning several artists as "Various Artists").
            var allArtists = ArtistNames.Of(tracks.Where(t => SearchText.Contains(t.Artists, text)));

            // Counted and covered the way the Artists tab's rows are, for the
            // shown ones only: one pass over the library, not one per artist.
            var shownArtists = allArtists.Take(MaxResultsPerSection).Select(a => a!).ToList();
            var shownSet = shownArtists.ToHashSet(ArtistNames.Comparer);
            var byArtist = tracks
                .Where(t => t.Artists != null && shownSet.Contains(t.Artists))
                .GroupBy(t => t.Artists!, ArtistNames.Comparer)
                .ToDictionary(g => g.Key, g => g.ToList(), ArtistNames.Comparer);
            var artistRows = shownArtists
                .Select(name => byArtist.TryGetValue(name, out var mine)
                    ? new ArtistPickerRow(name,
                        mine.Where(t => !string.IsNullOrEmpty(t.Album)).Select(t => t.Album).Distinct().Count(),
                        mine.Count,
                        AlbumCollage.ByAlbumName(mine))
                    : new ArtistPickerRow(name, 0, 0, AlbumCollage.Empty))
                .ToList();

            // Availability passed straight in, so these rows are right from
            // construction, as Main.Rows is (MainViewModel.RebuildRowsAsync).
            var allSongRows = TrackListBuilder.Build(tracks, text, "Title", true, playing,
                pairedServerFingerprint: pairedServerFingerprint, pairedServerReachable: pairedServerReachable);

            return (allAlbums.Take(MaxResultsPerSection).ToList(), allAlbums.Count,
                    artistRows, allArtists.Count,
                    allSongRows.Take(MaxResultsPerSection).ToList(), allSongRows.Count);
        }, token);

        if (token.IsCancellationRequested)
            return;

        AlbumResults.Clear();
        foreach (var album in albums)
            AlbumResults.Add(album);

        ArtistResults.Clear();
        foreach (var artist in artists)
            ArtistResults.Add(artist);

        SongResults.Clear();
        foreach (var row in songs)
            SongResults.Add(row);

        HasMoreResults =
            songsTotal > MaxResultsPerSection ||
            albumsTotal > MaxResultsPerSection ||
            artistsTotal > MaxResultsPerSection;
        RaiseResultsChanged();
    }

    // The server came or went: the rows and tiles built while it was the
    // other way say so again.
    public void ApplyAvailability(string? pairedServerFingerprint, bool pairedServerReachable)
    {
        TrackAvailability.Apply(SongResults, pairedServerFingerprint, pairedServerReachable);
        TrackAvailability.Apply(AlbumResults, pairedServerFingerprint, pairedServerReachable);
    }

    private void RaiseResultsChanged()
    {
        OnPropertyChanged(nameof(HasAlbumResults));
        OnPropertyChanged(nameof(HasArtistResults));
        OnPropertyChanged(nameof(HasSongResults));
        OnPropertyChanged(nameof(HasAnyResults));
        OnPropertyChanged(nameof(ShowsNoResults));
        ResultsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── Suggestions ──────────────────────────────────────────────────────
    //
    // The dropdown under the box: what the words typed so far could be
    // finishing - past searches, then artists, albums and songs in the
    // library (SearchSuggestion.For). Nothing while nothing is typed: opening
    // Search used to drop every past search over the screen before a key was
    // pressed. See ScreenSlot, which shows it only while the box has focus,
    // and the Search screen's menu for clearing the past ones. A search is
    // remembered when the box is left with something in it (Remember): a
    // result tapped, the keyboard put away, the tab changed. Not on each
    // keystroke, or every prefix of a word would be kept as a search of its
    // own.

    private IReadOnlyList<SearchSuggestion> _suggestions = [];

    public IReadOnlyList<SearchSuggestion> Suggestions
    {
        get => _suggestions;
        private set
        {
            _suggestions = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSuggestions));
            OnPropertyChanged(nameof(IsDropdownOpen));
        }
    }

    public bool HasSuggestions => _suggestions.Count > 0;

    // Whether the box has focus - view state, set by ScreenSlot, and here only
    // for MobileMainView: the search prompt there is drawn over every screen,
    // the dropdown included, so it has to step aside while that is open.
    private bool _isBoxFocused;

    public bool IsBoxFocused
    {
        get => _isBoxFocused;
        set
        {
            if (!SetProperty(ref _isBoxFocused, value))
                return;
            OnPropertyChanged(nameof(IsDropdownOpen));
        }
    }

    public bool IsDropdownOpen => _isBoxFocused && HasSuggestions;

    public ICommand ApplySuggestionCommand { get; }

    // Every past search - it is the history being cleared, not the query.
    public ICommand ClearRecentSearchesCommand { get; }

    // The suggestions follow on their own, off Main's RecentSearches change
    // (RefreshSuggestions, from the shell).
    public void Remember() => _main.RememberSearch(_query);

    // Shorter than the results' 250ms: the dropdown is what is being read
    // while typing, and one scan is far cheaper than rebuilding three
    // sections of tiles and rows.
    private static readonly TimeSpan SuggestionDelay = TimeSpan.FromMilliseconds(150);

    private CancellationTokenSource? _suggesting;

    // The past searches changed - one remembered or the history cleared. Not
    // once per keystroke, so the scan starts at once rather than debounced.
    public void RefreshSuggestions()
    {
        RecentSearches = [.. _main.RecentSearches];
        ScheduleSuggestions(TimeSpan.Zero);
    }

    // SearchSuggestion.For walks every track in the library, and it used to
    // do that on the UI thread on every keystroke. So it waits for typing to
    // pause, the same cancel-and-restart shape as ScheduleRebuild, and runs
    // on the pool. An empty box is answered at once, with nothing, so the
    // dropdown closes the moment the box is cleared rather than after a scan.
    private void ScheduleSuggestions(TimeSpan delay)
    {
        _suggesting?.Cancel();
        _suggesting = null;
        if (string.IsNullOrWhiteSpace(_query))
        {
            Suggestions = [];
            return;
        }

        _suggesting = new CancellationTokenSource();
        // A copy: the setting's own list changes in place on this thread.
        _ = SuggestAsync(_query, [.. _main.RecentSearches], _main.Library.Tracks, delay, _suggesting.Token);
    }

    private async Task SuggestAsync(string query, IReadOnlyList<string> recentSearches, IReadOnlyList<Track> tracks,
        TimeSpan delay, CancellationToken token)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, token);
            var suggestions = await Task.Run(
                () => SearchSuggestion.For(query, recentSearches, tracks, MaxSuggestions), token);
            if (!token.IsCancellationRequested)
                Suggestions = suggestions;
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke, or the box emptied - that call answers instead.
        }
    }

    // The past searches themselves, newest first - the desktop page lists
    // them while nothing is typed. A copy each time, since the setting's own
    // list changes in place and a binding to it would never hear of it.
    private IReadOnlyList<string> _recentSearches = [];

    public IReadOnlyList<string> RecentSearches
    {
        get => _recentSearches;
        private set
        {
            _recentSearches = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasRecentSearches));
        }
    }

    public bool HasRecentSearches => _recentSearches.Count > 0;
}
