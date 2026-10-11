using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Controls;

using Xunit;

namespace Flower.Tests.Views;

// The app's one text box. The border it draws is a theme brush every TextBox
// picks up (Theme.axaml's TextControlBorderBrush*), so what is this control's
// own is the little x at the end of the line - which is what these are about.
public class TextInputTests
{
    private static (Window Window, TextInput Box) Show(string? text = null)
    {
        var box = new TextInput { Text = text, Width = 200 };
        var window = new Window { Width = 300, Height = 120 };
        window.Styles.Add(new FluentTheme());
        window.Content = box;
        window.Show();
        Pump(window);
        return (window, box);
    }

    private static void Pump(Window window)
    {
        window.Measure(new Size(300, 120));
        window.Arrange(new Rect(0, 0, 300, 120));
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
    }

    private static Button ClearButton(Window window) =>
        window.GetVisualDescendants().OfType<Button>().Single();

    [AvaloniaFact]
    public void Tapping_it_empties_the_box_and_leaves_the_caret_in_it()
    {
        var (window, box) = Show("hum");

        var clear = ClearButton(window);
        // Through the button's own click rather than a synthesized pointer:
        // what matters here is what the handler does, not where it sits.
        clear.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump(window);

        Assert.True(string.IsNullOrEmpty(box.Text));
        Assert.True(box.IsFocused, "the caret left the box");
        Assert.False(clear.IsVisible);
        window.Close();
    }
}
