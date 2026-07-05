using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Flower.Persistence;

namespace Flower.Controls;

public class ColumnManager
{
    private readonly AppSettings _appSettings;
    private Task? _pendingSave;

    public List<MusicColumnDefinition> Columns { get; }

    public event EventHandler? ColumnsChanged;

    public IEnumerable<MusicColumnDefinition> VisibleColumns =>
        Columns.Where(c => c.IsVisible).OrderBy(c => c.Order);

    public ColumnManager(AppSettings appSettings)
    {
        _appSettings = appSettings;
        Columns = BuildDefaults();

        var saved = appSettings.ColumnStates;
        if (saved != null && saved.Count > 0)
            ApplySaved(saved);

        foreach (var col in Columns)
            col.PropertyChanged += (_, e) =>
            {
                // Width changes are already reflected live via each header
                // cell's own binding (see MusicListView.MakeHeaderCell) and each
                // row cell's direct width-sync subscription (see
                // TrackRowControl.BuildCells) - firing ColumnsChanged here too
                // would make MusicListView's ColumnsChanged handler rebuild the
                // whole header on every pixel of a resize drag, destroying and
                // replacing the very header cell (and its resize handle) whose
                // pointer capture is driving that drag, killing the gesture
                // after its first tiny movement. IsVisible/Order changes still
                // need the rebuild since those change which cells exist or
                // their sequence.
                if (e.PropertyName != nameof(MusicColumnDefinition.Width))
                    ColumnsChanged?.Invoke(this, EventArgs.Empty);
                ScheduleSave();
            };
    }

    // Moves `column` so it becomes the `newVisibleIndex`-th visible column
    // (other visible columns shifting to make room), then renumbers every
    // column's Order to match - hidden columns keep their existing relative
    // position among the ones that didn't move. Persisted the same way any
    // other column-state change is, via the PropertyChanged/ScheduleSave hookup
    // in the constructor.
    public void Reorder(MusicColumnDefinition column, int newVisibleIndex)
    {
        var ordered = Columns.OrderBy(c => c.Order).ToList();
        ordered.Remove(column);

        int insertAt = ordered.Count;
        int visibleSeen = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            if (!ordered[i].IsVisible)
                continue;
            if (visibleSeen == newVisibleIndex)
            {
                insertAt = i;
                break;
            }
            visibleSeen++;
        }

        ordered.Insert(insertAt, column);

        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Order != i)
                ordered[i].Order = i;
        }
    }

    private void ApplySaved(List<ColumnState> states)
    {
        foreach (var state in states)
        {
            var col = Columns.FirstOrDefault(c => c.Id == state.Id);
            if (col == null)
                continue;
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
        _appSettings.ColumnStates = BuildStates();
        await new AppSettingsStore().SaveAsync(_appSettings);
    }

    // Synchronous, immediate counterpart to the debounced SaveAsync above - for
    // the Window.Closing handler, where the process may exit before a pending
    // debounced save's Task.Delay(500) completes, silently losing a resize (or
    // reorder/hide) made shortly before quitting.
    public void Flush()
    {
        _appSettings.ColumnStates = BuildStates();
        new AppSettingsStore().Save(_appSettings);
    }

    private List<ColumnState> BuildStates() =>
        Columns
            .Select(c => new ColumnState { Id = c.Id, IsVisible = c.IsVisible, Width = c.Width, Order = c.Order })
            .ToList();

    private static List<MusicColumnDefinition> BuildDefaults() =>
    [
        new("TrackNumber", "#",        40,  30, true, 0),
        new("Title",       "Title",   240,  60, true, 1),
        new("Artist",      "Artist",  180,  60, true, 2),
        new("Album",       "Album",   180,  60, true, 3),
        new("Year",        "Year",     60,  40, true, 4),
        new("Genre",       "Genre",   100,  60, true, 5),
        new("Duration",    "Duration", 80,  50, true, 6),
        new("PlayCount",   "Plays",    55,  40, true, 7),
        new("DateAdded",   "Added",   100,  70, true, 8),
    ];
}
