using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Controls;
using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.Views;

using Xunit;

namespace Flower.Tests.ViewModels;

// The queue as a page: the shared QueueViewModel, which the phone's Queue tab
// and the desktop's Queue sidebar row both draw, and the desktop's half of it.
// The phone's menu commands are covered in MobileQueueLineUpTests.
[Collection("PlatformDataDirectory")]
public class QueuePageTests : PinnedDataDirectory
{
    public QueuePageTests() => TestIoc.EnsureConfigured();

    private static List<Track> Tracks(params string[] titles) =>
        titles.Select(t => new Track { Title = t, Path = $"/music/{t}.mp3", Album = "Album", Artists = "Artist" }).ToList();

    private static MainViewModelHarness.Parts Playing(params string[] titles)
    {
        var tracks = Tracks(titles);
        var parts = MainViewModelHarness.BuildParts(new Library(tracks), new MainPlaylist(tracks));
        parts.Main.SetPlayQueue(tracks);
        parts.PlaylistControl.Play(tracks[0], 0);
        Dispatcher.UIThread.RunJobs();
        return parts;
    }

    private static void ShowQueue(MainViewModel main) =>
        main.SelectedSidebarItem = main.SidebarItems.First(i => i.Kind == SidebarItemKind.Queue);

    private static string[] Titles(QueueViewModel queue) => queue.Rows.Select(r => r.Track.Title!).ToArray();

    // The desktop's list is handed the queue's rows on the Queue page, the
    // browser's everywhere else - and its headers stop showing a sort.
    [AvaloniaFact]
    public void The_track_list_shows_the_queue_on_its_page()
    {
        using var parts = Playing("A", "B", "C");
        var view = new MainView { DataContext = parts.Main };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Show();

        ShowQueue(parts.Main);
        Dispatcher.UIThread.RunJobs();

        Assert.True(parts.Main.IsShowingQueue);
        Assert.True(parts.Main.IsShowingTrackList);
        var list = view.FindControl<MusicListView>("MusicList")!;
        Assert.Same(parts.Main.Queue.Rows, list.Items);
        Assert.True(list.AllowReorder);

        parts.PlaylistControl.ToggleShuffle();
        Dispatcher.UIThread.RunJobs();
        Assert.False(list.AllowReorder);
        Assert.Same(parts.Main.Queue.Rows, list.Items);
        window.Close();
    }

    // Songs dragged from a list onto the sidebar's Queue row go on the end of
    // the queue, as the menu's Add to Queue puts them.
    [AvaloniaFact]
    public void A_song_dragged_onto_the_queue_row_is_queued()
    {
        using var parts = Playing("A", "B", "C");
        var main = parts.Main;
        var view = new MainView { DataContext = main };
        // Theme first: content added before it never gets its templates.
        var window = new Window { Width = 1100, Height = 700 };
        window.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        window.Content = view;
        window.Show();
        main.SelectedSidebarItem = main.SidebarItems.First(i => i.Kind == SidebarItemKind.Songs);
        Dispatcher.UIThread.RunJobs();

        Drag(window, view, RowCentre(window, view, "C"), QueueRowCentre(window, view));

        ShowQueue(main);
        Assert.Equal(new[] { "A", "B", "C", "C" }, Titles(main.Queue));
        window.Close();
    }

    // Under shuffle the Queue page's rows drag like any list's - but dropped
    // back on the Queue row they would only be queued again, so they are not.
    [AvaloniaFact]
    public void The_queue_s_own_rows_are_not_queued_again()
    {
        using var parts = Playing("A", "B", "C");
        var main = parts.Main;
        parts.PlaylistControl.ToggleShuffle();
        var view = new MainView { DataContext = main };
        // Theme first: content added before it never gets its templates.
        var window = new Window { Width = 1100, Height = 700 };
        window.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        window.Content = view;
        window.Show();
        ShowQueue(main);
        Dispatcher.UIThread.RunJobs();
        var before = parts.PlaylistControl.CurrentPlaylist.Tracks.Count;

        Drag(window, view, RowCentre(window, view, "B"), QueueRowCentre(window, view));

        Assert.Equal(before, parts.PlaylistControl.CurrentPlaylist.Tracks.Count);
        window.Close();
    }

    private static Point RowCentre(Window window, MainView view, string title)
    {
        window.UpdateLayout();
        var row = view.FindControl<MusicListView>("MusicList")!.GetVisualDescendants()
            .OfType<TrackRowControl>()
            .First(r => r.IsVisible && (r.DataContext as TrackRowViewModel)?.Track.Title == title);
        return row.TranslatePoint(new Point(200, row.Bounds.Height / 2), window)!.Value;
    }

    private static Point QueueRowCentre(Window window, MainView view)
    {
        var sidebar = view.FindControl<ListBox>("SidebarList")!;
        var index = ((MainViewModel)view.DataContext!).SidebarItems.ToList().FindIndex(i => i.Kind == SidebarItemKind.Queue);
        var row = (Control)sidebar.ContainerFromIndex(index)!;
        return row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
    }

    private static void Drag(Window window, MainView view, Point from, Point to)
    {
        // One press selects the row and arms the drag; a click first would
        // make it a double-click, which plays the song.
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point(from.X + 20, from.Y), RawInputModifiers.LeftMouseButton);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
