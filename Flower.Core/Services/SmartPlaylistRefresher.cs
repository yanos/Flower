using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Models;

namespace Flower.Services;

// Keeps every smart playlist's materialized contents in step with the library
// it is a query over. Phase 3 of docs/SMART-PLAYLIST-PLAN.md: the engine
// (SmartPlaylistEvaluator) and the storage (Playlist.Rules) already exist; this
// is what decides when to run one against the other.
//
// One debounced pass over all of them, not a subscription per playlist. That is
// what makes the dependency order between membership rules a property of a
// single function (SmartPlaylistEvaluator.EvaluateAll) rather than an emergent
// one that happens to be right depending on which event arrived first.
//
// A pass is deliberately invisible to sync. Playlist.Materialize does not bump
// UpdatedAt and the write goes through Library.SavePlaylists rather than
// PlaylistsChanged, so PlaylistSyncPlanner - which decides "did this side
// change?" from UpdatedAt alone - finds no fingerprint. Devices exchange rules
// and let each end bake from the same recipe; see the plan's "Both ends bake
// from the same recipe".
public sealed class SmartPlaylistRefresher : IDisposable
{
    // Not const so tests can shorten it, the same arrangement
    // PeerSyncCoordinator.ContentSyncCooldown uses and for the same reason.
    //
    // Half a second because the triggers arrive in bursts, not singly:
    // TrackStatsChanged fires twice per track change, and a sync merge or a
    // star across an album raises its event once per track touched. Long enough
    // to collapse a burst, short enough that a song finishing has updated
    // "Recently Played" before anyone looks.
    internal static TimeSpan Cooldown = TimeSpan.FromMilliseconds(500);

    private readonly Library _library;
    private readonly ILogger<SmartPlaylistRefresher> _logger;
    private readonly TimeProvider _clock;

    // Serializes passes against each other. Triggers arrive on the UI thread,
    // a rescan's Task.Run, a LibVLC callback and (on the server) a Kestrel
    // request thread, and a pass reads Library.Playlists and mutates the
    // Playlist objects in it.
    private readonly object _gate = new();

    private CancellationTokenSource? _pending;
    private bool _started;
    private bool _disposed;

    public SmartPlaylistRefresher(
        Library library,
        ILogger<SmartPlaylistRefresher> logger,
        TimeProvider? clock = null)
    {
        _library = library;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    // Raised after a pass that actually moved something, carrying only the
    // playlists whose contents changed. Nothing here marshals to the UI thread:
    // a pass runs on whichever thread the debounce timer completed on, and the
    // subscriber that cares about rendering is the one that knows how to get
    // back to the dispatcher.
    public event EventHandler<SmartPlaylistsRefreshedEventArgs>? Refreshed;

    // Every door a smart-playlist input can come through. TracksUpdated alone
    // is not enough and it is the trap this design is easiest to get wrong at:
    // play count, LastPlayedAt and skip count deliberately do *not* raise it
    // (they were split onto TrackStatsChanged because a play used to mean a
    // full track-list rebuild plus a peer sync, twice per track change - see
    // ARCHITECTURE-REVIEW.md Tier 1.1), and those three are exactly what the
    // flagship playlists are built on. Hang this off TracksUpdated only and
    // "Recently Played" never updates until the next rescan.
    //
    //   TracksUpdated     - rescan, download, import, a tag edit, and the two
    //                       merge paths that land a pulled catalog.
    //   TrackStatsChanged - a play, here or reported in by a paired device.
    //   TrackStarsChanged - a star, from Track Info or over Subsonic.
    //   PlaylistsChanged  - a membership rule makes another playlist's contents
    //                       an input, so a playlist edit is a track-set change
    //                       for everything that references it. (The refresher's
    //                       own writes do not come back through here - see
    //                       Library.SavePlaylists.)
    //
    // The fifth trigger, a rule edit, is the editor calling Schedule directly.
    public void Start()
    {
        if (_started)
            return;

        _started = true;

        _library.TracksUpdated += OnLibraryChanged;
        _library.TrackStatsChanged += OnStatsChanged;
        _library.TrackStarsChanged += OnLibraryChanged;
        _library.PlaylistsChanged += OnLibraryChanged;

        // Straight away rather than debounced: at startup the stored contents
        // are as old as the last session, and there is no burst to collapse.
        Refresh();
    }

    private void OnLibraryChanged(object? sender, EventArgs e) => Schedule();

    private void OnStatsChanged(object? sender, TrackStatsChangedEventArgs e) => Schedule();

    // Restarts the cooldown rather than queuing another pass, so a burst of
    // triggers costs one recomputation and it is the one that sees all of them.
    public void Schedule()
    {
        if (_disposed)
            return;

        CancellationTokenSource started;
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = started = new CancellationTokenSource();
        }

        _ = RunAfterCooldownAsync(started.Token);
    }

    private async Task RunAfterCooldownAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(Cooldown, _clock, token);
        }
        catch (OperationCanceledException)
        {
            return; // A newer trigger restarted the cooldown; its own delay will fire instead.
        }

        Refresh();
    }

    // One pass, now. Returns the playlists whose contents actually moved -
    // empty when nothing did, which is the common case and the reason
    // Materialize reports whether it changed anything: a pass that found
    // nothing must not write the playlist table back out.
    public IReadOnlyList<Playlist> Refresh()
    {
        lock (_gate)
        {
            List<Playlist> changed;
            try
            {
                changed = Evaluate();
            }
            catch (SmartPlaylistCycleException ex)
            {
                // The editor is what keeps a cycle out of the database
                // (SmartPlaylistGraph.ReferenceCandidates), so reaching here
                // means a hand-edited database or a rules blob from a peer.
                // Skipping the pass leaves every smart playlist holding its
                // last good contents, which beats emptying them all over a
                // loop between two of them.
                _logger.LogError(ex, "Smart playlists were not recomputed: their membership rules form a loop");
                return [];
            }
            catch (SmartPlaylistRuleException ex)
            {
                _logger.LogError(ex, "Smart playlists were not recomputed: a rule could not be evaluated");
                return [];
            }

            if (changed.Count == 0)
                return [];

            // The write, and deliberately not through PlaylistsChanged - see
            // Library.SavePlaylists for why a recomputation must not announce
            // itself as a playlist change.
            _library.SavePlaylists();

            _logger.LogInformation(
                "Recomputed smart playlists; {Count} changed: {Names}",
                changed.Count, string.Join(", ", changed.Select(p => p.Name)));

            Refreshed?.Invoke(this, new SmartPlaylistsRefreshedEventArgs(changed));
            return changed;
        }
    }

    private List<Playlist> Evaluate()
    {
        // One snapshot of the set for the whole pass: Playlists is swapped
        // wholesale rather than mutated in place (see Library.SwapPlaylists),
        // so a sync landing mid-pass cannot change what this is iterating.
        var playlists = _library.Playlists;

        var rules = new Dictionary<Guid, SmartPlaylistRules>();
        foreach (var playlist in playlists)
        {
            // LiveUpdating = false is "evaluate once when saved, then freeze",
            // as in iTunes. Such a playlist is left out of the pass entirely
            // but stays perfectly referenceable by a membership rule - it
            // answers from its stored contents through Membership below, the
            // same way an ordinary playlist does. The editor recomputes it once
            // on save via RefreshOne.
            if (playlist.Rules is { LiveUpdating: true } live)
                rules[playlist.Id] = live;
        }

        if (rules.Count == 0)
            return [];

        var byId = playlists.ToDictionary(p => p.Id);

        var results = SmartPlaylistEvaluator.EvaluateAll(
            rules, _library.Tracks, id => Membership(byId, id), _clock.GetUtcNow());

        var changed = new List<Playlist>();
        foreach (var (id, tracks) in results)
        {
            if (byId.TryGetValue(id, out var playlist) && playlist.Materialize(tracks))
                changed.Add(playlist);
        }

        return changed;
    }

    // Recomputes one playlist against the current library, regardless of its
    // LiveUpdating flag - what the editor calls when rules are saved, so a
    // frozen playlist is still filled in once at the moment it is defined.
    //
    // Other smart playlists it references answer from their stored contents
    // rather than being recomputed first: this is a single edit being applied,
    // not the ordered whole-set pass, and Schedule covers the rest.
    public bool RefreshOne(Playlist playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);

        if (playlist.Rules is not { } rules)
            return false;

        lock (_gate)
        {
            var byId = _library.Playlists.ToDictionary(p => p.Id);
            var context = new SmartPlaylistContext(_clock.GetUtcNow(), id => Membership(byId, id));

            if (!playlist.Materialize(SmartPlaylistEvaluator.Evaluate(rules, _library.Tracks, context)))
                return false;

            _library.SavePlaylists();
            Refreshed?.Invoke(this, new SmartPlaylistsRefreshedEventArgs([playlist]));
            return true;
        }
    }

    // What a membership rule sees when it names a playlist that is not part of
    // this pass: an ordinary one, a frozen smart one, or - by returning null -
    // one this device does not have at all, which resolves to empty rather than
    // failing the rule. Same tolerance playlist_tracks already applies to a
    // track id that no longer resolves.
    private static IReadOnlySet<Guid>? Membership(Dictionary<Guid, Playlist> byId, Guid id) =>
        byId.TryGetValue(id, out var playlist) ? playlist.Tracks.Select(t => t.Id).ToHashSet() : null;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_started)
        {
            _library.TracksUpdated -= OnLibraryChanged;
            _library.TrackStatsChanged -= OnStatsChanged;
            _library.TrackStarsChanged -= OnLibraryChanged;
            _library.PlaylistsChanged -= OnLibraryChanged;
        }

        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }
}

public sealed class SmartPlaylistsRefreshedEventArgs(IReadOnlyList<Playlist> changed) : EventArgs
{
    // Only the playlists whose materialized contents actually moved, so a
    // subscriber refreshing the view can skip the ones that did not.
    public IReadOnlyList<Playlist> Changed { get; } = changed;
}
