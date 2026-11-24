using System;

namespace Flower.Services;

// What a playlist prompt says, the same on the desktop window and the phone's
// sheet, worked out once per prompt. Two kinds: changed on both sides, and
// deleted on the server while this device cannot tell whether its copy holds
// an edit (docs/PLAYLIST-SYNC-PLAN.md, decision 1: "Deleted on your server.
// Keep it?").
public sealed record PlaylistConflictText(
    string Headline, string Explanation, string LocalSummary, string RemoteName, string RemoteSummary,
    string KeepLocal, string KeepRemote)
{
    public static PlaylistConflictText Of(PlaylistConflictEventArgs conflict)
    {
        var deleted = conflict.DeletedOnServer;
        var local = conflict.Local;
        return new PlaylistConflictText(
            Headline: deleted
                ? $"\"{local.Name}\" was deleted on your server. Keep it?"
                : $"\"{local.Name}\" was edited on both devices",
            Explanation: deleted
                ? "This device still has it, and cannot tell whether it was changed here. Nothing is deleted until you choose."
                : "This device and the other one both changed it since they last synced. Nothing is overwritten until you choose.",
            LocalSummary: Summary(local.Name, local.Tracks.Count, local.UpdatedAt),
            RemoteName: conflict.RemoteAlias,
            RemoteSummary: conflict.Remote is { } remote ? Summary(remote.Name, remote.Tracks.Count, remote.UpdatedAt) : "Deleted",
            KeepLocal: deleted ? "Keep It" : "Keep This Device's Version",
            KeepRemote: deleted ? "Delete It" : $"Keep {conflict.RemoteAlias}'s Version");
    }

    private static string Summary(string name, int trackCount, DateTimeOffset updatedAt) =>
        $"\"{name}\" - {trackCount} track{(trackCount == 1 ? "" : "s")} - last changed {updatedAt.LocalDateTime:g}";
}
