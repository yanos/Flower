using System;

namespace Flower.Services;

// What a playlist conflict says, the same on the desktop window and the
// phone's sheet.
public static class PlaylistConflictText
{
    public const string Explanation =
        "This device and the other one both changed it since they last synced. Nothing is overwritten until you choose.";

    public static string Headline(PlaylistConflictEventArgs conflict) =>
        $"\"{conflict.Local.Name}\" was edited on both devices";

    public static string LocalSummary(PlaylistConflictEventArgs conflict) =>
        Summary(conflict.Local.Name, conflict.Local.Tracks.Count, conflict.Local.UpdatedAt);

    public static string RemoteSummary(PlaylistConflictEventArgs conflict) =>
        Summary(conflict.Remote.Name, conflict.Remote.Tracks.Count, conflict.Remote.UpdatedAt);

    public static string KeepRemote(PlaylistConflictEventArgs conflict) =>
        $"Keep {conflict.RemoteAlias}'s Version";

    private static string Summary(string name, int trackCount, DateTimeOffset updatedAt) =>
        $"\"{name}\" - {trackCount} track{(trackCount == 1 ? "" : "s")} - last changed {updatedAt.LocalDateTime:g}";
}
