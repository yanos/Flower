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

namespace Flower.Tests;

// The Search tab's dropdown of past searches: which are kept, which are
// offered for what has been typed, and that picking one searches for it.
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

        Assert.Equal(new[] { "nat king cole", "the national" }, vm.SearchSuggestions);
    }

    [AvaloniaFact]
    public void An_empty_box_offers_the_latest_ones()
    {
        using var parts = Build();
        var vm = parts.Mobile;
        for (var i = 0; i < 10; i++)
            vm.Main.RememberSearch($"query {i}");

        vm.SelectTabCommand.Execute(nameof(MobileTab.Search));

        Assert.Equal(MobileMainViewModel.MaxSearchSuggestions, vm.SearchSuggestions.Count);
        Assert.Equal("query 9", vm.SearchSuggestions[0]);
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

        // Opening the tab focuses the box, so the empty box's dropdown - every
        // recent search - is already up.
        var box = window.GetVisualDescendants().OfType<TextBox>().Single(b => b.IsEffectivelyVisible);
        Assert.True(box.IsFocused);
        Assert.NotNull(Row(window, "radiohead"));
        Assert.NotNull(Row(window, "bjork"));

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

    // Clear forgets every past search - the ones the typing had filtered out
    // as well - and leaves the box focused, since whatever was being typed is
    // still being typed.
    [AvaloniaFact]
    public void Clear_forgets_them_all_and_keeps_the_keyboard_up()
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

        var box = window.GetVisualDescendants().OfType<TextBox>().Single(b => b.IsEffectivelyVisible);
        box.Text = "rad";
        Pump(100);

        var clear = window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.IsEffectivelyVisible && b.Content is TextBlock { Text: "Clear" });
        var centre = clear.TranslatePoint(new Point(clear.Bounds.Width / 2, clear.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Pump(100);

        Assert.Empty(vm.Main.RecentSearches);
        Assert.False(clear.IsEffectivelyVisible);
        Assert.True(box.IsFocused);
        Assert.Equal("rad", box.Text);

        Pump(300);
        window.Close();
    }

    // The search prompt is drawn over every screen, so while the dropdown is
    // open it steps aside rather than painting across the rows.
    [AvaloniaFact]
    public void The_search_prompt_steps_aside_while_the_dropdown_is_open()
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

        TextBlock Prompt() => window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == vm.EmptyStateTitle);
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
            .FirstOrDefault(b => b.DataContext as string == query && b.IsEffectivelyVisible);

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
