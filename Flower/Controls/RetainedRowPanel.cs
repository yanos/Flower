using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;

namespace Flower.Controls;

// Keeps generated containers attached while spare and centralizes the generator
// lifecycle. Derived panels own only layout and which rows remain realized.
public abstract class RetainedRowPanel : VirtualizingPanel
{
    protected sealed class Row(Control container, object? recycleKey)
    {
        public readonly Control Container = container;
        public readonly object? RecycleKey = recycleKey;
        public int Index;
    }

    protected readonly List<Row> Rows = new();
    private readonly List<Row> _spare = new();
    protected Row Realize(int index)
    {
        var generator = ItemContainerGenerator!;
        var item = Items[index];
        Row row;

        if (!generator.NeedsContainer(item, index, out var recycleKey))
        {
            // The item is a control already: it is its own row, and only ever
            // shows itself.
            var control = (Control)item!;
            row = new Row(control, null);
            generator.PrepareItemContainer(control, control, index);
            AddInternalChild(control);
            generator.ItemContainerPrepared(control, item, index);
        }
        else if (TakeSpare(recycleKey) is { } spare)
        {
            row = spare;
            generator.PrepareItemContainer(row.Container, item, index);
            row.Container.SetCurrentValue(IsVisibleProperty, true);
            generator.ItemContainerPrepared(row.Container, item, index);
            OnContainerReused(row.Container);
        }
        else
        {
            var container = generator.CreateContainer(item, index, recycleKey);
            row = new Row(container, recycleKey);
            generator.PrepareItemContainer(container, item, index);
            AddInternalChild(container);
            generator.ItemContainerPrepared(container, item, index);
        }

        row.Index = index;
        Rows.Add(row);
        return row;
    }

    private Row? TakeSpare(object? recycleKey)
    {
        for (var i = _spare.Count - 1; i >= 0; i--)
        {
            var row = _spare[i];
            if (Equals(row.RecycleKey, recycleKey))
            {
                _spare.RemoveAt(i);
                return row;
            }
        }
        return null;
    }

    protected virtual void OnContainerReused(Control container) { }

    protected void RecycleWhere(Func<Row, bool> keep)
    {
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            var row = Rows[i];
            if (keep(row))
                continue;
            Rows.RemoveAt(i);
            Recycle(row);
        }
    }

    protected void RecycleRange(int start, int count) =>
        RecycleWhere(row => row.Index < start || row.Index >= start + count);

    protected void ShiftRows(int from, int by)
    {
        foreach (var row in Rows)
        {
            if (row.Index < from)
                continue;
            var old = row.Index;
            row.Index += by;
            ItemContainerGenerator?.ItemContainerIndexChanged(row.Container, old, row.Index);
        }
    }

    private void Recycle(Row row)
    {
        if (row.RecycleKey == null)
        {
            // Its own container: there is nothing to hand it, and nobody else
            // can use it.
            RemoveInternalChild(row.Container);
            return;
        }

        ItemContainerGenerator?.ClearItemContainer(row.Container);
        row.Container.SetCurrentValue(IsVisibleProperty, false);
        _spare.Add(row);
    }

    protected override void OnItemsControlChanged(ItemsControl? oldValue)
    {
        base.OnItemsControlChanged(oldValue);
        Rows.Clear();
        _spare.Clear();
        Children.Clear();
    }

    protected Row? RowAt(int index)
    {
        foreach (var row in Rows)
        {
            if (row.Index == index)
                return row;
        }
        return null;
    }

    protected override Control? ContainerFromIndex(int index) => RowAt(index)?.Container;

    protected override int IndexFromContainer(Control container)
    {
        foreach (var row in Rows)
        {
            if (ReferenceEquals(row.Container, container))
                return row.Index;
        }
        return -1;
    }

    protected override IEnumerable<Control>? GetRealizedContainers() =>
        Rows.OrderBy(r => r.Index).Select(r => r.Container);

}
