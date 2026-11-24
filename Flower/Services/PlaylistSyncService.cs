using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Models;
using Flower.Persistence;
using Flower.Persistence.Sql;

namespace Flower.Services;

// Later is first so that it is what a dialog closed without a choice answers.
// It and NotAsked both leave the conflict open, with neither version pushed or
// marked as agreed: Later is the person putting it off, so it is not raised
// again until one of the two versions changes or the app restarts; NotAsked is
// that nobody could be asked just then, so the next session asks.
public enum PlaylistConflictChoice { Later, KeepLocal, KeepRemote, NotAsked }

// Raised when the server refuses a gated request as device-unknown (see
// FlowerProblem) - i.e. it has no key for this device, as opposed to being
// merely unreachable or refusing one request's signature. Shared by PlaylistSyncService and LibrarySyncService, both of
// which hit the same trust gate (see Flower.Server's SyncEndpoints) as the first
// request of their own sync session. MainViewModel uses this to notice a paired
// server has revoked (or
// never granted) trust and clear the stale local PairedServerFingerprint,
// rather than leaving the UI claiming "paired" indefinitely - see
// MainViewModel's own subscription for why that drift is otherwise invisible.
public sealed class PeerTrustRejectedEventArgs : EventArgs
{
    public required string Fingerprint { get; init; }
    public required string Alias { get; init; }
}

// Raised after a sync session, for each playlist only a person can decide: changed
// on both this device and the server since they last agreed, or deleted on the
// server while this device cannot tell whether its copy holds an edit (Remote
// null). The UI asks and reports back through Resolution;
// KeepRemote on a deletion is "delete it here too". The session itself never
// waits on the answer (UF-19): it is applied afterwards as an ordinary edit on
// top of the server's version, which the session that follows pushes.
public sealed class PlaylistConflictEventArgs : EventArgs
{
    public required Playlist Local { get; init; }
    public PlaylistSyncPlaylistDto? Remote { get; init; }
    public required string RemoteAlias { get; init; }
    public required TaskCompletionSource<PlaylistConflictChoice> Resolution { get; init; }

    public bool DeletedOnServer => Remote == null;
}

// Orchestrates a playlist sync session with the paired server (see
// docs/PLAYLIST-SYNC-PLAN.md). The I/O shell around PlaylistSyncPlanner, which
// makes the decisions and is unit tested on its own.
//
// A session reads the server's playlists, plans against the copies this device
// last agreed on, installs what it takes under the library's lock, and sends
// its own changes in one POST /playlists/changes, each over the server
// revision it is based on. A change the server answers Stale is planned again
// against what the server holds now, at most three times. Nothing waits on a
// person in between: what only a person can decide is asked once the session
// is over, and the answer becomes an edit the next session sends.
public class PlaylistSyncService
{
    // Pinned, for the same reason LibrarySyncService's is - see its remarks.
    private static readonly HttpClient Http = PeerHttpClient.Create(TimeSpan.FromSeconds(10));

    // How many times a session plans again after a Stale answer, and how many
    // rounds of questions one sync asks before leaving the rest to the next.
    private const int StaleRounds = 3;
    private const int AskRounds = 3;

    private readonly Library _library;
    private readonly IPeerCredentials _credentials;
    private readonly ILogger _logger;
    private readonly PlaylistAgreementRepository _agreements;
    private readonly DeviceNicknameStore _deviceNicknameStore;

    public event EventHandler<PlaylistConflictEventArgs>? ConflictDetected;
    public event EventHandler<PeerTrustRejectedEventArgs>? PeerTrustRejected;

    public PlaylistSyncService(
        Library library,
        DeviceIdentity deviceIdentity,
        DeviceSigningKey signingKey,
        AppSettings appSettings,
        PlaylistAgreementRepository agreements,
        DeviceNicknameStore deviceNicknameStore,
        ILogger<PlaylistSyncService> logger)
    {
        _library = library;
        // See LibrarySyncService's own note: built here from the three the
        // container already supplies, rather than injected separately.
        _credentials = new SignedDeviceCredentials(deviceIdentity, signingKey);
        _agreements = agreements;
        _deviceNicknameStore = deviceNicknameStore;
        _logger = logger;
    }

    // Always this device's paired server: the server never calls this, so a
    // device is always the side that starts a session.
    // Virtual for the same reason as LibrarySyncService.SyncWithAsync - see
    // its comment, and docs/ARCHITECTURE-REVIEW.md Tier 5.6.
    public virtual async Task SyncWithAsync(DiscoveredDevice device)
    {
        if (string.IsNullOrEmpty(device.Fingerprint))
        {
            _logger.LogTrace("Playlist sync skipped for {Alias}: no resolved fingerprint yet", device.Alias);
            return;
        }

        // A local nickname (see DeviceNicknameStore) wins over the peer's own
        // self-reported alias, so the conflict prompt's "Keep X's Version"
        // matches what this device is called elsewhere in the UI.
        var remoteDisplayName = _deviceNicknameStore.Get(device.Fingerprint) ?? device.Alias;

        var questions = await InSessionAsync(() => SyncOnceAsync(device));

        // Asked with the session over, so a prompt left open holds nothing
        // anyone else is waiting to write. An answer that settles something
        // is applied, and a session sends it; that session may find more to
        // ask about, within reason.
        for (var round = 0; round < AskRounds && questions.Count > 0; round++)
        {
            var answers = await AskAsync(questions, remoteDisplayName);
            var settles = answers.Any(a => a.Choice is PlaylistConflictChoice.KeepLocal or PlaylistConflictChoice.KeepRemote);
            questions = await InSessionAsync(() =>
            {
                ApplyAnswers(device, answers);
                return settles ? SyncOnceAsync(device) : Task.FromResult<List<Question>>([]);
            });
        }
    }

    // One session at a time. Several triggers can fire together - first
    // contact, a playlists-token change, the pass after a catalog pull
    // brought new songs in - and two sessions interleaved would each plan
    // against a library the other is about to replace.
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    private async Task<List<Question>> InSessionAsync(Func<Task<List<Question>>> session)
    {
        await _sessionGate.WaitAsync();
        try
        {
            return await session();
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    // A playlist only a person can decide, as the session found it: the
    // version of this device's copy it asked about, so an answer given after
    // that copy changed again is not applied to the new one.
    private sealed record Question(
        Playlist Local, string LocalVersion, PlaylistSyncPlaylistDto? Remote, PlaylistDeletionDto? Deletion)
    {
        public long RemoteRevision => Remote?.Revision ?? Deletion!.Revision;
    }

    private sealed record Answer(Question Question, PlaylistConflictChoice Choice);

    private async Task<List<Question>> SyncOnceAsync(DiscoveredDevice device)
    {
        _logger.LogInformation("Playlist sync starting with {Alias} ({Fingerprint}) at {EndPoint}",
            device.Alias, device.Fingerprint, device.BaseUri);

        PlaylistSyncManifestDto manifest;
        try
        {
            const string getPath = "/api/flower/v1/playlists";
            using var getRequest = new HttpRequestMessage(HttpMethod.Get, device.Url(getPath));
            await AddSignedIdentityHeadersAsync(getRequest, body: []);
            using var getResponse = await Http.SendAsync(getRequest);
            // A refusal throws with the server's reason, and with whether this
            // server's pinned key signed it - see below.
            await getResponse.EnsureSuccessAsync(device.PublicKey, device.Fingerprint);
            var json = await getResponse.Content.ReadAsStringAsync();
            manifest = JsonSerializer.Deserialize(json, PlaylistSyncJsonContext.Default.PlaylistSyncManifestDto)
                ?? new PlaylistSyncManifestDto(device.Fingerprint, []);
        }
        catch (Exception ex)
        {
            // Peer unreachable, not running this endpoint yet, or not (yet) trusted.
            _logger.LogWarning(ex, "Playlist sync with {Alias} ({Fingerprint}): GET /playlists failed, aborting this sync attempt",
                device.Alias, device.Fingerprint);

            // device-unknown specifically means the peer is up and answered, and
            // has no key for this device - and only when this server's pinned
            // key signed it. Every other failure (clock-skew, nonce-reused, a
            // bare 403 anything on the path could answer) just fails this
            // attempt and never unpairs. See FlowerProblem,
            // ServerResponseSignature and PeerTrustRejectedEventArgs.
            if (PeerResponses.IsDeviceUnknown(ex))
                PeerTrustRejected?.Invoke(this, new PeerTrustRejectedEventArgs { Fingerprint = device.Fingerprint, Alias = device.Alias });

            return [];
        }

        _logger.LogInformation("Playlist sync with {Alias}: fetched {RemoteCount} remote playlist(s), have {LocalCount} local",
            device.Alias, manifest.Playlists.Count, _library.Playlists.Count);

        var remote = manifest.Playlists.ToDictionary(p => p.Id);
        var deleted = (manifest.Deleted ?? []).ToDictionary(d => d.Id);
        var agreements = _agreements.Load(device.Fingerprint);
        var index = new PlaylistTrackIndex(_library.Tracks);
        var questions = new Dictionary<Guid, Question>();

        HashSet<Guid>? only = null;
        for (var round = 0; round < StaleRounds; round++)
        {
            var stale = await RoundAsync(device, remote, deleted, agreements, index, only, questions);
            if (stale.Count == 0)
                break;

            only = stale;
        }

        return questions.Values.ToList();
    }

    // Plans, installs and pushes once; returns the playlists the server
    // answered Stale for, with `remote` and `deleted` updated to what it
    // holds now.
    private async Task<HashSet<Guid>> RoundAsync(
        DiscoveredDevice device,
        Dictionary<Guid, PlaylistSyncPlaylistDto> remote,
        Dictionary<Guid, PlaylistDeletionDto> deleted,
        Dictionary<Guid, PlaylistAgreement> agreements,
        PlaylistTrackIndex index,
        HashSet<Guid>? only,
        Dictionary<Guid, Question> questions)
    {
        // Planned under the library's lock, against the playlists as they are
        // at that moment, and installed in the same breath: an edit made here
        // while the session read the server is simply this device's copy,
        // merged or asked about like any other, and none can land between the
        // plan and the install.
        IReadOnlyList<PlaylistSyncDecision> decisions = [];
        var agreed = new Dictionary<Guid, PlaylistAgreement?>();
        var tookRowIds = false;
        var refilled = false;
        var installed = _library.ReplacePlaylists(current =>
        {
            decisions = PlaylistSyncPlanner.Plan(current, remote.Values.ToList(), deleted.Values.ToList(), agreements, index, only);
            agreed.Clear();
            tookRowIds = false;
            refilled = false;
            var decided = new Dictionary<Guid, Playlist?>();
            foreach (var decision in decisions)
                decided[decision.PlaylistId] = Decide(device, decision, agreements, index, agreed, questions, ref tookRowIds, ref refilled);

            // In the library's own order, so the sidebar does not move; what
            // the server brought goes at the end.
            var next = new List<Playlist>();
            foreach (var playlist in current)
            {
                if (!decided.TryGetValue(playlist.Id, out var mine))
                    next.Add(playlist);
                else if (mine != null)
                    next.Add(mine);
            }
            var here = current.Select(p => p.Id).ToHashSet();
            next.AddRange(decided.Values.Where(p => p != null && !here.Contains(p.Id))!);
            return next;
        }).ToDictionary(p => p.Id);
        // Both are changes made in place, which the install cannot see.
        if (refilled)
            _library.NotePlaylistsRefilled();
        else if (tookRowIds)
            _library.NotePlaylistRowsRenamed();

        // After the install, never before - see PlaylistAgreementRepository.
        Save(device, agreements, agreed);

        var changes = new List<PlaylistChangeDto>();
        foreach (var decision in decisions)
        {
            if (decision.Kind is PlaylistSyncDecisionKind.Push or PlaylistSyncDecisionKind.Merge
                && installed.TryGetValue(decision.PlaylistId, out var mine))
            {
                changes.Add(new PlaylistChangeDto(decision.PlaylistId, decision.BaseRevision, PlaylistSyncMapper.ToDto(mine)));
            }
            else if (decision.Kind == PlaylistSyncDecisionKind.PushDelete && !installed.ContainsKey(decision.PlaylistId))
            {
                changes.Add(new PlaylistChangeDto(decision.PlaylistId, decision.BaseRevision, Delete: true));
            }
        }

        if (changes.Count == 0)
            return [];

        PlaylistChangesResponseDto? answer;
        try
        {
            answer = await PushAsync(device, changes);
        }
        catch (Exception ex)
        {
            // Unreachable, or not trusted yet. Nothing sent is agreed, so the
            // next session reads the same changes and sends them again.
            _logger.LogWarning(ex, "Playlist sync with {Alias}: POST /playlists/changes failed - our own merge is saved, but the server did not receive it this time",
                device.Alias);
            return [];
        }

        var sent = changes.ToDictionary(c => c.Id);
        var stale = new HashSet<Guid>();
        var settled = new Dictionary<Guid, PlaylistAgreement?>();
        foreach (var reply in answer?.Answers ?? [])
        {
            if (!sent.TryGetValue(reply.Id, out var change))
                continue;

            switch (reply.Outcome)
            {
                case PlaylistChangeOutcome.Applied:
                    settled[reply.Id] = change.Delete ? null : new PlaylistAgreement(reply.Revision, change.Playlist);
                    break;
                case PlaylistChangeOutcome.Refused:
                    // Agreed at the version it was sent over, which the server
                    // records it deleted from for this listener - so the next
                    // session drops the copy the ordinary way, and only on what
                    // GET says.
                    _logger.LogInformation(
                        "Playlist sync with {Alias}: the server refused playlist {PlaylistId}, which belongs to another listener on it; it will be dropped here on the next sync",
                        device.Alias, reply.Id);
                    settled[reply.Id] = change.Delete ? null : new PlaylistAgreement(change.BaseRevision, change.Playlist);
                    break;
                case PlaylistChangeOutcome.Stale:
                    _logger.LogInformation(
                        "Playlist sync with {Alias}: playlist {PlaylistId} changed on the server since this session read it - planning it again",
                        device.Alias, reply.Id);
                    remote.Remove(reply.Id);
                    deleted.Remove(reply.Id);
                    if (reply.Current is { } current)
                        remote[reply.Id] = current;
                    if (reply.CurrentDeletion is { } deletion)
                        deleted[reply.Id] = deletion;
                    stale.Add(reply.Id);
                    break;
            }
        }

        Save(device, agreements, settled);
        _logger.LogInformation("Playlist sync with {Alias}: sent {Count} change(s), {Stale} stale", device.Alias, changes.Count, stale.Count);
        return stale;
    }

    private void Save(DiscoveredDevice device, Dictionary<Guid, PlaylistAgreement> agreements, Dictionary<Guid, PlaylistAgreement?> changes)
    {
        if (changes.Count == 0)
            return;

        _agreements.Save(device.Fingerprint, changes);
        foreach (var (id, agreement) in changes)
        {
            if (agreement == null)
                agreements.Remove(id);
            else
                agreements[id] = agreement;
        }
    }

    private void LogDecision(DiscoveredDevice device, PlaylistSyncDecision decision, Dictionary<Guid, PlaylistAgreement> agreements) =>
        _logger.LogTrace(
            "Playlist sync with {Alias}: \"{Name}\" ({PlaylistId}) -> {Decision} (agreed {Agreed}, server {Server}, over {Base})",
            device.Alias, decision.Local?.Name ?? decision.Remote?.Name ?? "?", decision.PlaylistId, decision.Kind,
            agreements.GetValueOrDefault(decision.PlaylistId)?.Revision, decision.Remote?.Revision ?? decision.Deletion?.Revision,
            decision.BaseRevision);

    // What this device holds once one decision is carried out, or null for
    // nothing; records what it agrees on and what it must ask. Runs under the
    // library's lock (see RoundAsync), so it only computes: neither Materialize
    // nor TakeEntryIdsFrom raises anything.
    private Playlist? Decide(
        DiscoveredDevice device,
        PlaylistSyncDecision decision,
        Dictionary<Guid, PlaylistAgreement> agreements,
        PlaylistTrackIndex index,
        Dictionary<Guid, PlaylistAgreement?> agreed,
        Dictionary<Guid, Question> questions,
        ref bool tookRowIds,
        ref bool refilled)
    {
        LogDecision(device, decision, agreements);
        questions.Remove(decision.PlaylistId);

        switch (decision.Kind)
        {
            case PlaylistSyncDecisionKind.NoChange:
                // The same sentence on both sides: the server's songs are the
                // answer to it, installed without moving UpdatedAt, as a
                // smart playlist's own re-evaluation is. In place, so the
                // install sees no change: refilled has it saved and shown.
                if (decision.Local!.IsVibe)
                    refilled |= decision.Local.Materialize(index.Resolve(decision.Remote!.Tracks, out _));
                // Copies holding the same songs under different row ids become
                // one set of rows, this device taking the server's. No edit,
                // so nothing to push.
                var took = decision.Local.IsOrdinary
                    && decision.Local.TakeEntryIdsFrom(decision.Local.Entries, decision.Remote!.Tracks);
                tookRowIds |= took;
                // Already agreed at this revision, the usual case for a
                // playlist nobody touched, is left as stored rather than
                // rewritten every session.
                if (took || agreements.GetValueOrDefault(decision.PlaylistId)?.Revision != decision.Remote!.Revision)
                    agreed[decision.PlaylistId] = new PlaylistAgreement(decision.Remote!.Revision, decision.Remote);
                return decision.Local;

            case PlaylistSyncDecisionKind.AdoptRemote:
                agreed[decision.PlaylistId] = new PlaylistAgreement(decision.Remote!.Revision, decision.Remote);
                return PlaylistSyncMapper.ToPlaylist(decision.Remote, index, _logger);

            case PlaylistSyncDecisionKind.Push:
                return decision.Local;

            case PlaylistSyncDecisionKind.Merge:
                _logger.LogInformation(
                    "Playlist sync with {Alias}: \"{Name}\" was edited here and on the server, and the two edits combine",
                    device.Alias, decision.Merged!.Name);
                return decision.Merged;

            case PlaylistSyncDecisionKind.DeleteHere:
                _logger.LogInformation("Playlist sync with {Alias}: \"{Name}\" was deleted on the server, deleting it here",
                    device.Alias, decision.Local!.Name);
                agreed[decision.PlaylistId] = null;
                return null;

            case PlaylistSyncDecisionKind.Forget:
                agreed[decision.PlaylistId] = null;
                return null;

            case PlaylistSyncDecisionKind.Conflict:
            case PlaylistSyncDecisionKind.ConfirmDeletion:
                var question = new Question(
                    decision.Local!, PlaylistVersion.Of(decision.Local!), decision.Remote, decision.Deletion);
                if (_putOff.GetValueOrDefault(decision.PlaylistId) != (question.LocalVersion, question.RemoteRevision))
                    questions[decision.PlaylistId] = question;
                return decision.Local;

            default:
                return decision.Local;
        }
    }

    private async Task<PlaylistChangesResponseDto?> PushAsync(DiscoveredDevice device, List<PlaylistChangeDto> changes)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new PlaylistChangesRequestDto(changes), PlaylistSyncJsonContext.Default.PlaylistChangesRequestDto));
        const string postPath = "/api/flower/v1/playlists/changes";
        using var content = new ByteArrayContent(bodyBytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var postRequest = new HttpRequestMessage(HttpMethod.Post, device.Url(postPath)) { Content = content };
        await AddSignedIdentityHeadersAsync(postRequest, bodyBytes);
        using var postResponse = await Http.SendAsync(postRequest);
        postResponse.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize(
            await postResponse.Content.ReadAsStringAsync(), PlaylistSyncJsonContext.Default.PlaylistChangesResponseDto);
    }

    // See Flower.Server's trust gate - every gated endpoint requires these.
    // ConnectionClose forces a fresh connection per request: a session is a
    // couple of requests, and it avoids reusing a keep-alive connection the
    // server or the OS (iOS backgrounding) has already torn down.
    private async Task AddSignedIdentityHeadersAsync(HttpRequestMessage request, byte[] body)
    {
        await request.AddPeerCredentialsAsync(_credentials, body);
        request.Headers.ConnectionClose = true;
    }

    // One prompt at a time, each playlist once even with two sessions' worth
    // of questions about it. Nobody listening (sync running before the view
    // attaches) is nobody asked: nothing is overwritten on either side.
    private async Task<List<Answer>> AskAsync(List<Question> questions, string remoteAlias)
    {
        var answers = new List<Answer>();
        foreach (var question in questions)
        {
            var handler = ConflictDetected;
            if (handler == null)
                break;

            lock (_asking)
            {
                if (!_asking.Add(question.Local.Id))
                    continue;
            }

            try
            {
                var tcs = new TaskCompletionSource<PlaylistConflictChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
                handler.Invoke(this, new PlaylistConflictEventArgs
                {
                    Local = question.Local,
                    Remote = question.Remote,
                    RemoteAlias = remoteAlias,
                    Resolution = tcs,
                });

                var choice = await tcs.Task;
                _logger.LogInformation("Playlist {Name} with {RemoteAlias}: {Question} resolved as {Choice}",
                    question.Local.Name, remoteAlias, question.Remote == null ? "deleted on the server" : "conflict", choice);
                answers.Add(new Answer(question, choice));
            }
            finally
            {
                lock (_asking)
                    _asking.Remove(question.Local.Id);
            }
        }

        return answers;
    }

    private readonly HashSet<Guid> _asking = [];

    // An answer is an ordinary edit on top of the server's version: keeping
    // this device's copy agrees on the server's, so the session that follows
    // sees an edit here and sends it over that revision; keeping the server's
    // installs it. Applied only to the copy that was asked about - one edited
    // again since is left, and the next session asks again if it must.
    private void ApplyAnswers(DiscoveredDevice device, List<Answer> answers)
    {
        var agreed = new Dictionary<Guid, PlaylistAgreement?>();
        var taken = new Dictionary<Guid, Playlist?>();
        PlaylistTrackIndex? index = null;
        foreach (var (question, choice) in answers)
        {
            var id = question.Local.Id;
            switch (choice)
            {
                case PlaylistConflictChoice.KeepLocal:
                    _putOff.Remove(id);
                    agreed[id] = new PlaylistAgreement(question.RemoteRevision, question.Remote);
                    break;
                case PlaylistConflictChoice.KeepRemote:
                    _putOff.Remove(id);
                    taken[id] = question.Remote == null
                        ? null
                        : PlaylistSyncMapper.ToPlaylist(question.Remote, index ??= new PlaylistTrackIndex(_library.Tracks), _logger);
                    agreed[id] = question.Remote == null ? null : new PlaylistAgreement(question.RemoteRevision, question.Remote);
                    break;
                case PlaylistConflictChoice.Later:
                    _putOff[id] = (question.LocalVersion, question.RemoteRevision);
                    break;
            }
        }

        var unchanged = new HashSet<Guid>();
        _library.ReplacePlaylists(current =>
        {
            unchanged.Clear();
            var result = new List<Playlist>();
            foreach (var playlist in current)
            {
                var asked = answers.FirstOrDefault(a => a.Question.Local.Id == playlist.Id)?.Question;
                if (asked != null && asked.LocalVersion == PlaylistVersion.Of(playlist))
                {
                    unchanged.Add(playlist.Id);
                    if (taken.TryGetValue(playlist.Id, out var theirs))
                    {
                        if (theirs != null)
                            result.Add(theirs);
                        continue;
                    }
                }
                result.Add(playlist);
            }
            return result;
        });

        _agreements.Save(device.Fingerprint, agreed.Where(e => unchanged.Contains(e.Key)).ToDictionary(e => e.Key, e => e.Value));
    }

    // The questions put off, by the two versions they were put off at: not
    // asked again until one of them changes or the app restarts.
    private readonly Dictionary<Guid, (string LocalVersion, long RemoteRevision)> _putOff = new();
}
