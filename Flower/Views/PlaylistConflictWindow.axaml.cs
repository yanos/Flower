using Avalonia.Controls;
using Avalonia.Interactivity;

using Flower.Services;

namespace Flower.Views;

public partial class PlaylistConflictWindow : Window
{
    // Satisfies Avalonia's runtime-XAML-loader/previewer check (AVLN3001) -
    // never called directly; the real constructor below is what's actually used.
    public PlaylistConflictWindow() => InitializeComponent();

    public PlaylistConflictWindow(PlaylistConflictEventArgs conflict)
    {
        InitializeComponent();

        HeadlineText.Text = PlaylistConflictText.Headline(conflict);
        ExplanationText.Text = PlaylistConflictText.Explanation;
        LocalSummaryText.Text = PlaylistConflictText.LocalSummary(conflict);
        RemoteHeaderText.Text = conflict.RemoteAlias;
        RemoteSummaryText.Text = PlaylistConflictText.RemoteSummary(conflict);
        KeepRemoteText.Text = PlaylistConflictText.KeepRemote(conflict);
        NativeMenuHelper.InheritFromMainWindow(this);
    }

    private void KeepLocalButton_Click(object? sender, RoutedEventArgs e) => Close(PlaylistConflictChoice.KeepLocal);

    private void KeepRemoteButton_Click(object? sender, RoutedEventArgs e) => Close(PlaylistConflictChoice.KeepRemote);

    private void DecideLaterButton_Click(object? sender, RoutedEventArgs e) => Close(PlaylistConflictChoice.Later);
}
