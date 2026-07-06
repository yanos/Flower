using System.Reflection;
using Avalonia.Controls;

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
}
