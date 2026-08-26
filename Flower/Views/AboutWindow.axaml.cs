using Avalonia.Controls;
using Avalonia.Input;
using Flower.Services;

namespace Flower.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        VersionTextBlock.Text = $"Version {AppVersion.Display}";
    }

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
