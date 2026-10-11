using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Controls;
using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;
using Flower.Views.Mobile.Screens;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

using Track = Flower.Models.Track;

namespace Flower.Tests;

// RecyclingListBox and RecyclingItemsControl build a row once per container
// and only hand it new items after that, and UniformRowPanel keeps those
// containers on the tree while it does. Avalonia's own lists recycle the
// container and throw the row inside it away, so a scroll through the phone's
// Songs list built one whole row per song that came on screen - a probe counted
// 127 rows built for ten containers in 300 frames. What can go wrong with
// keeping them is a row showing the wrong item, so these check both halves:
// how many rows get built, and that each one shows its own.
[Collection("PlatformDataDirectory")]
public class RecyclingItemsTests : PinnedDataDirectory
{
    [AvaloniaFact]
    public void Scrolling_the_songs_list_builds_each_row_once_and_each_shows_its_own_song()
    {
        var tracks = Enumerable.Range(0, 300).Select(i => new Track
        {
            Title = $"Song {i:D3}",
            Album = $"Album {i}",
            Artists = $"Artist {i}",
            Path = $"/music/a{i}/s{i}.flac",
            DateAdded = DateTimeOffset.UtcNow,
        }).ToList();
        using var parts = MainViewModelHarness.BuildParts(new Library(tracks), new MainPlaylist(new List<Track>()));
        MainViewModelHarness.UseSuiteTabs(parts);
        var vm = new MobileMainViewModel(parts.Main, parts.PlaylistControl, parts.CurrentlyPlaying,
            NullLogger<MobileMainViewModel>.Instance);
        vm.SelectTabCommand.Execute(nameof(MobileTab.Songs));
        PumpUntil(() => vm.Main.Rows.Count == tracks.Count);

        var view = new TrackListScreenView { DataContext = vm };
        var window = new Window { Width = 390, Height = 700 };
        window.Styles.Add(new FluentTheme());
        window.Content = view;
        window.Show();
        view.ObserveLive(vm);
        PumpUntil(() => false, milliseconds: 50);

        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "TrackListBox");
        var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        var containers = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        var rows = new HashSet<Control>(ReferenceEqualityComparer.Instance);

        // And none of them leaves the tree to do it (UniformRowPanel): joining
        // the tree is the expensive thing a control does, and Avalonia's own
        // panel had every row do it each time it was handed a new song.
        var reattached = 0;
        foreach (var item in list.GetVisualDescendants().OfType<ListBoxItem>())
            item.AttachedToVisualTree += (_, _) => reattached++;

        for (var frame = 0; frame < 150; frame++)
        {
            scroller.Offset = new Vector(0, scroller.Offset.Y + 40);
            window.UpdateLayout();

            foreach (var container in list.GetVisualDescendants().OfType<ListBoxItem>().Where(c => c.IsVisible))
            {
                containers.Add(container);
                var row = container.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("trackRow"));
                rows.Add(row);

                var shown = (TrackRowViewModel)row.DataContext!;
                Assert.Same(vm.Main.Rows[list.IndexFromContainer(container)], shown);
                Assert.Contains(ShownText.In(row), t => ShownText.Of(t) == shown.Track.Title);
            }
        }

        Assert.True(scroller.Offset.Y > 5000, "the list never scrolled far enough to recycle anything");
        Assert.Equal(containers.Count, rows.Count);
        Assert.Equal(0, reattached);
        window.Close();
    }

    private sealed record Song(string Title);

    private sealed record Heading(string Text);

    // A plain panel throws its containers away whenever its items change - the
    // tiles inside one album-grid row, every time that row is handed another
    // row's albums - so for those the control keeps them itself. Items with
    // different templates (Home's headings and shelves) must only ever be
    // handed a row built from their own.
    [AvaloniaFact]
    public void A_plain_panel_gets_its_rows_back_and_only_for_items_of_their_kind()
    {
        var items = new RecyclingItemsControl
        {
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel()),
        };
        items.DataTemplates.Add(new FuncDataTemplate<Song>((_, _) => new TextBlock { [!TextBlock.TextProperty] = new Avalonia.Data.Binding(nameof(Song.Title)) }));
        items.DataTemplates.Add(new FuncDataTemplate<Heading>((_, _) => new Border()));
        // The theme before the content: a control's theme is found as it is
        // attached, and one added afterwards leaves it with no template.
        var window = new Window { Width = 300, Height = 300 };
        window.Styles.Add(new FluentTheme());
        window.Content = items;
        window.Show();

        items.ItemsSource = new object[] { new Heading("A"), new Song("one"), new Song("two") };
        window.UpdateLayout();
        var firstRows = Rows(items);

        items.ItemsSource = new object[] { new Song("three"), new Song("four"), new Heading("B") };
        window.UpdateLayout();
        var secondRows = Rows(items);

        Assert.All(secondRows, row => Assert.Contains(row, firstRows));
        Assert.Equal(new[] { "three", "four" }, secondRows.OfType<TextBlock>().Select(t => t.Text));
        Assert.IsType<Border>(secondRows[2]);
        window.Close();
    }

    private static List<Control> Rows(ItemsControl items) =>
        items.ItemsPanelRoot!.Children.Select(c => ((Decorator)c).Child!).ToList();

    private static void PumpUntil(Func<bool> done, int milliseconds = 5000)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (!done() && Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
