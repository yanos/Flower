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

        var text = PlaylistConflictText.Of(conflict);
        HeadlineText.Text = text.Headline;
        ExplanationText.Text = text.Explanation;
        LocalSummaryText.Text = text.LocalSummary;
        RemoteHeaderText.Text = text.RemoteName;
        RemoteSummaryText.Text = text.RemoteSummary;
        KeepLocalButton.Content = text.KeepLocal;
        KeepRemoteText.Text = text.KeepRemote;
        NativeMenuHelper.InheritFromMainWindow(this);
    }

    private void KeepLocalButton_Click(object? sender, RoutedEventArgs e) => Close(PlaylistConflictChoice.KeepLocal);

    private void KeepRemoteButton_Click(object? sender, RoutedEventArgs e) => Close(PlaylistConflictChoice.KeepRemote);

    private void DecideLaterButton_Click(object? sender, RoutedEventArgs e) => Close(PlaylistConflictChoice.Later);
}
