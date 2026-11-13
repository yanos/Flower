using Avalonia.Controls;
using Avalonia.Input;
using Flower.Audio.Ffmpeg;
using Flower.Services;
using Flower.ViewModels;

namespace Flower.Views;

public partial class AboutWindow : Window
{
    // For the XAML loader and the previewer.
    public AboutWindow() : this(null)
    {
    }

    // `updates` is the app's one AppUpdatesViewModel (MainViewModel.Updates),
    // so a check from here is the one the banner and Settings see; null
    // where there is no updater hides the row.
    public AboutWindow(AppUpdatesViewModel? updates)
    {
        InitializeComponent();

        // The decoder's own versions under the app's: which FFmpeg is playing
        // is the first thing to ask about a track that will not.
        VersionTextBlock.Text = DecoderVersion.Display is { } decoder
            ? $"Version {AppVersion.Display}\n{decoder}"
            : $"Version {AppVersion.Display}";
        ReportIssueLink.NavigateUri = IssueLink.General(AppVersion.Full, IssueLink.Platform);

        if (updates != null)
        {
            UpdatesPanel.DataContext = updates;
            UpdatesPanel.IsVisible = true;
        }
    }

    // The menu's Check for Updates…: the answer shows in this window's
    // updates row, so it opens here and starts the check its button does.
    public static void ShowCheckingForUpdates(AppUpdatesViewModel updates)
    {
        new AboutWindow(updates).Show();
        if (updates.CheckNowCommand.CanExecute(null))
            updates.CheckNowCommand.Execute(null);
    }

    private void LicencesButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        new OpenSourceLicencesWindow().Show();

    // No button here to hang IsCancel="True" off of (unlike SettingsWindow/
    // ColumnSelectorWindow/etc.) - this is a pure info popup, so Escape just
    // closes it directly instead.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
            Close();
    }
}
