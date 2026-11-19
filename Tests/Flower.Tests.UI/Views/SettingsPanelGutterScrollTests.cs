using System.Linq;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Tests.TestSupport;
using Flower.Views;

using Xunit;

namespace Flower.Tests.Views;

// The wheel over the settings screen's margin - between the sidebar and the
// controls on a server's page - used to land outside every tab's scroller and
// scroll nothing.
public class SettingsPanelGutterScrollTests : SettingsLogTabHarness
{
    [AvaloniaFact]
    public async Task The_wheel_over_the_margin_scrolls_the_open_tab()
    {
        var panel = new SettingsPanel(await MakeAsync());
        var window = new Window { Width = 600, Height = 180 };
        window.Styles.Add(new FluentTheme());
        window.Content = panel;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var tabs = panel.GetVisualDescendants().OfType<TabControl>().First();
        var scroller = Assert.IsType<ScrollViewer>(Assert.IsType<TabItem>(tabs.SelectedItem).Content);
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height, "the tab needs to be taller than the window for this to mean anything");

        window.MouseWheel(new Point(4, 100), new Vector(0, -1), RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(scroller.Offset.Y > 0);
        window.Close();
    }
}
