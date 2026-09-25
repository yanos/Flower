using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Models;
using Flower.Persistence;

namespace Flower.Services;

// How a "Remove from Library" went, in the words the UI shows. Error set
// means nothing was removed at all.
public sealed record LibraryRemovalOutcome(int Removed, string? Error = null);

// "Remove from Library" on a device, deciding what that means for each song
// before LibraryRemoval carries it out.
//
// The question is whose song it is. A song this device has on its own - a file
// it imported, or anything its paired server does not know about - is this
// device's to remove. A song the paired server serves is the server's: removed
// here alone, the next catalog pull would bring it straight back as a
// placeholder, so it is removed there, and only a device the server made an
// admin can ask that. The server goes first, and a refusal or an unreachable
// server removes nothing locally either - half a removal is the one outcome
// that would look like it worked and then undo itself.
//
// A listener's phone (paired, not an admin) therefore cannot remove the
// server's songs at all, and is not offered to: CanRemove says no, and the
// menus hide the entry. Its own files it can remove like anyone else.
public class LibraryRemovalService(
    Library library,
    AppSettings appSettings,
    PeerTrackResolver? resolver,
    IPeerCredentials? credentials,
    ILogger<LibraryRemovalService> logger)
{
    // A song the paired server serves: it carries the server's id for it and
    // came from the server this device is paired with now. A stale origin -
    // a server paired with before - is not that server's to answer for.
    public bool IsOnPairedServer(Track track) =>
        track.OriginTrackId is { Length: > 0 }
        && track.OriginDeviceFingerprint is { } origin
        && string.Equals(origin, appSettings.PairedServerFingerprint, StringComparison.OrdinalIgnoreCase);

    public bool CanRemove(Track track) =>
        !IsOnPairedServer(track) || resolver?.Resolve(track) is { WeAreAdmin: true };

    public IReadOnlyList<Track> Removable(IEnumerable<Track> tracks) => tracks.Where(CanRemove).ToList();

    // Whether removing these would delete anything that is a file somewhere -
    // this device's own, or the server's. What decides whether the "also
    // delete the files" choice is worth offering.
    public bool HasFiles(IEnumerable<Track> tracks) =>
        tracks.Any(t => t.Path != null || IsOnPairedServer(t));

    public bool AnyOnPairedServer(IEnumerable<Track> tracks) => tracks.Any(IsOnPairedServer);

    public virtual async Task<LibraryRemovalOutcome> RemoveAsync(IReadOnlyList<Track> tracks, bool deleteFiles)
    {
        var removable = Removable(tracks);
        if (removable.Count == 0)
            return new LibraryRemovalOutcome(0);

        var onServer = removable.Where(IsOnPairedServer).ToList();
        var serverFilesNotDeleted = 0;
        if (onServer.Count > 0)
        {
            var (error, notDeleted) = await RemoveOnServerAsync(onServer, deleteFiles);
            if (error != null)
                return new LibraryRemovalOutcome(0, error);
            serverFilesNotDeleted = notDeleted;
        }

        var result = LibraryRemoval.Remove(library, removable, deleteFiles, logger);
        var problems = new List<string>();
        if (result.FilesNotDeleted.Count > 0)
            problems.Add($"{result.FilesNotDeleted.Count} file(s) on this device could not be deleted.");
        // The server removed those songs either way - they are out of its
        // catalog - so a file it could not delete is worth saying, not worth
        // undoing the removal over. A read-only music folder is the usual
        // reason: that is how docker-compose.yml mounts it.
        if (serverFilesNotDeleted > 0)
            problems.Add($"{serverFilesNotDeleted} file(s) on the server could not be deleted - its music folder may be read-only.");

        return problems.Count == 0
            ? new LibraryRemovalOutcome(result.Removed)
            : new LibraryRemovalOutcome(result.Removed,
                string.Join(" ", problems) + " Those songs are out of the library, but their files are still there.");
    }

    // Error is null on success, otherwise the sentence to show; FilesNotDeleted
    // is how many of the server's files it asked to delete are still there.
    private async Task<(string? Error, int FilesNotDeleted)> RemoveOnServerAsync(IReadOnlyList<Track> tracks, bool deleteFiles)
    {
        if (resolver?.Resolve(tracks[0]) is not { WeAreAdmin: true } device || credentials == null)
            return ("Your server isn't reachable right now, so its songs can't be removed.", 0);

        try
        {
            using var http = PeerHttpClient.Create(TimeSpan.FromMinutes(1));
            var client = new ServerAdminClient(http, device.BaseUri, ServerAdminClient.SignWith(credentials), logger: logger);
            var response = await client.RemoveFromLibraryAsync(
                new LibraryRemovalRequestDto(tracks.Select(t => t.OriginTrackId!).Distinct().ToList(), deleteFiles));
            logger.LogInformation("Removed {Removed} track(s) from {Server}'s library ({Trashed} file(s) to its trash, {Deleted} deleted, {NotDeleted} left in place)",
                response.Removed, device.Alias, response.FilesTrashed, response.FilesDeleted, response.FilesNotDeleted);
            return (null, response.FilesNotDeleted);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not remove {Count} track(s) from {Server}'s library", tracks.Count, device.Alias);
            return (ex is ServerAdminException { IsAuthFailure: true }
                ? "Your server refused: only an administrator can remove its songs."
                : ex is ServerAdminException admin
                    ? admin.Message
                    : "Your server isn't reachable right now, so its songs can't be removed.", 0);
        }
    }
}
