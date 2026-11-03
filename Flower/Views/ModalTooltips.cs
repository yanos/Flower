using System;
using System.Collections.Generic;

using Avalonia;
using Avalonia.Controls;

namespace Flower.Views;

// A window behind a modal dialog shows no tooltips, as on macOS itself, where
// a window waiting on a dialog does not answer the pointer at all. Avalonia
// keeps them on: hovering the sidebar's New Playlist button while the Vibe
// Playlist editor was open showed its tooltip and took it away again many
// times a second, for as long as the pointer stayed there. So every window
// shown with ShowDialog turns its owner's tooltips off (ToolTip.ServiceEnabled
// is inherited, so that covers everything in it) and the last one to close
// turns them back on. Watched once, for every window, rather than written at
// each ShowDialog, so a dialog added later is covered without anyone
// remembering to.
//
// What is watched is Owner rather than WindowOpenedEvent/WindowClosedEvent:
// Avalonia raises Opened before it sets Owner, and sets it back to null as
// the dialog closes, so Owner changing is the one moment both are known.
public static class ModalTooltips
{
    // Each open dialog, and the window it blocks.
    private static readonly Dictionary<Window, Window> Blocking = [];
    private static IDisposable? _watching;

    public static void Install() =>
        _watching ??= WindowBase.OwnerProperty.Changed.Subscribe(new Observer());

    private static void OwnerChanged(Window dialog, Window? owner)
    {
        if (Blocking.Remove(dialog, out var blocked) && !Blocking.ContainsValue(blocked))
            blocked.ClearValue(ToolTip.ServiceEnabledProperty);

        if (owner == null || !dialog.IsDialog)
            return;

        Blocking[dialog] = owner;
        ToolTip.SetServiceEnabled(owner, false);
    }

    private sealed class Observer : IObserver<AvaloniaPropertyChangedEventArgs<WindowBase?>>
    {
        public void OnNext(AvaloniaPropertyChangedEventArgs<WindowBase?> e)
        {
            if (e.Sender is Window dialog)
                OwnerChanged(dialog, e.NewValue.GetValueOrDefault() as Window);
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }
}
