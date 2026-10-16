using System;
using System.Collections.Generic;
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
using Flower.ViewModels.Mobile;
using Flower.Views.Mobile;

using Material.Icons.Avalonia;

using Xunit;

namespace Flower.Tests.ViewModels.Mobile;

// The Search tab's dropdown of suggestions: which past searches are kept,
// what is offered for what has been typed - past searches, then the library's
// artists, albums and songs - and that picking one searches for it.
// Pinned, because remembering a search saves settings.json.
[Collection("PlatformDataDirectory")]
public class MobileRecentSearchesTests : PinnedDataDirectory
{
    private static MainViewModelHarness.MobileParts Build()
    {
        var tracks = Enumerable.Range(0, 4).Select(i => new Track
        {
            Title = $"Track {i}", Path = $"/music/{i}.mp3", Album = $"Album {i}", Artists = $"Artist {i}",
        }).ToList();
        return MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
    }

    [AvaloniaFact]
    public void The_latest_search_goes_first_and_a_repeat_moves_rather_than_doubles()
    {
        using var parts = Build();
        var main = parts.Mobile.Main;

        main.RememberSearch("radiohead");
        main.RememberSearch("bjork");
        main.RememberSearch("  Radiohead ");

        Assert.Equal(new[] { "Radiohead", "bjork" }, main.RecentSearches);
    }

    [AvaloniaFact]
    public void Only_so_many_are_kept()
    {
        using var parts = Build();
        var main = parts.Mobile.Main;

        for (var i = 0; i < MainViewModel.MaxRecentSearches + 5; i++)
            main.RememberSearch($"query {i}");

        Assert.Equal(MainViewModel.MaxRecentSearches, main.RecentSearches.Count);
        Assert.Equal($"query {MainViewModel.MaxRecentSearches + 4}", main.RecentSearches[0]);
    }

    [AvaloniaFact]
    public void What_is_typed_narrows_the_suggestions_and_is_not_offered_back()
    {
        using var parts = Build();
        var vm = parts.Mobile;
        vm.Main.RememberSearch("the national");
        vm.Main.RememberSearch("radiohead");
        vm.Main.RememberSearch("nat king cole");
        vm.Main.RememberSearch("nat");

        vm.SelectTabCommand.Execute(nameof(MobileTab.Search));
        vm.SearchQuery = "nat";

        Assert.Equal(new[] { "nat king cole", "the national" }, vm.SearchSuggestions.Select(s => s.Text));
    }

    // Opening Search is the start of a search, not a list of old ones.
    [AvaloniaFact]
    public void An_empty_box_offers_nothing()
    {
        using var parts = Build();
        var vm = parts.Mobile;
        vm.Main.RememberSearch("radiohead");

        vm.SelectTabCommand.Execute(nameof(MobileTab.Search));

        Assert.Empty(vm.SearchSuggestions);
    }

    // After the past searches, the library: names that start with what is
    // typed before names with a later word that does, and each kind says
    // what it is.
    [AvaloniaFact]
    public void The_library_s_names_are_offered_after_past_searches()
    {
        var tracks = new List<Track>
        {
            new() { Title = "Wildfire", Path = "/music/1.mp3", Album = "The Wild Hunt", Artists = "Tallest Man" },
            new() { Title = "Sonata", Path = "/music/2.mp3", Album = "Classics", Artists = "Wilco" },
        };
        using var parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
        var vm = parts.Mobile;
        vm.Main.RememberSearch("wild nothing");

        vm.SelectTabCommand.Execute(nameof(MobileTab.Search));
        vm.SearchQuery = "wil";

        Assert.Equal(
            new[]
            {
                ("wild nothing", SearchSuggestionKind.Recent),
                ("Wilco", SearchSuggestionKind.Artist),
                ("Wildfire", SearchSuggestionKind.Song),
                ("The Wild Hunt", SearchSuggestionKind.Album),
            },
            vm.SearchSuggestions.Select(s => (s.Text, s.Kind)));
    }

    // Past searches are cleared from the Search screen's menu.
    [AvaloniaFact]
    public void The_search_menu_clears_past_searches()
    {
        using var parts = Build();
        var vm = parts.Mobile;
        vm.Main.RememberSearch("radiohead");
        vm.SelectTabCommand.Execute(nameof(MobileTab.Search));

        var clear = vm.BuildScreenMenu(vm.CurrentFrame).SelectMany(s => s.Entries)
            .Single(e => e.Label == "Clear Recent Searches");
        clear.Invoke();

        Assert.Empty(vm.Main.RecentSearches);
        Assert.DoesNotContain(vm.BuildScreenMenu(vm.CurrentFrame).SelectMany(s => s.Entries),
            e => e.Label == "Clear Recent Searches");
    }

    // The dropdown is only up while the box has focus, and a tap on a row
    // searches for it, remembers it - not the letters typed to find it - and
    // puts the keyboard away.
    [AvaloniaFact]
    public void Picking_a_suggestion_searches_for_it_and_closes_the_dropdown()
    {
        using var parts = Build();
        var vm = parts.Mobile;
        vm.Main.RememberSearch("radiohead");
        vm.Main.RememberSearch("bjork");

        var window = new Window { Width = 390, Height = 844 };
        window.Styles.Add(new FluentTheme());
        window.Styles.Add(new MaterialIconStyles(null));
        window.Content = new MobileMainView { DataContext = vm };
        window.Show();
        vm.SelectTabCommand.Execute(nameof(MobileTab.Search));
        Pump(400);

        // Opening the tab focuses the box, with nothing dropped under it until
        // something is typed.
        var box = window.GetVisualDescendants().OfType<TextBox>().Single(b => b.IsEffectivelyVisible);
        Assert.True(box.IsFocused);
        Assert.Null(Row(window, "radiohead"));

        box.Text = "rad";
        Pump(100);
        var row = Row(window, "radiohead");
        Assert.NotNull(row);
        Assert.Null(Row(window, "bjork"));

        var centre = row!.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Pump(100);

        Assert.Equal("radiohead", vm.SearchQuery);
        Assert.False(box.IsFocused);
        Assert.Null(Row(window, "radiohead"));
        Assert.Equal(new[] { "radiohead", "bjork" }, vm.Main.RecentSearches);

        Pump(300);
        window.Close();
    }

    // The empty state - "no results" for what is typed - is drawn over every
    // screen, so while the dropdown is open it steps aside rather than
    // painting across the rows.
    [AvaloniaFact]
    public void The_empty_state_steps_aside_while_the_dropdown_is_open()
    {
        using var parts = Build();
        var vm = parts.Mobile;
        vm.Main.RememberSearch("radiohead");

        var window = new Window { Width = 390, Height = 844 };
        window.Styles.Add(new FluentTheme());
        window.Content = new MobileMainView { DataContext = vm };
        window.Show();
        vm.SelectTabCommand.Execute(nameof(MobileTab.Search));
        Pump(400);

        var box = window.GetVisualDescendants().OfType<TextBox>().Single(b => b.IsEffectivelyVisible);
        box.Text = "rad";
        PumpUntil(() => vm.IsContentEmpty && !vm.IsShowingSearchPrompt && Row(window, "radiohead") != null, 5000);

        // The empty state's words, whatever they say for this query.
        Control Prompt() => (Control)window.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "EmptyStateHost")
            .GetVisualChildren().First();
        Assert.NotNull(Row(window, "radiohead"));
        Assert.False(Prompt().IsEffectivelyVisible);

        window.FocusManager!.Focus(null);

        // Waited for rather than pumped a fixed 100ms: a CI iOS simulator under
        // the interpreter had not laid the prompt back out by then.
        PumpUntil(() => Row(window, "radiohead") == null && Prompt().IsEffectivelyVisible, 5000);

        Pump(300);
        window.Close();
    }

    // Leaving the box with something typed in it is a search of its own.
    [AvaloniaFact]
    public void Leaving_the_box_remembers_what_it_held()
    {
        using var parts = Build();
        var vm = parts.Mobile;

        var window = new Window { Width = 390, Height = 844 };
        window.Styles.Add(new FluentTheme());
        window.Content = new MobileMainView { DataContext = vm };
        window.Show();
        vm.SelectTabCommand.Execute(nameof(MobileTab.Search));
        Pump(400);

        var box = window.GetVisualDescendants().OfType<TextBox>().Single(b => b.IsEffectivelyVisible);
        box.Focus();
        box.Text = "portishead";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Pump(100);

        Assert.False(box.IsFocused);
        Assert.Equal(new[] { "portishead" }, vm.Main.RecentSearches);

        Pump(300);
        window.Close();
    }

    // A visible dropdown row for this search, or null.
    private static Button? Row(Window window, string query) =>
        window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => (b.DataContext as SearchSuggestion)?.Text == query && b.IsEffectivelyVisible);

    private static void Pump(int milliseconds)
    {
        using var cts = new CancellationTokenSource(milliseconds);
        Dispatcher.UIThread.MainLoop(cts.Token);
    }

    private static void PumpUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
            Pump(20);
        Assert.True(condition(), "the expected layout never settled");
    }
}
