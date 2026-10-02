using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Models;
using Flower.Persistence;

namespace Flower.Services;

// Replacing or removing the cover of a set of tracks, for the desktop Track
// Info window's Artwork tab. Out of the window so that what it needs - the
// paired server and the credentials to reach it - is handed in by
// MainViewModel.ArtEditorFor rather than looked up in the container.
//
// A change writes the picture straight into the tag rather than waiting for a
// Save - see TrackInfoWindow's Artwork tab comment for why - and, when the
// tracks have no file on this device because they live on the paired server,
// asks that server to do the write instead (ServerAlbumIds).
//
// The credentials are whichever this head has, as the container picked them
// for MainViewModel: a native head's device key, or the browser's pairing.
public sealed class AlbumArtEditor(
    IReadOnlyList<Track> tracks,
    Library library,
    AppSettings settings,
    PeerTrackResolver? peerTrackResolver,
    IPeerCredentials? peerCredentials,
    ILogger logger)
{
    // The tracks a write can land on directly: a file on this disk to embed a
    // picture into. A placeholder that only exists on the paired server has
    // none, and is handled by ServerAlbumIds below instead.
    public List<Track> LocalTargets() =>
        [.. tracks.Where(AlbumArtLoader.IsLocalFile)];

    // ── Artwork on the paired server ───────────────────────────────────────
    //
    // The other half of "replace this cover": the track being looked at has no
    // local file, because it lives on the server this device is paired with.
    // Rather than refuse, an admin device asks the server to do the write, over
    // the same signed admin surface the settings screen uses. See
    // AdminEndpoints' /cover-art routes for why that is an admin route rather
    // than one a listener could reach.
    //
    // Addressed by album id, not by track: art is served per album on the way
    // out (LibraryDtoMapper's CoverArt field, and PeerCoverArtUrlResolver asks
    // for exactly this id), so writing into one track's file would leave the
    // album still serving whichever other file the read path reached first.
    // Distinct, because a batch selection can span albums.
    public List<string> ServerAlbumIds()
    {
        if (tracks.Count == 0 || LocalTargets().Count > 0 || PairedServerSourceName(tracks, settings) == null)
            return [];

        // PeerTrackResolver owns "may this device still ask that peer for this
        // track" - the same rule the art fetch goes through. No resolver, no
        // credentials, or a peer that is not reachable right now all mean the
        // same thing here: nothing to send the picture to.
        if (peerTrackResolver?.Resolve(tracks[0]) == null || peerCredentials == null)
            return [];

        return [.. tracks.Select(CatalogIdentity.AlbumIdFor).Distinct(StringComparer.Ordinal)];
    }

    // Somewhere to write - and, for a song of the paired server's, the right
    // to (SyncRolePolicy.MayEditSong): a cover is changed for everybody, so on
    // a guest's device the server's songs are not this to change, downloaded
    // copy or not.
    public bool CanWrite =>
        tracks.All(MayEdit) && (LocalTargets().Count > 0 || ServerAlbumIds().Count > 0);

    private bool MayEdit(Track track) =>
        SyncRolePolicy.MayEditSong(
            track, settings.PairedServerFingerprint,
            peerTrackResolver?.Resolve(track)?.WeAreAdmin ?? settings.PairedServerGrantsAdmin);

    // Embeds a picture in the tracks' own files, or - when they only exist on
    // the paired server - asks that server to do the same to its
    // (AlbumArtWriter is the shared implementation of the write itself, so the
    // two paths cannot disagree about how a picture goes into a tag). Null when
    // it all went through, or what to tell the user when it did not.
    public Task<string?> WriteAsync(byte[] bytes, string mimeType) =>
        ApplyAsync(
            path => AlbumArtWriter.TryWrite(path, bytes, mimeType, logger),
            (client, albumId) => client.SetCoverArtAsync(albumId, bytes, mimeType),
            "The artwork couldn't be written to");

    // The counterpart: takes the picture back out.
    public Task<string?> RemoveAsync() =>
        ApplyAsync(
            path => AlbumArtWriter.TryRemove(path, logger),
            (client, albumId) => client.RemoveCoverArtAsync(albumId),
            "The artwork couldn't be removed from");

    private async Task<string?> ApplyAsync(
        Func<string, bool> writeLocal,
        Func<ServerAdminClient, string, Task<CoverArtWriteDto>> writeServer,
        string failureVerb)
    {
        var targets = LocalTargets();
        var albumIds = ServerAlbumIds();
        if (targets.Count == 0 && albumIds.Count == 0)
            return null;

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
        foreach (var track in tracks)
            AlbumArtLoader.Invalidate(track);

        library.NotifyTracksChanged(tracks, TrackChange.Artwork);
        return message;
    }

    // Runs one admin call per album the selection covers, and reports how many
    // files the server said it rewrote. A ServerAdminException carries the
    // server's own words ("This device is paired, but is not an administrator
    // of that server.") which is exactly what to show.
    private async Task<(int Written, string? Error)> ApplyServerArtAsync(
        IReadOnlyList<string> albumIds, Func<ServerAdminClient, string, Task<CoverArtWriteDto>> apply)
    {
        var device = peerTrackResolver?.Resolve(tracks[0]);
        var credentials = peerCredentials;
        if (device == null || credentials == null)
            return (0, "That server isn't reachable right now.");

        // PeerHttpClient rather than a bare one, for the same reason the art
        // fetch uses it: this is the same peer on the same origin, under the
        // same accepted-certificate rule. Generous timeout - the server is
        // rewriting whole audio files, one per track on the album.
        using var http = PeerHttpClient.Create(TimeSpan.FromMinutes(2));
        var client = new ServerAdminClient(http, device.BaseUri, ServerAdminClient.SignWith(credentials), logger: logger);

        var written = 0;
        foreach (var albumId in albumIds)
        {
            try
            {
                written += (await apply(client, albumId)).Written;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not change the album art for {AlbumId} on {Server}", albumId, device.BaseUri);
                return (written, ex is ServerAdminException ? ex.Message : "Could not reach that server.");
            }
        }

        return (written, null);
    }

    // The paired server's name, when every one of these tracks is a placeholder
    // for one of its songs rather than a file on this device; null otherwise.
    public static string? PairedServerSourceName(IReadOnlyList<Track> tracks, AppSettings settings)
    {
        if (tracks.Count == 0)
            return null;

        if (settings.PairedServerFingerprint is not { Length: > 0 } fingerprint)
            return null;

        foreach (var track in tracks)
        {
            if (track.Path != null || track.OriginDeviceFingerprint != fingerprint)
                return null;
        }

        return string.IsNullOrWhiteSpace(settings.PairedServerAlias) ? "Server" : settings.PairedServerAlias;
    }
}
