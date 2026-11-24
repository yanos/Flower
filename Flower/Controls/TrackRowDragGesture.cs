using System.Collections.Generic;
using System.Linq;

using Avalonia;

using Flower.ViewModels;

namespace Flower.Controls;

/// <summary>
/// What a press, moves and a release on the track list mean: a click, rows
/// dragged to a new place in the list, or songs dragged out of it toward the
/// sidebar's playlists. MusicListView feeds it the pointer and acts on what
/// it says.
/// </summary>
/// <remarks>
/// Where the list can be reordered (a playlist, the queue), a drag that stays
/// over the list moves rows and one that leaves it carries songs out, so a
/// playlist's songs can be dropped on another playlist (UF-15). Either way it
/// is the whole selection that goes, in the order the list shows it, when the
/// row pressed is part of it.
/// </remarks>
public sealed class TrackRowDragGesture
{
    public const double Threshold = 4.0;

    public enum Mode
    {
        // Pressed and not yet moved far enough to be a drag - or not pressed.
        None,
        Reorder,
        Out,
    }

    private Point _start;
    private bool _pressed;
    private bool _canReorder;
    private bool _canDragOut;
    private bool _pastThreshold;

    public Mode Current { get; private set; }

    /// <summary>The rows a reorder moves, in the list's order.</summary>
    public IReadOnlyList<TrackRowViewModel> Rows { get; private set; } = [];

    /// <summary>Whether the press can become a drag at all.</summary>
    public bool IsPressed => _pressed;

    /// <summary>
    /// A left press on <paramref name="row"/>, once the selection has taken it.
    /// <paramref name="selectedInOrder"/> is the selection as the list shows
    /// it. A reorder needs the list to allow one; a drag out needs the row to
    /// be selected (a toggle that just deselected it starts nothing).
    /// </summary>
    public void Press(TrackRowViewModel row, Point at, bool allowReorder, IReadOnlyList<TrackRowViewModel> selectedInOrder)
    {
        End();
        _canReorder = allowReorder && row.CanBeDragged;
        _canDragOut = row.IsSelected;
        if (!_canReorder && !_canDragOut)
            return;

        _pressed = true;
        _start = at;
        Rows = row.IsSelected
            ? selectedInOrder.Where(r => r.CanBeDragged).ToList()
            : [row];
        if (Rows.Count == 0)
            Rows = [row];
    }

    /// <summary>
    /// The pointer at <paramref name="at"/>, over the list or not. Returns what
    /// the gesture is now.
    /// </summary>
    public Mode Move(Point at, bool overList)
    {
        if (!_pressed)
            return Mode.None;

        if (!_pastThreshold)
        {
            var dx = at.X - _start.X;
            var dy = at.Y - _start.Y;
            // A reorder starts on vertical movement alone, as it always has;
            // a drag out on movement any way.
            var far = _canReorder && overList
                ? System.Math.Abs(dy) >= Threshold
                : dx * dx + dy * dy >= Threshold * Threshold;
            if (!far)
                return Mode.None;
            _pastThreshold = true;
        }

        Current = _canReorder && (overList || !_canDragOut) ? Mode.Reorder
            : _canDragOut ? Mode.Out
            : Mode.None;
        return Current;
    }

    /// <summary>
    /// The row a reorder dropped at slot <paramref name="index"/> of
    /// <paramref name="items"/> goes before: the first there that is not
    /// itself moving, or null for the end of the list.
    /// </summary>
    public TrackRowViewModel? InsertBefore(IReadOnlyList<TrackRowViewModel> items, int index)
    {
        var moving = Rows.ToHashSet();
        for (var i = System.Math.Max(0, index); i < items.Count; i++)
        {
            if (!moving.Contains(items[i]))
                return items[i];
        }
        return null;
    }

    public void End()
    {
        _pressed = false;
        _pastThreshold = false;
        _canReorder = false;
        _canDragOut = false;
        Current = Mode.None;
        Rows = [];
    }
}
