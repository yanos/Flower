using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Logging;

using Flower.Models;

namespace Flower.Services;

// One version of one playlist on a server: the revision it was given, whether
// it is a deletion, and which content it is (PlaylistVersion), null for a
// deletion.
public sealed record PlaylistRevision(long Revision, bool Deleted, string? Version);

// Where a server keeps its playlists' revisions, per listener and playlist,
// each list oldest first. In flower.db (PlaylistRevisionRepository), so a
// backup carries the revisions as they stood when it was taken.
public interface IPlaylistRevisionStore
{
    Dictionary<(string Listener, Guid Id), List<PlaylistRevision>> Load();

    // Appends these, then keeps only the newest `keep` of each playlist's.
    void Append(IReadOnlyList<(string Listener, Guid Id, PlaylistRevision Revision)> issued, int keep);
}

// The server's versions of its playlists (docs/PLAYLIST-SYNC-PLAN.md,
// "Revisions"). The server is the only thing that issues one, so a device's
// clock never decides what a sync does, and a write based on a version the
// server has moved on from is refused rather than taken.
//
// A revision is issued whenever a playlist's content changes, however it
// changed: this listens to Library.PlaylistsChanged and compares each
// playlist with the version it last issued, rather than trusting each writer
// to remember. ApplyChanges is the only writer today, but an edit that
// reached the library without a revision would be overwritten by the next
// device to push over the old one. Started once the stored playlists are
// loaded, and reconciled then too, which also covers a crash between saving a
// playlist and recording its revision.
//
// revision = max(last issued + 1, the server's UTC milliseconds). The clock
// keeps revisions unique after a backup is restored, when a plain counter
// would hand out numbers devices already hold for other versions.
public sealed class PlaylistRevisions
{
    // How many of each playlist's revisions are kept: a device whose version
    // is older than all of them has no history in common with the server.
    public const int Kept = 64;

    private readonly object _gate = new();
    private readonly Library _library;
    private readonly IPlaylistRevisionStore _store;
    private readonly Func<long> _now;
    private Dictionary<(string Listener, Guid Id), List<PlaylistRevision>> _held = new();
    private long _lastIssued;
    private bool _started;

    // How many times Reconcile has run, so ApplyChanges can tell whether its
    // install already ran one.
    private long _reconciled;

    public PlaylistRevisions(Library library, IPlaylistRevisionStore store, Func<long>? now = null)
    {
        _library = library;
        _store = store;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        library.PlaylistsChanged += (_, _) =>
        {
            if (_started)
                Reconcile();
        };
    }

    // After the stored playlists are in the library, never before: started
    // against an empty one it would record every playlist as deleted.
    public void Start()
    {
        lock (_gate)
        {
            _held = _store.Load();
            _lastIssued = _held.Values.SelectMany(r => r).Select(r => r.Revision).DefaultIfEmpty(0).Max();
            _started = true;
            Reconcile();
        }
    }

    // Issues a revision for every playlist whose content is not the version
    // last issued for it, and a deletion for every one that has gone.
    public void Reconcile()
    {
        lock (_gate)
        {
            _reconciled++;
            var issued = new List<(string Listener, Guid Id, PlaylistRevision Revision)>();
            var present = new HashSet<(string, Guid)>();
            foreach (var playlist in _library.Playlists)
            {
                var key = (playlist.Listener, playlist.Id);
                present.Add(key);
                var version = PlaylistVersion.Of(playlist);
                if (Latest(key) is { Deleted: false } latest && latest.Version == version)
                    continue;

                issued.Add((key.Listener, key.Id, new PlaylistRevision(Issue(), false, version)));
            }

            foreach (var (key, revisions) in _held)
            {
                if (!present.Contains(key) && !revisions[^1].Deleted)
                    issued.Add((key.Listener, key.Id, new PlaylistRevision(Issue(), true, null)));
            }

            Keep(issued);
        }
    }

    // GET /playlists: one listener's playlists, each at its revision, and its
    // recorded deletions.
    public PlaylistSyncManifestDto ManifestFor(string listener, string fingerprint)
    {
        lock (_gate)
        {
            var playlists = PlaylistSyncMapper.For(_library.Playlists, listener);
            var held = playlists.Select(p => p.Id).ToHashSet();
            var deleted = _held
                .Where(e => e.Key.Listener == listener && e.Value[^1].Deleted && !held.Contains(e.Key.Id))
                .Select(e => DeletionOf(e.Key.Id, e.Value))
                .ToList();
            return new PlaylistSyncManifestDto(
                fingerprint, playlists.Select(p => Served(p)).ToList(), deleted.Count > 0 ? deleted : null);
        }
    }

    // POST /playlists/changes. Each change is applied only if the server's
    // version of that playlist is still the one it is based on; otherwise it
    // is answered Stale with what the server holds now, for the device to
    // merge with. One lock for the whole request, so two devices' writes to
    // one playlist cannot both pass the check.
    //
    // A push only ever speaks for one listener (see Listeners): an id that is
    // another listener's is refused, whatever its base, and recorded as
    // deleted for this listener over that base, so the pushing device's next
    // session drops its copy the ordinary way. The device deletes nothing on
    // the answer alone, which is unsigned.
    public PlaylistChangesResponseDto ApplyChanges(PlaylistChangesRequestDto request, string listener, ILogger? logger = null)
    {
        lock (_gate)
        {
            var current = _library.Playlists.ToDictionary(p => p.Id);
            var answers = new Dictionary<Guid, PlaylistChangeAnswerDto>();
            var accepted = new List<PlaylistChangeDto>();
            var refused = new List<(Guid Id, long Base)>();
            var seen = new HashSet<Guid>();

            foreach (var change in request.Changes)
            {
                if (!seen.Add(change.Id))
                    continue;
                if (!change.Delete && change.Playlist?.Id != change.Id)
                    continue;

                if (current.TryGetValue(change.Id, out var held) && held.Listener != listener)
                {
                    refused.Add((change.Id, change.BaseRevision));
                    answers[change.Id] = new PlaylistChangeAnswerDto(change.Id, PlaylistChangeOutcome.Refused);
                    continue;
                }

                var (revision, copy, deletion) = StateOf(listener, change.Id, held);
                if (change.BaseRevision != revision)
                {
                    answers[change.Id] = new PlaylistChangeAnswerDto(
                        change.Id, PlaylistChangeOutcome.Stale, revision, copy, deletion);
                    continue;
                }

                accepted.Add(change);
            }

            RecordRefusals(listener, refused);

            if (accepted.Count > 0)
            {
                var index = new PlaylistTrackIndex(_library.Tracks);
                var reconciled = _reconciled;
                var deleting = accepted.Where(c => c.Delete).Select(c => c.Id).ToHashSet();
                var writing = accepted.Where(c => !c.Delete).ToDictionary(c => c.Id, c => c.Playlist!);
                _library.ReplacePlaylists(now =>
                {
                    var result = new List<Playlist>();
                    foreach (var playlist in now)
                    {
                        if (deleting.Contains(playlist.Id))
                            continue;
                        result.Add(writing.Remove(playlist.Id, out var dto)
                            ? PlaylistSyncMapper.ToPlaylist(dto, index, logger, listener)
                            : playlist);
                    }
                    result.AddRange(writing.Values.Select(dto => PlaylistSyncMapper.ToPlaylist(dto, index, logger, listener)));
                    return result;
                });

                // Already run by PlaylistsChanged, unless the install changed
                // nothing; and a deletion of a playlist the server does not
                // hold is still recorded, so the devices that have it drop it.
                if (_reconciled == reconciled)
                    Reconcile();
                RecordDeletionsOfMissing(listener, deleting);
            }

            var installed = _library.Playlists.ToDictionary(p => p.Id);
            foreach (var change in accepted)
            {
                var (revision, _, _) = StateOf(listener, change.Id, installed.GetValueOrDefault(change.Id));
                answers[change.Id] = new PlaylistChangeAnswerDto(change.Id, PlaylistChangeOutcome.Applied, revision);
            }

            if (refused.Count > 0)
            {
                logger?.LogWarning(
                    "Refused {Count} playlist change(s) for listener {Listener}: they belong to another listener ({PlaylistIds})",
                    refused.Count, listener, string.Join(", ", refused.Select(r => r.Id)));
            }

            return new PlaylistChangesResponseDto(
                request.Changes.Select(c => c.Id).Distinct().Where(answers.ContainsKey).Select(id => answers[id]).ToList());
        }
    }

    // The server's version of a playlist the way GET /playlists shows it: the
    // playlist at its revision, its recorded deletion, or nothing at revision
    // 0. A revision recorded for a playlist the library does not hold, and
    // never deleted, is nothing - what a device sees is what it writes over.
    private (long Revision, PlaylistSyncPlaylistDto? Copy, PlaylistDeletionDto? Deletion) StateOf(
        string listener, Guid id, Playlist? held)
    {
        if (held != null)
        {
            var served = Served(held);
            return (served.Revision, served, null);
        }

        if (_held.TryGetValue((listener, id), out var revisions) && revisions[^1].Deleted)
        {
            var deletion = DeletionOf(id, revisions);
            return (deletion.Revision, null, deletion);
        }

        return (0, null, null);
    }

    private PlaylistSyncPlaylistDto Served(Playlist playlist)
    {
        var dto = PlaylistSyncMapper.ToDto(playlist);
        if (!_held.TryGetValue((playlist.Listener, playlist.Id), out var revisions) || revisions[^1].Deleted)
            return dto;

        return dto with { Revision = revisions[^1].Revision, History = HistoryOf(revisions) };
    }

    private static PlaylistDeletionDto DeletionOf(Guid id, List<PlaylistRevision> revisions) =>
        new(id, revisions[^1].Revision, HistoryOf(revisions));

    private static List<long>? HistoryOf(List<PlaylistRevision> revisions) =>
        revisions.Count > 1 ? revisions.Take(revisions.Count - 1).Select(r => r.Revision).ToList() : null;

    // The base a refused change claimed goes into the history, so the device
    // that sent it reads the deletion as one made over its own version.
    private void RecordRefusals(string listener, List<(Guid Id, long Base)> refused)
    {
        var issued = new List<(string Listener, Guid Id, PlaylistRevision Revision)>();
        foreach (var (id, baseRevision) in refused)
        {
            var revisions = _held.GetValueOrDefault((listener, id));
            if (revisions is [.., { Deleted: true }] && revisions.Any(r => r.Revision == baseRevision))
                continue;

            if (revisions == null || revisions.All(r => r.Revision != baseRevision))
                issued.Add((listener, id, new PlaylistRevision(baseRevision, false, null)));
            issued.Add((listener, id, new PlaylistRevision(Issue(), true, null)));
        }

        Keep(issued);
    }

    private void RecordDeletionsOfMissing(string listener, IEnumerable<Guid> ids)
    {
        var issued = new List<(string Listener, Guid Id, PlaylistRevision Revision)>();
        foreach (var id in ids)
        {
            if (_held.GetValueOrDefault((listener, id)) is not [.., { Deleted: true }])
                issued.Add((listener, id, new PlaylistRevision(Issue(), true, null)));
        }

        Keep(issued);
    }

    private PlaylistRevision? Latest((string, Guid) key) =>
        _held.TryGetValue(key, out var revisions) ? revisions[^1] : null;

    private long Issue() => _lastIssued = Math.Max(_lastIssued + 1, _now());

    // Stored first, then held: a write that fails leaves this process believing
    // nothing it did not keep.
    private void Keep(List<(string Listener, Guid Id, PlaylistRevision Revision)> issued)
    {
        if (issued.Count == 0)
            return;

        _store.Append(issued, Kept);
        foreach (var (listener, id, revision) in issued)
        {
            if (!_held.TryGetValue((listener, id), out var revisions))
                _held[(listener, id)] = revisions = [];
            revisions.Add(revision);
            revisions.Sort((a, b) => a.Revision.CompareTo(b.Revision));
            if (revisions.Count > Kept)
                revisions.RemoveRange(0, revisions.Count - Kept);
        }
    }
}

// Which content a version of a playlist is: what a sync carries, and nothing
// it does not. Not UpdatedAt, which is for display, and not a smart or Vibe
// playlist's songs, which each device works out (or takes from the server)
// for itself. Rows by EntryId, which never changes for a row and is never
// reused, so a song retagged on the server is not a new version of every
// playlist holding it.
//
// The same comparison on both ends: the server issues a revision when it
// changes, and a device has changed a playlist when it differs from its agreed
// copy - so an edit and its undo are no change at all (UF-22).
public static class PlaylistVersion
{
    public static string Of(PlaylistSyncPlaylistDto playlist)
    {
        var text = new StringBuilder();
        Field(text, playlist.Name);
        Field(text, playlist.IsFolder ? "folder" : "");
        Field(text, playlist.ParentId?.ToString("N"));
        Field(text, playlist.SortKey?.ToString("R", CultureInfo.InvariantCulture));
        Field(text, playlist.Rules is { } rules ? SmartPlaylistRulesJson.Write(rules) : null);
        Field(text, playlist.VibePrompt);
        Field(text, playlist.VibeInstrumental ? "instrumental" : "");
        if (playlist.Rules == null && playlist.VibePrompt == null && !playlist.IsFolder)
        {
            foreach (var row in playlist.Tracks)
                Field(text, row.EntryId?.ToString("N") ?? row.SongKey);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }

    public static string Of(Playlist playlist) => Of(PlaylistSyncMapper.ToDto(playlist));

    public static bool Same(PlaylistSyncPlaylistDto a, PlaylistSyncPlaylistDto b) => Of(a) == Of(b);

    // Length-prefixed, so no value can run into the next.
    private static void Field(StringBuilder text, string? value) =>
        text.Append(value?.Length ?? -1).Append(':').Append(value).Append(';');
}
