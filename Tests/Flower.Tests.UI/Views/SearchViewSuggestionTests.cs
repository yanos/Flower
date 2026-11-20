using System;
using System.Linq;
using System.Threading;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.Views;

using Xunit;

namespace Flower.Tests.Views;

// The desktop Search page's box, an AutoCompleteBox over the suggestions
// SearchViewModel works out. Picking one puts it in the box, which is a new
// query and so a new list of suggestions a moment later - and the box drops
// its picked row when the list it came from goes, putting back what had been
// typed. Only the real control shows that.
[Collection("PlatformDataDirectory")]
public class SearchViewSuggestionTests : PinnedDataDirectory
{
    private sealed class Harness : IDisposable
    {
        public Window Window { get; }
        public AutoCompleteBox Box { get; }
        public SearchViewModel Search => _parts.Main.Search;

        private readonly MainViewModelHarness.Parts _parts;

        public Harness()
        {
            var tracks = new[]
            {
                new Track { Title = "Bemsha Swing", Album = "Monk's Dream", Artists = "Thelonious Monk", Path = "/music/1.mp3" },
                new Track { Title = "Monk's Mood", Album = "Thelonious Himself", Artists = "Thelonious Monk", Path = "/music/2.mp3" },
                new Track { Title = "Money Jungle", Album = "Money Jungle", Artists = "Duke Ellington", Path = "/music/3.mp3" },
            }.ToList();
            _parts = MainViewModelHarness.Build(new Library(tracks), new MainPlaylist(tracks));

            var view = new SearchView { DataContext = _parts.Main };
            // No theme in the test app, and without one the box has no
            // template - no text box to type in, no dropdown.
            Window = new Window { Width = 1000, Height = 700 };
            Window.Styles.Add(new FluentTheme());
            Window.Content = view;
            Window.Show();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Box = view.FindControl<AutoCompleteBox>("Box")!;
        }

        public static void Pump(int milliseconds = 400)
        {
            using var cts = new CancellationTokenSource(milliseconds);
            Dispatcher.UIThread.MainLoop(cts.Token);
        }

        // Until SearchViewModel has made a list since `before` and the box
        // has what it should of it: rather than a fixed wait, which on the
        // iOS simulator's interpreter ran out before the list arrived.
        public void PumpUntilSuggested(System.Collections.Generic.IReadOnlyList<SearchSuggestion> before, bool boxTakesIt = true)
        {
            var deadline = Environment.TickCount64 + 10_000;
            while (!(!ReferenceEquals(Search.Suggestions, before)
                     && (!boxTakesIt || ReferenceEquals(Box.ItemsSource, Search.Suggestions))))
            {
                Assert.True(Environment.TickCount64 < deadline, "no new suggestions arrived");
                Pump(20);
            }
        }

        // A key typed, then its list in the box.
        public void Type(string text)
        {
            var before = Search.Suggestions;
            Window.KeyTextInput(text);
            PumpUntilSuggested(before);
        }

        public void Dispose()
        {
            Window.Close();
            _parts.Dispose();
        }
    }

    private static Harness Typed(string text)
    {
        var h = new Harness();
        try
        {
            // The text box inside it, which is what keys reach.
            h.Box.GetVisualDescendants().OfType<TextBox>().First().Focus();
            // A key at a time, as typed.
            foreach (var key in text)
                h.Type(key.ToString());
            Assert.True(h.Box.IsDropDownOpen);
            return h;
        }
        catch
        {
            h.Dispose();
            throw;
        }
    }

    [AvaloniaFact]
    public void A_clicked_suggestion_stays_in_the_box()
    {
        using var h = Typed("mon");
        // The dropdown is a popup of its own, outside the window's tree.
        var popup = h.Box.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().Single();
        var row = popup.Child!.GetVisualDescendants().OfType<ListBoxItem>().ElementAt(1);
        var picked = ((SearchSuggestion)row.DataContext!).Text;
        var root = TopLevel.GetTopLevel(row)!;
        var at = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), root)!.Value;

        root.MouseDown(at, MouseButton.Left);
        Harness.Pump();
        root.MouseUp(at, MouseButton.Left);
        Harness.Pump();

        Assert.Equal(picked, h.Box.Text);
        Assert.Equal(picked, h.Search.Query);
    }

    [AvaloniaFact]
    public void A_suggestion_picked_with_the_keys_stays_in_the_box()
    {
        using var h = Typed("mon");
        var picked = h.Search.Suggestions[0].Text;
        var before = h.Search.Suggestions;

        h.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        h.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        // The pick's own list, which must not take it back out.
        h.PumpUntilSuggested(before, boxTakesIt: false);
        Harness.Pump();

        Assert.Equal(picked, h.Box.Text);
        Assert.Equal(picked, h.Search.Query);
    }

    // A row walked to on the list on show before the one for the last key
    // arrived: that list came for the walk instead, while it was still a
    // walk, and dropped the row - putting back what was typed. Enter then
    // searched for that. The iOS simulator was slow enough to do this to the
    // test above.
    [AvaloniaFact]
    public void A_row_walked_to_before_the_last_keys_list_arrives_stays_in_the_box()
    {
        using var h = Typed("mo");
        var picked = ((SearchSuggestion)h.Box.ItemsSource!.Cast<object>().First()).Text;
        var before = h.Search.Suggestions;

        h.Window.KeyTextInput("n");
        h.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        h.PumpUntilSuggested(before, boxTakesIt: false);
        Harness.Pump();
        h.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Harness.Pump();

        Assert.Equal(picked, h.Box.Text);
        Assert.Equal(picked, h.Search.Query);
    }

    // Walking the dropdown with the arrows shows each row in the box as it
    // goes; the list must not be swapped out from under the walk.
    [AvaloniaFact]
    public void Walking_the_suggestions_keeps_the_row_walked_to()
    {
        using var h = Typed("mon");
        var second = h.Search.Suggestions[1].Text;

        h.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Harness.Pump();
        h.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Harness.Pump();

        Assert.Equal(second, h.Box.Text);
        Assert.Equal(second, h.Search.Query);
    }

    // Leaving the box remembers the search (SearchView.Box_LostFocus), which
    // refreshes the suggestions - one more new list, which must not take the
    // pick back out of the box either.
    [AvaloniaFact]
    public void Remembering_a_pick_keeps_it_in_the_box()
    {
        using var h = Typed("mon");
        var picked = h.Search.Suggestions[0].Text;
        h.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        h.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Harness.Pump();

        h.Search.Remember();
        Harness.Pump();

        Assert.Equal(picked, h.Box.Text);
        Assert.Equal(picked, h.Search.RecentSearches[0]);
    }

    // Escape out of a walk is the box's own way back to what was typed, and
    // has to keep working.
    [AvaloniaFact]
    public void Escape_from_a_walk_puts_back_what_was_typed()
    {
        using var h = Typed("mon");

        h.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Harness.Pump();
        h.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Harness.Pump();

        Assert.Equal("mon", h.Box.Text);
        Assert.Equal("mon", h.Search.Query);
    }

    // After a pick, typing on carries on from it, and the list follows.
    [AvaloniaFact]
    public void Typing_after_a_pick_suggests_for_the_new_words()
    {
        using var h = Typed("mon");
        h.Window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        h.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Harness.Pump();
        h.Box.Text = "";
        Harness.Pump();

        h.Type("duke");

        Assert.Equal("duke", h.Box.Text);
        Assert.Contains(h.Box.ItemsSource!.Cast<SearchSuggestion>(), s => s.Text == "Duke Ellington");
    }
}
