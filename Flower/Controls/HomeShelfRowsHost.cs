using System;

using Avalonia.Controls;

using Flower.ViewModels;

namespace Flower.Controls;

// Feeds a Home view's virtualizing list its rows (HomeShelfRows): works out
// the column count from the list's own width, and re-chunks the shelves when
// that count changes or Home rebuilds them - not on every pixel of a resize,
// which between two column counts only re-sizes the rows already on screen.
//
// The list's DataContext is the HomeViewModel. Each rebuild hands the list a
// new collection rather than refilling one: Avalonia keys a source's view on
// the instance, and a virtualizing panel handed the same one back can keep
// containers it had realized - see AlbumGridView.RebuildRows.
public sealed class HomeShelfRowsHost
{
    private readonly ItemsControl _list;
    private readonly Func<double, int> _columnsFor;
    private HomeViewModel? _home;
    private int _columns;

    public HomeShelfRowsHost(ItemsControl list, Func<double, int> columnsFor)
    {
        _list = list;
        _columnsFor = columnsFor;
        list.SizeChanged += (_, e) =>
        {
            var columns = _columnsFor(e.NewSize.Width);
            if (columns != _columns)
                Rebuild(columns);
        };
        list.DataContextChanged += (_, _) => Attach(list.DataContext as HomeViewModel);
        Attach(list.DataContext as HomeViewModel);
    }

    // The desktop's column count: as many of the Albums grid's cells as fit
    // across, at least one - AlbumGridView's own arithmetic.
    public static int DesktopColumnsFor(double width, double cellWidth) =>
        Math.Max(1, double.IsFinite(width) && width > 0 ? (int)(width / cellWidth) : 1);

    private void Attach(HomeViewModel? home)
    {
        if (ReferenceEquals(home, _home))
            return;
        if (_home != null)
            _home.ShelvesChanged -= OnShelvesChanged;
        _home = home;
        if (_home != null)
            _home.ShelvesChanged += OnShelvesChanged;
        Rebuild(_columns > 0 ? _columns : _columnsFor(_list.Bounds.Width));
    }

    private void OnShelvesChanged(object? sender, EventArgs e) => Rebuild(_columns > 0 ? _columns : _columnsFor(_list.Bounds.Width));

    private void Rebuild(int columns)
    {
        _columns = columns;
        _list.ItemsSource = _home == null ? null : HomeShelfRows.Build(_home, columns);
    }
}
