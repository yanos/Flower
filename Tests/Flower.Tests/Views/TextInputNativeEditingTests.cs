using System;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

using Flower.Controls;
using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.Views;

// On a phone a box is typed in through the platform's own field, laid over it
// while it has the focus (NativeTextSession). Everything that listens to the
// box - its Text and the bindings on it, its Enter, its LostFocus - has to
// carry on as if the box were being typed in itself.
//
// In a collection of its own that runs alone, because the factory it sets is
// read by every box in the process - see NativeTextFieldCollection.
[Collection("NativeTextField")]
public sealed class TextInputNativeEditingTests : IDisposable
{
    private readonly FakeNativeTextFieldFactory _factory = new();

    public TextInputNativeEditingTests() => PlatformNativeTextField.Factory = _factory;

    public void Dispose() => PlatformNativeTextField.Factory = null;

    private static (Window Window, TextInput First, TextInput Second) Show(string? text = null)
    {
        var first = new TextInput { Text = text, Width = 200 };
        var second = new TextInput { Width = 200 };
        var window = new Window { Width = 300, Height = 200 };
        window.Styles.Add(new FluentTheme());
        window.Content = new StackPanel { Children = { first, second } };
        window.Show();
        Pump();
        return (window, first, second);
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private FakeNativeTextField Field(int index = 0) => _factory.Created[index];

    [AvaloniaFact]
    public void Focusing_a_box_types_in_the_platforms_field_starting_from_the_boxs_text()
    {
        var (window, box, _) = Show("hum");

        box.Focus();
        Pump();

        Assert.Single(_factory.Created);
        Assert.Equal("hum", Field().Text);
        Assert.True(Field().IsEditing);
        // Avalonia's own keyboard would come up alongside the platform's.
        Assert.False(InputMethod.GetIsInputMethodEnabled(box));
        window.Close();
    }

    // The Search box tapped back into after a suggestion put its text there.
    [AvaloniaFact]
    public void A_box_focused_again_types_on_from_the_end_of_its_text()
    {
        var (window, box, second) = Show();
        box.Focus();
        Pump();
        box.Text = "Thelonious Monk";
        second.Focus();
        Pump();

        box.Focus();
        Pump();

        Assert.Equal((15, 15), _factory.Created.Last().Selection);
        window.Close();
    }

    [AvaloniaFact]
    public void A_selection_the_box_was_given_is_the_fields()
    {
        var (window, box, _) = Show("Old Name");
        box.SelectAll();

        box.Focus();
        Pump();

        Assert.Equal((0, 8), Field().Selection);
        window.Close();
    }

    [AvaloniaFact]
    public void What_is_typed_in_the_field_is_the_boxs_text()
    {
        var (window, box, _) = Show();
        var changes = 0;
        box.TextChanged += (_, _) => changes++;
        box.Focus();
        Pump();

        Field().Type("humming");
        Pump();

        Assert.Equal("humming", box.Text);
        Assert.Equal(1, changes);
        window.Close();
    }

    [AvaloniaFact]
    public void Text_put_in_the_box_while_it_is_typed_in_shows_in_the_field()
    {
        // A suggestion chip under the box, or its clear button.
        var (window, box, _) = Show("hum");
        box.Focus();
        Pump();

        box.Text = "Ambient";

        Assert.Equal("Ambient", Field().Text);
        Assert.Equal((7, 7), Field().Selection);
        window.Close();
    }

    [AvaloniaFact]
    public void Return_is_the_boxs_Enter()
    {
        var (window, box, _) = Show();
        var enters = 0;
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                enters++;
                e.Handled = true;
            }
        };
        box.Focus();
        Pump();

        Field().PressReturn();

        Assert.Equal(1, enters);
        Assert.True(box.IsFocused, "a handled Enter decides for itself whether the box keeps the focus");
        window.Close();
    }

    [AvaloniaFact]
    public void A_Return_nothing_handles_puts_the_keyboard_away()
    {
        var (window, box, _) = Show();
        box.Focus();
        Pump();

        Field().PressReturn();
        Pump();

        Assert.False(box.IsFocused);
        Assert.True(Field().IsDisposed);
        window.Close();
    }

    [AvaloniaFact]
    public void The_platform_taking_the_keyboard_away_is_the_box_losing_the_focus()
    {
        // So whatever commits on LostFocus - a rename - commits.
        var (window, box, _) = Show();
        var lostFocus = 0;
        box.LostFocus += (_, _) => lostFocus++;
        box.Focus();
        Pump();

        Field().LoseKeyboard();
        Pump();

        Assert.False(box.IsFocused);
        Assert.Equal(1, lostFocus);
        Assert.True(Field().IsDisposed);
        window.Close();
    }

    [AvaloniaFact]
    public void The_next_box_takes_the_keyboard_before_the_last_ones_field_goes()
    {
        // The other way round, the keyboard would drop and come back up
        // between two boxes.
        var (window, first, second) = Show();
        first.Focus();
        Pump();

        second.Focus();
        Pump();

        Assert.Equal(["begin 0", "begin 1", "dispose 0"], _factory.Log);
        Assert.True(Field(1).IsEditing);
        window.Close();
    }

    [AvaloniaFact]
    public void A_box_the_user_cannot_change_opens_no_field()
    {
        var (window, box, _) = Show("hum");
        box.IsReadOnly = true;

        box.Focus();
        Pump();

        Assert.Empty(_factory.Created);
        window.Close();
    }
}
