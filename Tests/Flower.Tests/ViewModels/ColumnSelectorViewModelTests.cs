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
    public void Filtering_keeps_only_matching_columns_and_drops_empty_groups()
    {
        using var vm = New(NewManager());

        vm.Filter = "sort";

        var group = Assert.Single(vm.Groups);
        Assert.NotEmpty(group.Items);
        Assert.All(group.Items, item => Assert.Contains("sort", item.Header, StringComparison.OrdinalIgnoreCase));
        Assert.False(vm.NoMatch);
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
    public void Reset_restores_the_default_columns()
    {
        var manager = NewManager();
        var defaults = manager.VisibleColumns.Select(c => c.Id).ToArray();
        using var vm = New(manager);
        Shown(vm, "Genre").HideCommand.Execute(null);
        Shown(vm, "Year").MoveUpCommand.Execute(null);
        manager.Columns.Single(c => c.Id == "Composer").IsVisible = true;

        vm.ResetCommand.Execute(null);

        Assert.Equal(defaults, ShownIds(vm));
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
