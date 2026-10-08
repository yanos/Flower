using System.Collections.Generic;

using AppKit;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;

using CoreGraphics;

using Flower.Services;

using Foundation;

using Material.Icons;

namespace Flower.MacOS;

// Draws Flower's context menus as NSMenus - see Flower.Services.PlatformContextMenu,
// which builds them, decides when one opens, and hands over a description of
// it (ContextMenuEntry). All this does is turn that into AppKit's menu, pop it
// up at the pointer, and run what was picked.
public sealed class AppleContextMenu : IPlatformContextMenu
{
    public bool TryShow(IReadOnlyList<ContextMenuEntry> entries, Control target)
    {
        var menu = Build(entries);

        // In Flower's own light or dark rather than the system's: the app can
        // be set to either regardless of macOS (Settings > Theme), and a menu
        // in the other one opens like a hole in the window.
        menu.Appearance = NSAppearance.GetAppearance(
            Application.Current?.ActualThemeVariant == ThemeVariant.Dark ? NSAppearance.NameDarkAqua : NSAppearance.NameAqua);

        // After the input event that asked for it has finished, rather than
        // inside it: popping up runs AppKit's own tracking loop until the menu
        // closes, and Avalonia is mid-way through routing that event - a
        // pointer press, a context request - when it asks.
        Dispatcher.UIThread.Post(() => menu.PopUpMenu(null, NSEvent.CurrentMouseLocation, null));
        return true;
    }

    private static NSMenu Build(IReadOnlyList<ContextMenuEntry> entries)
    {
        // Enabled is the entry's to say (a disabled item, a command that
        // cannot run), not AppKit's guess from responder-chain validation.
        var menu = new NSMenu { AutoEnablesItems = false };
        foreach (var entry in entries)
        {
            if (entry.IsSeparator)
            {
                menu.AddItem(NSMenuItem.SeparatorItem);
                continue;
            }

            var item = new NSMenuItem(entry.Title) { Enabled = entry.IsEnabled };
            if (entry.IsChecked)
                item.State = NSCellStateValue.On;
            if (entry.Icon is { } icon)
                item.Image = ImageFor(icon);

            if (entry.Children.Count > 0)
                item.Submenu = Build(entry.Children);
            else if (entry.Activate is { } activate)
                // Posted, so the menu has finished closing before what was
                // picked runs - which may open a window of its own.
                item.Activated += (_, _) => Dispatcher.UIThread.Post(activate);

            menu.AddItem(item);
        }
        return menu;
    }

    // The icon a menu entry has on every other platform - the same Material
    // glyph, not an SF Symbol standing in for it - drawn once per kind. A
    // template image, so AppKit colours it as it does its own: the menu's
    // text colour, white under the highlight, grey when disabled.
    private const double IconPoints = 16;
    private static readonly Dictionary<MaterialIconKind, NSImage> Icons = new();

    private static NSImage ImageFor(MaterialIconKind kind)
    {
        if (Icons.TryGetValue(kind, out var cached))
            return cached;

        // At twice the size it is shown, for a Retina screen.
        var image = new NSImage(NSData.FromArray(MenuItemIcons.RenderPng(kind, (int)IconPoints * 2)))
        {
            Size = new CGSize(IconPoints, IconPoints),
            Template = true,
        };
        Icons[kind] = image;
        return image;
    }
}
