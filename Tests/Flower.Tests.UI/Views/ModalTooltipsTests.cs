using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Views;

using Xunit;

namespace Flower.Tests.Views;

// A window behind a modal dialog shows no tooltips: hovering the sidebar's
// New Playlist button while the Vibe Playlist editor was open flickered its
// tooltip on and off for as long as the pointer stayed there.
public class ModalTooltipsTests
{
    public ModalTooltipsTests() => ModalTooltips.Install();

    private static (Window Owner, Button Button) Owner()
    {
        var button = new Button();
        ToolTip.SetTip(button, "New Playlist or Folder");
        var owner = new Window { Content = button };
        owner.Show();
        return (owner, button);
    }

    [AvaloniaFact]
    public void A_dialog_turns_its_owners_tooltips_off_until_it_closes()
    {
        var (owner, button) = Owner();
        var dialog = new Window();

        _ = dialog.ShowDialog(owner);
        Dispatcher.UIThread.RunJobs();
        Assert.False(ToolTip.GetServiceEnabled(button));

        dialog.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(ToolTip.GetServiceEnabled(button));
        owner.Close();
    }
}
