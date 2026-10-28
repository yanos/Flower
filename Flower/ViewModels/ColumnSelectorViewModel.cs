using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

using Flower.Controls;

namespace Flower.ViewModels;

// Backs the Columns window (View > Select Columns…), laid out from the
// October 2026 redesign's Columns board: the shown columns down the left in
// the order the list draws them, each movable and hideable, and every column
// there is on the right, grouped and filterable, as checkboxes.
//
// Everything here writes straight through to the ColumnManager, which owns and
// persists the state - there is no Apply, as there is none anywhere else in
// the app. The window lives as long as it is open; Dispose drops the
// ColumnsChanged subscription, because a subscriber to the app-wide manager
// that outlives its window is exactly the leak MusicListView once had.
public sealed class ColumnSelectorViewModel : ViewModelBase, IDisposable
{
    // The board's grouping, in the board's order. A column missing from every
    // group lands in "Other" rather than vanishing from the window, so adding
    // one to ColumnManager.BuildDefaults never makes it unreachable here.
    private static readonly (string Name, string[] Ids)[] GroupLayout =
    [
        ("Essentials", ["TrackNumber", "Title", "Artist", "Album", "AlbumArtist", "Duration", "Year", "Genre", "Disc"]),
        ("Listening",  ["PlayCount", "DateAdded", "LastPlayed", "Starred", "VolumeAdjustment", "ResumePosition", "RememberPosition", "SkipInShuffle", "Compilation"]),
        ("Credits",    ["Composer", "Conductor", "RemixedBy", "Publisher", "Copyright", "Grouping"]),
        ("Details",    ["Subtitle", "Description", "Comment", "Bpm", "InitialKey", "Isrc"]),
        ("Sorting",    ["SortTitle", "SortArtist", "SortAlbum", "SortComposer"]),
        ("File",       ["Codec", "Encoding", "Bitrate", "SampleRate", "BitDepth", "Channels", "Location"]),
    ];

    private readonly ColumnManager _columns;
    private readonly Func<bool> _getSortByYear;
    private readonly Action<bool> _setSortByYear;
    private readonly IReadOnlyList<ColumnGroup> _allGroups;
    private string _filter = "";
    private IReadOnlyList<ColumnGroup> _groups;
    private IReadOnlyList<ShownColumn> _shown = [];

    // Sort-by-year is the browser's setting (MainViewModel's, persisted
    // there), handed in as a getter and setter so the whole window binds to
    // this one view model. It used to be a second DataContext on its switch,
    // and setting the two in the wrong order crashed the window on open.
    public ColumnSelectorViewModel(ColumnManager columns, Func<bool> getSortByYear, Action<bool> setSortByYear)
    {
        _columns = columns;
        _getSortByYear = getSortByYear;
        _setSortByYear = setSortByYear;
        _allGroups = BuildGroups(columns);
        _groups = _allGroups;
        ResetCommand = new RelayCommand(columns.ResetToDefaults);
        RebuildShown();
        columns.ColumnsChanged += OnColumnsChanged;
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value ?? ""))
                return;
            Groups = ApplyFilter(_filter);
            OnPropertyChanged(nameof(NoMatch));
        }
    }

    public IReadOnlyList<ColumnGroup> Groups
    {
        get => _groups;
        private set => SetProperty(ref _groups, value);
    }

    public bool NoMatch => Groups.Count == 0;

    public IReadOnlyList<ShownColumn> Shown
    {
        get => _shown;
        private set => SetProperty(ref _shown, value);
    }

    public string ShownCount => Shown.Count == 1 ? "1 column" : $"{Shown.Count} columns";

    public bool ShowAlbumArt
    {
        get => _columns.ShowAlbumArt;
        set => _columns.ShowAlbumArt = value;
    }

    public bool SortArtistAlbumsByYear
    {
        get => _getSortByYear();
        set
        {
            if (_getSortByYear() == value)
                return;
            _setSortByYear(value);
            OnPropertyChanged();
        }
    }

    public ICommand ResetCommand { get; }

    public void Dispose() => _columns.ColumnsChanged -= OnColumnsChanged;

    private void OnColumnsChanged(object? sender, EventArgs e)
    {
        RebuildShown();
        OnPropertyChanged(nameof(ShowAlbumArt));
    }

    private void RebuildShown()
    {
        var visible = _columns.VisibleColumns.ToList();
        Shown = visible
            .Select((column, i) => new ShownColumn(
                column,
                canMoveUp: i > 0,
                canMoveDown: i < visible.Count - 1,
                moveBy: delta => _columns.Reorder(column, i + delta)))
            .ToList();
        OnPropertyChanged(nameof(ShownCount));
    }

    private IReadOnlyList<ColumnGroup> ApplyFilter(string filter)
    {
        var q = filter.Trim();
        if (q.Length == 0)
            return _allGroups;
        return _allGroups
            .Select(g => new ColumnGroup(g.Name, g.Items.Where(o => o.Header.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList()))
            .Where(g => g.Items.Count > 0)
            .ToList();
    }

    private static IReadOnlyList<ColumnGroup> BuildGroups(ColumnManager columns)
    {
        var byId = columns.Columns.ToDictionary(c => c.Id);
        var groups = GroupLayout
            .Select(g => new ColumnGroup(g.Name, g.Ids.Where(byId.ContainsKey).Select(id => new ColumnOption(byId[id])).ToList()))
            .ToList();

        var placed = GroupLayout.SelectMany(g => g.Ids).ToHashSet();
        var other = columns.Columns.Where(c => !placed.Contains(c.Id)).Select(c => new ColumnOption(c)).ToList();
        if (other.Count > 0)
            groups.Add(new ColumnGroup("Other", other));

        return groups.Where(g => g.Items.Count > 0).ToList();
    }
}

public sealed record ColumnGroup(string Name, IReadOnlyList<ColumnOption> Items)
{
    public string Heading => Name.ToUpperInvariant();
}

// A checkbox on the right. Bound to the column's own IsVisible, so it stays
// put while the shown list beside it is rebuilt around every change.
public sealed class ColumnOption(MusicColumnDefinition column)
{
    public MusicColumnDefinition Column { get; } = column;
    public string Header => Column.Header;
    public bool IsLocked => ColumnManager.IsAlwaysShown(Column);
    public bool CanToggle => !IsLocked;
}

// A row on the left. Rebuilt whenever the columns change, so CanMoveUp and
// CanMoveDown are fixed for its lifetime.
public sealed class ShownColumn
{
    public ShownColumn(MusicColumnDefinition column, bool canMoveUp, bool canMoveDown, Action<int> moveBy)
    {
        Column = column;
        CanMoveUp = canMoveUp;
        CanMoveDown = canMoveDown;
        MoveUpCommand = new RelayCommand(() => moveBy(-1), () => CanMoveUp);
        MoveDownCommand = new RelayCommand(() => moveBy(1), () => CanMoveDown);
        HideCommand = new RelayCommand(() => Column.IsVisible = false, () => CanHide);
    }

    public MusicColumnDefinition Column { get; }
    public string Header => Column.Header;
    public bool IsLocked => ColumnManager.IsAlwaysShown(Column);
    public bool CanHide => !IsLocked;
    public bool CanMoveUp { get; }
    public bool CanMoveDown { get; }

    public string MoveUpLabel => $"Move {Header} up";
    public string MoveDownLabel => $"Move {Header} down";
    public string HideLabel => $"Hide {Header}";

    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand HideCommand { get; }
}
