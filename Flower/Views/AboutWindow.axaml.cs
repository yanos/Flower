using Avalonia.Controls;
using Avalonia.Input;
using Flower.Audio.Ffmpeg;
using Flower.Services;

namespace Flower.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        // The decoder's own versions under the app's: which FFmpeg is playing
        // is the first thing to ask about a track that will not.
        VersionTextBlock.Text = DecoderVersion.Display is { } decoder
            ? $"Version {AppVersion.Display}\n{decoder}"
            : $"Version {AppVersion.Display}";
        ReportIssueLink.NavigateUri = IssueLink.General(AppVersion.Full, IssueLink.Platform);
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
