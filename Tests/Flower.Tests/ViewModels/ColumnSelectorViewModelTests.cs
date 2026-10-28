using System.Linq;

using Flower.Controls;
using Flower.Persistence;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels;

// The Columns window's behaviour, on its view model. Every ColumnManager here
// is built over its own in-memory AppSettings; the debounced save the
// mutations schedule is never awaited (see ColumnManagerTests).
[Collection("PlatformDataDirectory")]
public class ColumnSelectorViewModelTests : PinnedDataDirectory
{
    private static ColumnManager NewManager() => new(new AppSettings(), new AppSettingsStore());

    private static ColumnSelectorViewModel New(ColumnManager manager) => new(manager, () => false, _ => { });

    private static string[] ShownIds(ColumnSelectorViewModel vm) =>
        vm.Shown.Select(s => s.Column.Id).ToArray();

    private static ShownColumn Shown(ColumnSelectorViewModel vm, string id) =>
        vm.Shown.Single(s => s.Column.Id == id);

    [Fact]
    public void Every_column_is_offered_in_exactly_one_group()
    {
        var manager = NewManager();
        using var vm = New(manager);

        var offered = vm.Groups.SelectMany(g => g.Items).Select(o => o.Column.Id).ToList();

        Assert.Equal(manager.Columns.Select(c => c.Id).OrderBy(id => id), offered.OrderBy(id => id));
    }

    [Fact]
    public void The_shown_list_follows_the_visible_columns_in_order()
    {
        var manager = NewManager();
        using var vm = New(manager);

        Assert.Equal(manager.VisibleColumns.Select(c => c.Id), ShownIds(vm));
    }

    [Fact]
    public void Filtering_keeps_only_matching_columns_and_drops_empty_groups()
    {
        using var vm = New(NewManager());

        vm.Filter = "sort";

        var group = Assert.Single(vm.Groups);
        Assert.Equal("Sorting", group.Name);
        Assert.False(vm.NoMatch);
    }

    [Fact]
    public void A_filter_matching_nothing_says_so()
    {
        using var vm = New(NewManager());

        vm.Filter = "zzz";

        Assert.Empty(vm.Groups);
        Assert.True(vm.NoMatch);
    }

    [Fact]
    public void Moving_a_column_down_swaps_it_with_the_next_one()
    {
        using var vm = New(NewManager());

        Shown(vm, "Artist").MoveDownCommand.Execute(null);

        Assert.Equal(["TrackNumber", "Title", "Album", "Artist", "Year"], ShownIds(vm).Take(5));
    }

    [Fact]
    public void Moving_a_column_up_swaps_it_with_the_previous_one()
    {
        using var vm = New(NewManager());

        Shown(vm, "Album").MoveUpCommand.Execute(null);

        Assert.Equal(["TrackNumber", "Title", "Album", "Artist", "Year"], ShownIds(vm).Take(5));
    }

    [Fact]
    public void The_ends_of_the_shown_list_cannot_move_past_them()
    {
        using var vm = New(NewManager());

        Assert.False(vm.Shown[0].MoveUpCommand.CanExecute(null));
        Assert.False(vm.Shown[^1].MoveDownCommand.CanExecute(null));
    }

    [Fact]
    public void Hiding_a_column_takes_it_out_of_the_shown_list()
    {
        var manager = NewManager();
        using var vm = New(manager);

        Shown(vm, "Genre").HideCommand.Execute(null);

        Assert.DoesNotContain("Genre", ShownIds(vm));
        Assert.False(manager.Columns.Single(c => c.Id == "Genre").IsVisible);
    }

    [Fact]
    public void Title_cannot_be_hidden_from_either_side()
    {
        using var vm = New(NewManager());

        Assert.False(Shown(vm, "Title").HideCommand.CanExecute(null));
        Assert.False(vm.Groups.SelectMany(g => g.Items).Single(o => o.Column.Id == "Title").CanToggle);
    }

    [Fact]
    public void Reset_restores_the_default_columns_and_the_album_art()
    {
        var manager = NewManager();
        var defaults = manager.VisibleColumns.Select(c => c.Id).ToArray();
        using var vm = New(manager);
        Shown(vm, "Genre").HideCommand.Execute(null);
        Shown(vm, "Year").MoveUpCommand.Execute(null);
        manager.Columns.Single(c => c.Id == "Composer").IsVisible = true;
        vm.ShowAlbumArt = false;

        vm.ResetCommand.Execute(null);

        Assert.Equal(defaults, ShownIds(vm));
        Assert.True(vm.ShowAlbumArt);
    }

    [Fact]
    public void Disposing_stops_following_the_manager()
    {
        var manager = NewManager();
        var vm = New(manager);
        var before = vm.Shown;

        vm.Dispose();
        manager.Columns.Single(c => c.Id == "Genre").IsVisible = false;

        Assert.Same(before, vm.Shown);
    }
}
