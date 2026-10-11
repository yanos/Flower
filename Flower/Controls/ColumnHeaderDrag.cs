using System;
using System.Linq;

namespace Flower.Controls;

/// <summary>
/// What a left-button press on one of the track list's column headers turns
/// into: a click, which sorts by that column, or - once it has moved further
/// than <see cref="DragThreshold"/> - a drag, which moves the column to the gap
/// it is released over. <see cref="MusicListView"/> owns the pointer, the
/// capture and the drop indicator; this owns the decision, and where the gaps
/// are, and is testable without a control.
/// </summary>
/// <remarks>
/// Positions are along the header as it is laid out right now, which mid-drag
/// still shows the dragged column in its original slot - only the indicator
/// moves; the cells do not rearrange until the drop. So the gaps are measured
/// with the dragged column included, and converted to ColumnManager.Reorder's
/// convention (dragged column excluded) only for the result.
/// </remarks>
public sealed class ColumnHeaderDrag(ColumnManager columns)
{
    /// <summary>How far a press must move, in pixels, before it is a drag.</summary>
    public const double DragThreshold = 4.0;

    private double _startX;

    /// <summary>The column being pressed or dragged, or null between gestures.</summary>
    public MusicColumnDefinition? Column { get; private set; }

    public bool IsDragging { get; private set; }

    public void Press(MusicColumnDefinition column, double x)
    {
        Column = column;
        _startX = x;
        IsDragging = false;
    }

    /// <summary>
    /// The pointer has moved to <paramref name="x"/>. Returns where the drop
    /// indicator goes, or null while the press is still a click.
    /// </summary>
    public double? Move(double x)
    {
        if (Column == null)
            return null;
        if (!IsDragging)
        {
            if (Math.Abs(x - _startX) < DragThreshold)
                return null;
            IsDragging = true;
        }
        return GapX(GapIndexAt(x));
    }

    /// <summary>
    /// The press ended at <paramref name="x"/>: a sort, a reorder, or nothing
    /// if no press was in progress. Ends the gesture either way.
    /// </summary>
    public ColumnHeaderGesture? Release(double x)
    {
        if (Column is not { } column)
            return null;

        ColumnHeaderGesture result = IsDragging
            ? new ColumnHeaderGesture.Reorder(column, ExcludingIndex(GapIndexAt(x), column))
            : new ColumnHeaderGesture.Sort(column.Id);
        End();
        return result;
    }

    public void End()
    {
        Column = null;
        IsDragging = false;
    }

    // Gap index (0..VisibleColumns.Count) that `x` falls into: before a column
    // until past its midpoint.
    private int GapIndexAt(double x)
    {
        double cursor = columns.ArtColumnWidth;
        var visible = columns.VisibleColumns.ToList();
        for (int i = 0; i < visible.Count; i++)
        {
            if (x < cursor + visible[i].Width / 2)
                return i;
            cursor += visible[i].Width;
        }
        return visible.Count;
    }

    // Where a gap begins.
    private double GapX(int gapIndex)
    {
        double cursor = columns.ArtColumnWidth;
        var visible = columns.VisibleColumns.ToList();
        for (int i = 0; i < gapIndex && i < visible.Count; i++)
            cursor += visible[i].Width;
        return cursor;
    }

    // ColumnManager.Reorder's index counts the visible columns with the dragged
    // one already taken out.
    private int ExcludingIndex(int gapIndex, MusicColumnDefinition excluding)
    {
        int draggedIndex = columns.VisibleColumns.ToList().IndexOf(excluding);
        return gapIndex > draggedIndex ? gapIndex - 1 : gapIndex;
    }
}

/// <summary>What a finished column-header press asks for.</summary>
public abstract record ColumnHeaderGesture
{
    public sealed record Sort(string ColumnId) : ColumnHeaderGesture;

    /// <summary>Move <paramref name="Column"/> to <paramref name="NewVisibleIndex"/>, in ColumnManager.Reorder's terms.</summary>
    public sealed record Reorder(MusicColumnDefinition Column, int NewVisibleIndex) : ColumnHeaderGesture;
}
