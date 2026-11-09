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
// asks that server to do the write instead (ServerArtIds).
//
// The credentials are whichever this head has, as the container picked them
// for MainViewModel: a native head's device key, or the browser's pairing.
//
// A change is for the songs picked and no others. Where the setting keeps
// covers in the album's folder (ArtworkPlacement.Folder), that is only where
// every song of the album was picked, since a cover file there is every
// song's in that folder; the rest get theirs in their own files.
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
    // none, and is handled by ServerArtIds below instead.
    public List<Track> LocalTargets() =>
        [.. tracks.Where(AlbumArtLoader.IsLocalFile)];

    // Whether a cover may go into an album's folder for these songs: the
    // setting says so, and every song of the album is among them. A song with
    // no album name has no album to be the whole of.
    private bool ToFolder(HashSet<Track> picked, Track song)
    {
        if (settings.ArtworkPlacement != ArtworkPlacement.Folder || string.IsNullOrEmpty(song.Album))
            return false;

        return library.Snapshot.AlbumTracks(CatalogIdentity.AlbumIdFor(song)).All(picked.Contains);
    }

    // ── Artwork on the paired server ───────────────────────────────────────
    //
    // The other half of "replace this cover": the track being looked at has no
    // local file, because it lives on the server this device is paired with.
    // Rather than refuse, an admin device asks the server to do the write, over
    // the same signed admin surface the settings screen uses. See
    // AdminEndpoints' /cover-art routes for why that is an admin route rather
    // than one a listener could reach.
    //
    // Addressed by each song's id on the server (Track.OriginTrackId), which
    // the route writes into that song's file alone - the server then names
    // that song's cover by its own id (AlbumCover), so it is drawn with it
    // rather than with the album's. An album picked whole goes as its album
    // id instead: one call rather than one per song, and the only way to have
    // the server keep it in the album's folder.
    public List<(string Id, ArtworkPlacement Placement)> ServerArtIds()
    {
        if (tracks.Count == 0 || LocalTargets().Count > 0 || PairedServerSourceName(tracks, settings) == null)
            return [];

        // PeerTrackResolver owns "may this device still ask that peer for this
        // track" - the same rule the art fetch goes through. No resolver, no
        // credentials, or a peer that is not reachable right now all mean the
        // same thing here: nothing to send the picture to.
        if (peerTrackResolver?.Resolve(tracks[0]) == null || peerCredentials == null)
            return [];

        var ids = new List<(string Id, ArtworkPlacement Placement)>();
        foreach (var album in tracks.GroupBy(CatalogIdentity.AlbumIdFor, StringComparer.Ordinal))
        {
            if (!string.IsNullOrEmpty(album.First().Album)
                && library.Snapshot.AlbumTracks(album.Key).All(tracks.Contains))
            {
                ids.Add((album.Key, settings.ArtworkPlacement));
                continue;
            }

            ids.AddRange(album
                .Select(t => t.OriginTrackId)
                .OfType<string>()
                .Where(id => id.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Select(id => (id, ArtworkPlacement.Files)));
        }

        return ids;
    }

    // How many files a change rewrites, for Track Info to say while it does:
    // one per song picked, here or on the server.
    public int FileCount =>
        LocalTargets().Count is > 0 and var local ? local : ServerArtIds().Count == 0 ? 0 : tracks.Count;

    // Somewhere to write - and, for a song of the paired server's, the right
    // to (SyncRolePolicy.MayEditSong): a cover is changed for everybody, so on
    // a guest's device the server's songs are not this to change, downloaded
    // copy or not.
    public bool CanWrite =>
        tracks.All(MayEdit) && (LocalTargets().Count > 0 || ServerArtIds().Count > 0);

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
            new LocalAlbumArt(bytes, mimeType),
            (client, artId, placement) => client.SetCoverArtAsync(artId, bytes, mimeType, placement),
            ArtworkDimensions.Of(bytes),
            "The artwork couldn't be written to");

    // The counterpart: takes the picture back out.
    public Task<string?> RemoveAsync() =>
        ApplyAsync(
            art: null,
            (client, artId, placement) => client.RemoveCoverArtAsync(artId, placement),
            ArtworkDimensions.None,
            "The artwork couldn't be removed from");

    // serverArtwork is what a song the server rewrote now measures, as far as
    // this device can tell without its file: the picture sent, which goes into
    // the tag and so is the one drawn, or none for a removal - where a cover
    // file beside the server's copy would still be found, and the next pull's
    // catalog says so (TrackDto.ArtWidth).
    private async Task<string?> ApplyAsync(
        LocalAlbumArt? art,
        Func<ServerAdminClient, string, ArtworkPlacement, Task<CoverArtWriteDto>> writeServer,
        ArtworkDimensions serverArtwork,
        string failureVerb)
    {
        var targets = LocalTargets();
        var artIds = ServerArtIds();
        if (targets.Count == 0 && artIds.Count == 0)
            return null;

        // Into the files, or the folder of an album picked whole (ToFolder) -
        // which AlbumArtWriter.Apply still declines for a folder holding
        // songs of other albums, putting those in their files instead.
        string? message = null;
        IReadOnlyList<Track> changed = tracks;
        if (targets.Count > 0)
        {
            var results = await Task.Run(() =>
            {
                var picked = targets.ToHashSet();
                var toFolder = targets.ToLookup(t => ToFolder(picked, t));
                var applied = AlbumArtWriter.Apply(library, [.. toFolder[true]], art, ArtworkPlacement.Folder, logger)
                    .Concat(AlbumArtWriter.Apply(library, [.. toFolder[false]], art, ArtworkPlacement.Files, logger))
                    .ToList();
                foreach (var (track, _) in applied)
                    ArtworkDimensions.OfFile(track.Path!).ApplyTo(track);
                return applied;
            });
            changed = [.. tracks.Concat(results.Select(r => r.Track)).Distinct()];
            var failed = results.Count(r => !r.Written);
            if (failed > 0)
                message = failed == results.Count
                    ? $"{failureVerb} the file."
                    : $"{failureVerb} {failed} of {results.Count} files.";
        }
        else
        {
            var (written, error) = await ApplyServerArtAsync(artIds, writeServer);
            message = error ?? (written == 0 ? "The server didn't change any files." : null);
            if (written > 0)
            {
                foreach (var track in tracks)
                    serverArtwork.ApplyTo(track);
            }
        }

        // The cached bitmap was decoded from bytes that are no longer what the
        // file (or the server) holds; without this every other view in the app
        // keeps painting the old cover until the process restarts.
        foreach (var track in changed)
            AlbumArtLoader.Invalidate(track);

        library.NotifyTracksChanged(changed, TrackChange.Artwork);
        return message;
    }

    // Runs one admin call per album (or song) the change covers, and reports how many
    // files the server said it rewrote. A ServerAdminException carries the
    // server's own words ("This device is paired, but is not an administrator
    // of that server.") which is exactly what to show.
    private async Task<(int Written, string? Error)> ApplyServerArtAsync(
        IReadOnlyList<(string Id, ArtworkPlacement Placement)> artIds, Func<ServerAdminClient, string, ArtworkPlacement, Task<CoverArtWriteDto>> apply)
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
        foreach (var (artId, placement) in artIds)
        {
            try
            {
                written += (await apply(client, artId, placement)).Written;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not change the album art for {ArtId} on {Server}", artId, device.BaseUri);
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
