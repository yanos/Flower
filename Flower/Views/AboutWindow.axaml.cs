using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;

namespace Flower.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var informationalVersion = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (informationalVersion != null)
        {
            // Strip MinVer's "+<commit-sha>" build metadata - not meaningful to a user.
            var plusIndex = informationalVersion.IndexOf('+');
            var displayVersion = plusIndex >= 0
                ? informationalVersion[..plusIndex]
                : informationalVersion;
            VersionTextBlock.Text = $"Version {displayVersion}";
        }
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
