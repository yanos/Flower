using Avalonia.Controls;
using Avalonia.Interactivity;

using Flower.Services;

namespace Flower.Views;

public partial class PlaylistConflictWindow : Window
{
    public PlaylistConflictWindow(PlaylistConflictEventArgs conflict)
    {
        InitializeComponent();

        HeadlineText.Text = $"\"{conflict.Local.Name}\" was edited on both devices";
        LocalSummaryText.Text = Summarize(conflict.Local.Name, conflict.Local.Tracks.Count, conflict.Local.UpdatedAt);
        RemoteHeaderText.Text = conflict.RemoteAlias;
        RemoteSummaryText.Text = Summarize(conflict.Remote.Name, conflict.Remote.Tracks.Count, conflict.Remote.UpdatedAt);
        KeepRemoteButton.Content = $"Keep {conflict.RemoteAlias}'s Version";
        NativeMenuHelper.InheritFromMainWindow(this);
    }

    private static string Summarize(string name, int trackCount, System.DateTimeOffset updatedAt) =>
        $"\"{name}\" - {trackCount} track{(trackCount == 1 ? "" : "s")} - last changed {updatedAt.LocalDateTime:g}";

    private void KeepLocalButton_Click(object? sender, RoutedEventArgs e) => Close(PlaylistConflictChoice.KeepLocal);

    private void KeepRemoteButton_Click(object? sender, RoutedEventArgs e) => Close(PlaylistConflictChoice.KeepRemote);
}
