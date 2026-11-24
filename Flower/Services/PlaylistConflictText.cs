using System;

namespace Flower.Services;

// What a playlist prompt says, the same on the desktop window and the phone's
// sheet. Two kinds: changed on both sides, and deleted on the server while
// this device cannot tell whether its copy holds an edit (docs/
// PLAYLIST-SYNC-PLAN.md, decision 1: "Deleted on your server. Keep it?").
public static class PlaylistConflictText
{
    public static string Headline(PlaylistConflictEventArgs conflict) =>
        conflict.DeletedOnServer
            ? $"\"{conflict.Local.Name}\" was deleted on your server. Keep it?"
            : $"\"{conflict.Local.Name}\" was edited on both devices";

    public static string Explanation(PlaylistConflictEventArgs conflict) =>
        conflict.DeletedOnServer
            ? "This device still has it, and cannot tell whether it was changed here. Nothing is deleted until you choose."
            : "This device and the other one both changed it since they last synced. Nothing is overwritten until you choose.";

    public static string LocalSummary(PlaylistConflictEventArgs conflict) =>
        Summary(conflict.Local.Name, conflict.Local.Tracks.Count, conflict.Local.UpdatedAt);

    public static string RemoteSummary(PlaylistConflictEventArgs conflict) =>
        conflict.Remote is { } remote
            ? Summary(remote.Name, remote.Tracks.Count, remote.UpdatedAt)
            : "Deleted";

    public static string KeepLocal(PlaylistConflictEventArgs conflict) =>
        conflict.DeletedOnServer ? "Keep It" : "Keep This Device's Version";

    public static string KeepRemote(PlaylistConflictEventArgs conflict) =>
        conflict.DeletedOnServer ? "Delete It" : $"Keep {conflict.RemoteAlias}'s Version";

    private static string Summary(string name, int trackCount, DateTimeOffset updatedAt) =>
        $"\"{name}\" - {trackCount} track{(trackCount == 1 ? "" : "s")} - last changed {updatedAt.LocalDateTime:g}";
}
