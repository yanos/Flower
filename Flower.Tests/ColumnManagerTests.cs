using System.Linq;

using Flower.Controls;
using Flower.Persistence;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests;

// Reorder is the nontrivial algorithm here and had no tests
// (docs/ARCHITECTURE-REVIEW.md §5.7): it is expressed in *visible* indices,
// because that is what a header drag produces, but it has to renumber Order
// across hidden columns too - and a hidden column's relative position must
// survive a reorder that never mentions it.
//
// Every ColumnManager here is built over its own in-memory AppSettings, so
// nothing reads or writes the developer's real settings.json. The debounced
// save these mutations schedule is 500ms out and never awaited; the tests
// assert on in-memory state, and the store write is covered by StoreRoundTripTests.
[Collection("PlatformDataDirectory")]
public class ColumnManagerTests : PinnedDataDirectory
{
    private static ColumnManager New() => new(new AppSettings(), new AppSettingsStore());

    private static string[] VisibleIds(ColumnManager manager) =>
        manager.VisibleColumns.Select(c => c.Id).ToArray();

    private static MusicColumnDefinition Column(ColumnManager manager, string id) =>
        manager.Columns.Single(c => c.Id == id);

    [Fact]
    public void Columns_start_in_their_declared_default_order()
    {
        Assert.Equal(
            ["TrackNumber", "Title", "Artist", "Album", "Year", "Genre", "Duration"],
            VisibleIds(New()).Take(7));
    }

    [Fact]
    public void Reorder_moves_a_column_later_in_the_visible_sequence()
    {
        var manager = New();

        manager.Reorder(Column(manager, "Title"), newVisibleIndex: 3);

        Assert.Equal(["TrackNumber", "Artist", "Album", "Title", "Year"], VisibleIds(manager).Take(5));
    }

    [Fact]
    public void Reorder_moves_a_column_earlier_in_the_visible_sequence()
    {
        var manager = New();

        manager.Reorder(Column(manager, "Genre"), newVisibleIndex: 0);

        Assert.Equal(["Genre", "TrackNumber", "Title", "Artist", "Album"], VisibleIds(manager).Take(5));
    }

    [Fact]
    public void Reorder_to_a_columns_own_position_leaves_the_sequence_alone()
    {
        var manager = New();
        var before = VisibleIds(manager);

        manager.Reorder(Column(manager, "Album"), newVisibleIndex: 3);

        Assert.Equal(before, VisibleIds(manager));
    }

    [Fact]
    public void Reorder_past_the_end_puts_the_column_last()
    {
        var manager = New();
        var count = manager.VisibleColumns.Count();

        manager.Reorder(Column(manager, "Title"), newVisibleIndex: count + 10);

        Assert.Equal("Title", VisibleIds(manager)[^1]);
    }

    [Fact]
    public void Reorder_indices_count_visible_columns_only_and_skip_hidden_ones()
    {
        var manager = New();
        Column(manager, "Artist").IsVisible = false;

        // Visible sequence is now TrackNumber, Title, Album, ... - so visible
        // index 2 means "where Album is", not "where Artist is".
        manager.Reorder(Column(manager, "Genre"), newVisibleIndex: 2);

        Assert.Equal(["TrackNumber", "Title", "Genre", "Album", "Year"], VisibleIds(manager).Take(5));
    }

    [Fact]
    public void Reorder_leaves_a_hidden_column_where_it_was_relative_to_its_neighbours()
    {
        var manager = New();
        var hidden = Column(manager, "Year");
        hidden.IsVisible = false;

        manager.Reorder(Column(manager, "Genre"), newVisibleIndex: 0);

        // Year was between Album and Genre and stays there: Genre jumped over
        // it to the front, so Year now sits directly after Album.
        var all = manager.Columns.OrderBy(c => c.Order).Select(c => c.Id).ToArray();
        Assert.Equal(["Genre", "TrackNumber", "Title", "Artist", "Album", "Year"], all.Take(6));
    }

    [Fact]
    public void Reorder_renumbers_Order_into_a_contiguous_sequence()
    {
        var manager = New();

        manager.Reorder(Column(manager, "Duration"), newVisibleIndex: 1);

        Assert.Equal(
            Enumerable.Range(0, manager.Columns.Count),
            manager.Columns.OrderBy(c => c.Order).Select(c => c.Order));
    }

    [Fact]
    public void Hiding_or_reordering_a_column_raises_ColumnsChanged_but_resizing_does_not()
    {
        var manager = New();
        var raised = 0;
        manager.ColumnsChanged += (_, _) => raised++;

        // A resize drag fires Width changes continuously; rebuilding the header
        // on each one would destroy the header cell driving the drag.
        Column(manager, "Title").Width = 300;
        Assert.Equal(0, raised);

        Column(manager, "Genre").IsVisible = false;
        Assert.True(raised > 0);
    }
}
