using System.Linq;

using Flower.Controls;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels;

// Reorder is the nontrivial algorithm here and had no tests
// (docs/ARCHITECTURE-REVIEW.md §5.7): it is expressed in *visible* indices,
// because that is what a header drag produces, but it has to renumber Order
// across hidden columns too - and a hidden column's relative position must
// survive a reorder that never mentions it.
//
// Every ColumnManager here is built over its own in-memory AppSettings, so
// nothing reads or writes the developer's real settings.json. The debounced
// save these mutations schedule is 500ms out and never awaited; the tests
// assert on in-memory state, and the store write is covered by AppSettingsStoreTests.
[Collection("PlatformDataDirectory")]
public class ColumnManagerTests : PinnedDataDirectory
{
    private static ColumnManager New() => new(new AppSettings(), new AppSettingsStore());

    private static string[] VisibleIds(ColumnManager manager) =>
        manager.VisibleColumns.Select(c => c.Id).ToArray();

    private static MusicColumnDefinition Column(ColumnManager manager, string id) =>
        manager.Columns.Single(c => c.Id == id);

    [Fact]
    public void Reorder_moves_a_column_later_in_the_visible_sequence()
    {
        var manager = New();

        manager.Reorder(Column(manager, "Title"), newVisibleIndex: 3);

        Assert.Equal(["TrackNumber", "Artist", "Album", "Title", "Year"], VisibleIds(manager).Take(5));
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

    // Title was hideable before it was locked; a settings file that hid it
    // must not leave it hidden with nothing able to show it again.
    [Fact]
    public void A_saved_hidden_title_comes_back_shown()
    {
        var settings = new AppSettings
        {
            ColumnStates = [new ColumnState { Id = "Title", IsVisible = false, Width = 240, Order = 1 }],
        };

        var manager = new ColumnManager(settings, new AppSettingsStore());

        Assert.True(Column(manager, "Title").IsVisible);
    }
}
