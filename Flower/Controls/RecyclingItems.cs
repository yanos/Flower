using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Flower.Controls;

// A list whose rows are built once per container and then only handed new
// items, rather than built again for every item that scrolls into view.
//
// Avalonia recycles a list's containers - the ListBoxItem or ContentPresenter
// around each row - but not what is inside them. Clearing a container sets its
// Content back to null, which makes its ContentPresenter drop the row its
// DataTemplate built and forget the template it was built from, so the next
// item through that container builds the whole template again: every control
// in it, its styles matched, its own templates applied, its bindings set up.
// A probe scrolling the phone's Songs list headlessly counted ten containers
// and 127 rows built in 300 frames - one per song that came on screen - and
// about 420 KB allocated per frame at a hard fling, which on a phone's Mono is
// a garbage collection every few frames, each one stopping the render thread
// too. With the row built into the container once, the same scroll built ten,
// and the slowest frames came down from ~20ms to ~9ms on a desktop.
//
// So here each container is made with its row already in it - as a ListBoxItem's
// Content, or a Decorator's Child - and the only thing that changes on reuse is
// the container's DataContext, which ItemsControl sets before preparing it. The template is chosen per item, and is the recycle key,
// so a list whose items take different templates (Home's shelves) only ever
// hands a row to an item of the kind it was built for.
//
// A virtualizing panel keeps its own pool of cleared containers and hands them
// back; a plain panel (the tiles of one album-grid row) throws its containers
// away whenever its items change, so for those this keeps the pool itself.
internal sealed class RowRecycler
{
    // Enough for every tile a row of the widest grid can hold, several times
    // over; anything beyond it is a list that shrank, and is let go.
    private const int MaxPooledPerTemplate = 32;

    // Which template built the row inside a container - set only on containers
    // made here, so a container that is its own item, or one made before a
    // template could be found, is cleared and prepared the ordinary way.
    private static readonly ConditionalWeakTable<Control, IDataTemplate> BuiltFrom = new();

    private readonly ItemsControl _owner;
    private readonly Dictionary<IDataTemplate, Stack<Control>> _pool = new();

    public RowRecycler(ItemsControl owner) => _owner = owner;

    public static bool HoldsRow(Control container) => BuiltFrom.TryGetValue(container, out _);

    // The template this item would be shown with, which is also the key it is
    // recycled under. Null for an item that is a control itself, or one no
    // template matches - both left to ItemsControl.
    public IDataTemplate? TemplateFor(object? item) =>
        item is Control ? null : _owner.FindDataTemplate(item, _owner.ItemTemplate);

    public Control? TakePooled(object? recycleKey)
    {
        if (recycleKey is IDataTemplate template
            && _pool.TryGetValue(template, out var pooled)
            && pooled.Count > 0)
            return pooled.Pop();
        return null;
    }

    public T Fill<T>(T container, object? recycleKey, object? item) where T : Control
    {
        if (recycleKey is not IDataTemplate template || template.Build(item) is not { } row)
            return container;

        switch (container)
        {
            case ContentControl contentControl:
                contentControl.Content = row;
                break;
            case Decorator decorator:
                decorator.Child = row;
                break;
            default:
                return container;
        }

        BuiltFrom.Add(container, template);
        return container;
    }

    // Called in place of ItemsControl's own clearing for a container holding a
    // row, which would set Content to null and throw the row away. Kept for
    // reuse here only when no virtualizing panel is doing that already - a
    // container in both pools would be handed to two items at once.
    public void Release(Control container)
    {
        if (_owner.ItemsPanelRoot is VirtualizingPanel || !BuiltFrom.TryGetValue(container, out var template))
            return;

        if (!_pool.TryGetValue(template, out var pooled))
            _pool[template] = pooled = new Stack<Control>();
        if (pooled.Count < MaxPooledPerTemplate)
            pooled.Push(container);
    }
}

/// <summary>
/// A ListBox whose rows are built once per container - see <see cref="RowRecycler"/>.
/// Styled and templated exactly as a ListBox.
/// </summary>
public class RecyclingListBox : ListBox
{
    private readonly RowRecycler _rows;

    public RecyclingListBox() => _rows = new RowRecycler(this);

    protected override Type StyleKeyOverride => typeof(ListBox);

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        if (!base.NeedsContainerOverride(item, index, out recycleKey))
            return false;
        if (_rows.TemplateFor(item) is { } template)
            recycleKey = template;
        return true;
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) =>
        _rows.TakePooled(recycleKey) ?? _rows.Fill(new ListBoxItem(), recycleKey, item);

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        if (!RowRecycler.HoldsRow(container))
        {
            base.PrepareContainerForItemOverride(container, item, index);
            return;
        }

        // What SelectingItemsControl's own preparation does before handing on
        // to ItemsControl's, which would set the item as Content and the
        // template as ContentTemplate - and a ContentPresenter given both a
        // control and a template builds the template again around the control.
        _ = Selection;
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        if (!RowRecycler.HoldsRow(container))
        {
            base.ClearContainerForItemOverride(container);
            return;
        }

        // SelectingItemsControl's half of clearing; ItemsControl's half is the
        // Content this exists to keep.
        container.ClearValue(IsSelectedProperty);
        _rows.Release(container);
    }
}

/// <summary>
/// An ItemsControl whose rows are built once per container - see <see cref="RowRecycler"/>.
/// Styled and templated exactly as an ItemsControl.
/// </summary>
public class RecyclingItemsControl : ItemsControl
{
    private readonly RowRecycler _rows;

    public RecyclingItemsControl() => _rows = new RowRecycler(this);

    protected override Type StyleKeyOverride => typeof(ItemsControl);

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        if (!base.NeedsContainerOverride(item, index, out recycleKey))
            return false;
        if (_rows.TemplateFor(item) is { } template)
            recycleKey = template;
        return true;
    }

    // A Decorator rather than the ContentPresenter ItemsControl would make: a
    // ContentPresenter whose Content is a control and has no template clears
    // its own DataContext to let the control keep whatever it has, which here
    // is the item ItemsControl has just set on the container - the row would
    // inherit the screen's DataContext instead of its item.
    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) =>
        _rows.TakePooled(recycleKey) ?? _rows.Fill(new Decorator(), recycleKey, item);

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        if (!RowRecycler.HoldsRow(container))
            base.PrepareContainerForItemOverride(container, item, index);
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        if (!RowRecycler.HoldsRow(container))
        {
            base.ClearContainerForItemOverride(container);
            return;
        }

        _rows.Release(container);
    }
}
