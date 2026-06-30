using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flower.Persistence;

namespace Flower.Controls;

public class ColumnManager
{
    private readonly ColumnVisibilityStore _store;
    private Task? _pendingSave;

    public List<MusicColumnDefinition> Columns { get; }

    public event EventHandler? ColumnsChanged;

    public IEnumerable<MusicColumnDefinition> VisibleColumns =>
        Columns.Where(c => c.IsVisible).OrderBy(c => c.Order);

    public ColumnManager(ColumnVisibilityStore store)
    {
        _store = store;
        Columns = BuildDefaults();

        var saved = store.LoadColumnStates();
        if (saved != null && saved.Count > 0)
            ApplySaved(saved);

        foreach (var col in Columns)
            col.PropertyChanged += (_, _) => { ColumnsChanged?.Invoke(this, EventArgs.Empty); ScheduleSave(); };
    }

    private void ApplySaved(List<ColumnState> states)
    {
        foreach (var state in states)
        {
            var col = Columns.FirstOrDefault(c => c.Id == state.Id);
            if (col == null) continue;
            col.Width = state.Width;
            col.IsVisible = state.IsVisible;
            col.Order = state.Order;
        }
    }

    private void ScheduleSave()
    {
        _pendingSave = SaveAsync();
    }

    private async Task SaveAsync()
    {
        await Task.Delay(500);
        var states = Columns
            .Select(c => new ColumnState { Id = c.Id, IsVisible = c.IsVisible, Width = c.Width, Order = c.Order })
            .ToList();
        await _store.SaveColumnStatesAsync(states);
    }

    private static List<MusicColumnDefinition> BuildDefaults() =>
    [
        new("TrackNumber", "#",        40,  30, true, 0),
        new("Title",       "Title",   240,  60, true, 1),
        new("Artist",      "Artist",  180,  60, true, 2),
        new("Album",       "Album",   180,  60, true, 3),
        new("Year",        "Year",     60,  40, true, 4),
        new("Genre",       "Genre",   100,  60, true, 5),
        new("Duration",    "Duration", 80,  50, true, 6),
    ];
}
