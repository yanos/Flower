using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Controls;
using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.Views;

using Xunit;

namespace Flower.Tests.Views;

// Home's covers answer the mouse the way the Albums grid's do: a right-click
// opens the album's menu, and a drag carries the album to the sidebar.
// Recently Played's and Recently Added's tiles once did neither.
[Collection("PlatformDataDirectory")]
public class HomeTileGestureTests : PinnedDataDirectory, IDisposable
{
    private readonly IPlatformContextMenu? _previous = PlatformContextMenu.Current;

    public HomeTileGestureTests() => TestIoc.EnsureConfigured();

    public new void Dispose()
    {
        PlatformContextMenu.Current = _previous;
        base.Dispose();
    }

    private sealed class Recorder : IPlatformContextMenu
    {
        public List<IReadOnlyList<ContextMenuEntry>> Shown { get; } = new();

        public bool TryShow(IReadOnlyList<ContextMenuEntry> entries, Control target)
        {
            Shown.Add(entries);
            return true;
        }
    }

    private static (Window Window, MainViewModelHarness.Parts Parts, List<Track> Album) ShowHome()
    {
        var album = Enumerable.Range(1, 3).Select(i => new Track
        {
            Title = $"S{i}",
            Album = "Played",
            Artists = "Ar",
            Path = $"/m/{i}.mp3",
            TrackNumber = (uint)i,
            Duration = TimeSpan.FromMinutes(3),
            LastPlayedAt = DateTimeOffset.Now.AddMinutes(-i),
        }).ToList();
        var parts = MainViewModelHarness.BuildParts(new Library(album), new MainPlaylist(album));
        // Theme first: content added before it never gets its templates.
        var window = new Window { Width = 1200, Height = 800 };
        window.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        window.Content = new MainView { DataContext = parts.Main };
        window.Show();
        parts.Main.SelectedSidebarItem = parts.Main.SidebarItems.First(i => i.Kind == SidebarItemKind.Home);
        // Home rebuilds its shelves on a posted job, and its rows are laid
        // out after that.
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (window, parts, album);
    }

    private static Point CenterOf(Window window, Visual visual) =>
        visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 3), window)!.Value;

    // Recently Played's - it is on Recently Added as well.
    private static AlbumCard PlayedCard(Window window) =>
        window.GetVisualDescendants().OfType<AlbumCard>().First(c => c.IsEffectivelyVisible && c.Tile?.Name == "Played");

    [AvaloniaFact]
    public void A_right_click_on_a_recently_played_cover_opens_the_album_menu()
    {
        var recorder = new Recorder();
        PlatformContextMenu.Current = recorder;
        var (window, parts, _) = ShowHome();
        using var _p = parts;

        var at = CenterOf(window, PlayedCard(window));
        window.MouseDown(at, MouseButton.Right);
        window.MouseUp(at, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        var menu = Assert.Single(recorder.Shown);
        // The album menu's first entry; a song's starts with Play Next.
        Assert.Equal("Play", menu[0].Title);
        window.Close();
    }

    [AvaloniaFact]
    public void A_cover_dragged_onto_the_queue_row_is_queued_and_not_opened()
    {
        var (window, parts, album) = ShowHome();
        using var _p = parts;

        var from = CenterOf(window, PlayedCard(window));
        var queueRow = window.GetVisualDescendants().OfType<ListBoxItem>()
            .First(i => i.DataContext is SidebarItem { Kind: SidebarItemKind.Queue });
        var to = CenterOf(window, queueRow);

        // Something playing, so the album goes after it rather than becoming
        // the queue (PlaylistControlViewModel.LineUp).
        parts.PlaylistControl.Play(album[0]);
        Dispatcher.UIThread.RunJobs();
        var queued = parts.PlaylistControl.CurrentPlaylist.Tracks.Count;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Point(10, 10));
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        var queue = parts.PlaylistControl.CurrentPlaylist.Tracks;
        Assert.Equal(queued + album.Count, queue.Count);
        Assert.Equal(album.Select(t => t.Id), queue.Skip(queued).Select(t => t.Id));
        Assert.Equal(SidebarItemKind.Home, parts.Main.SelectedSidebarItem?.Kind);
        window.Close();
    }
}
