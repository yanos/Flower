using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

using Material.Icons;
using Material.Icons.Avalonia;

namespace Flower.Services;

// A context menu drawn by the operating system rather than by Avalonia - on
// macOS an NSMenu, which looks, scrolls, types-to-select and dismisses the way
// every other Mac app's does. Set by a head that has one (Flower.MacOS),
// before Avalonia starts, like PlatformRoutePicker; null everywhere else,
// where the menus stay Avalonia's.
//
// The menus themselves are still built as Avalonia ContextMenus, once, in the
// shared code - MainView's track/album/column/sidebar menus, AlbumTrackList's,
// the XAML-declared ones on Home and in the log viewer. ContextMenuEntry.From
// reads one into a platform-free description, and the head only has to draw
// that and say which entry was picked; picking one raises the original
// MenuItem's Click, so its Command or Click handler runs exactly as it would
// from Avalonia's menu.
public interface IPlatformContextMenu
{
    // Shows the menu at the pointer. False when it could not, in which case
    // the caller opens Avalonia's instead.
    bool TryShow(IReadOnlyList<ContextMenuEntry> entries, Control target);
}

public static class PlatformContextMenu
{
    private static IPlatformContextMenu? _current;

    public static IPlatformContextMenu? Current
    {
        get => _current;
        set
        {
            _current = value;
            if (value != null)
                HookDeclaredMenus();
        }
    }

    // Every menu opened from code goes through here rather than
    // ContextMenu.Open, which never asks anybody first.
    public static void Open(ContextMenu menu, Control target)
    {
        if (!TryShowNatively(menu, target))
            menu.Open(target);
    }

    private static bool TryShowNatively(ContextMenu menu, Control target)
    {
        if (_current is not { } native)
            return false;

        // A menu Avalonia has never opened is not in the visual tree, so it
        // has inherited nothing and a binding inside it - a XAML menu's
        // Command="{Binding ...}" - has not resolved. Opening would have
        // given it the target's data; this does the same, and on every
        // opening rather than the first: Home's tiles are recycled as the
        // rows scroll, so the Button a menu belongs to can be showing a
        // different album from the last time it was right-clicked.
        menu.DataContext = target.DataContext;

        var entries = ContextMenuEntry.From(menu.Items.OfType<object>());
        return entries.Count > 0 && native.TryShow(entries, target);
    }

    // A ContextMenu declared on a control (Control.ContextMenu, from XAML)
    // is opened by Avalonia itself on a right-click, never through Open
    // above. Its Opening event is raised first and can be cancelled, which is
    // where the native one is shown instead - so every such menu is watched
    // for the control it belongs to as it is assigned. Installed once, the
    // moment a head sets Current.
    private static bool _hooked;
    private static readonly ConditionalWeakTable<ContextMenu, Control> Owners = new();

    private static void HookDeclaredMenus()
    {
        if (_hooked)
            return;
        _hooked = true;

        Control.ContextMenuProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.OldValue is ContextMenu old)
            {
                old.Opening -= OnDeclaredMenuOpening;
                Owners.Remove(old);
            }
            if (e.NewValue is ContextMenu menu)
            {
                Owners.AddOrUpdate(menu, control);
                menu.Opening += OnDeclaredMenuOpening;
            }
        });
    }

    private static void OnDeclaredMenuOpening(object? sender, CancelEventArgs e)
    {
        if (sender is ContextMenu menu && Owners.TryGetValue(menu, out var owner) && TryShowNatively(menu, owner))
            e.Cancel = true;
    }
}

// One line of a context menu, as the operating system is asked to draw it.
public sealed record ContextMenuEntry(
    string Title,
    bool IsEnabled,
    bool IsChecked,
    IReadOnlyList<ContextMenuEntry> Children,
    Action? Activate,
    bool IsSeparator = false)
{
    public static readonly ContextMenuEntry Separator = new("", false, false, [], null, IsSeparator: true);

    // What the user would see in Avalonia's menu: hidden items left out,
    // a disabled item - or one whose command cannot run - greyed, the column
    // menu's check glyph as a checkmark, submenus as submenus. Separators
    // that a hidden item left leading, trailing or doubled are dropped, as
    // Avalonia's own layout makes them look.
    public static List<ContextMenuEntry> From(IEnumerable<object> items)
    {
        var entries = new List<ContextMenuEntry>();
        foreach (var item in items)
        {
            switch (item)
            {
                case Avalonia.Controls.Separator:
                    if (entries.Count > 0 && !entries[^1].IsSeparator)
                        entries.Add(Separator);
                    break;
                case MenuItem { IsVisible: true } menuItem:
                    entries.Add(FromItem(menuItem));
                    break;
            }
        }

        if (entries.Count > 0 && entries[^1].IsSeparator)
            entries.RemoveAt(entries.Count - 1);
        return entries;
    }

    private static ContextMenuEntry FromItem(MenuItem item)
    {
        var children = From(item.Items.OfType<object>());
        var canRun = item.Command?.CanExecute(item.CommandParameter) ?? true;
        var isChecked = item.IsChecked
            || item.Icon is MaterialIcon { Kind: MaterialIconKind.Check };

        return new ContextMenuEntry(
            Title: item.Header as string ?? item.Header?.ToString() ?? "",
            IsEnabled: item.IsEnabled && (children.Count > 0 || canRun),
            IsChecked: isChecked,
            Children: children,
            Activate: children.Count > 0 ? null : () => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
    }
}
