using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Controls;
using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.Views;

using Xunit;

namespace Flower.Tests.Views;

// Each name in the desktop Artists column draws that artist's icon - their
// picture or their covers, from ArtistIcons. It once drew nothing at all:
// the collage was handed down as the row's DataContext, whose binding reads
// its <Binding/> from the DataContext it sets, so never saw the name.
[Collection("PlatformDataDirectory")]
public class ArtistColumnPictureTests : PinnedDataDirectory
{
    public ArtistColumnPictureTests() => TestIoc.EnsureConfigured();

    [AvaloniaFact]
    public void Each_artist_row_draws_that_artists_cover()
    {
        // On the application, and before MainView is made: given only the
        // window's, the Artists column's list was never templated headlessly.
        var theme = new FluentTheme();
        Application.Current!.Styles.Add(theme);
        var tracks = Enumerable.Range(1, 3)
            .Select(i => new Track { Title = $"S{i}", Album = $"Al{i}", Artists = $"Ar{i}", Path = $"/m/{i}.mp3" })
            .ToList();
        using var parts = MainViewModelHarness.BuildParts(new Library(tracks), new MainPlaylist(tracks));
        var view = new MainView { DataContext = parts.Main };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        try
        {
            window.Show();
            AssertEachRowDrawsItsArtist(window, parts);
        }
        finally
        {
            window.Close();
            Application.Current.Styles.Remove(theme);
        }
    }

    private static void AssertEachRowDrawsItsArtist(Window window, MainViewModelHarness.Parts parts)
    {
        parts.Main.SelectedSidebarItem = parts.Main.SidebarItems.First(i => i.Kind == SidebarItemKind.Artists);
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "SubList");
        for (var frame = 0; frame < 10 && !list.GetVisualDescendants().OfType<ListBoxItem>().Any(); frame++)
        {
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();
        }
        var rows = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();

        Assert.Equal(3, rows.Count);
        foreach (var row in rows)
        {
            var picture = row.GetVisualDescendants().OfType<AlbumArtView>().Single();
            Assert.Same(parts.Main.Browser.ArtistIcons.For((string)row.DataContext!, thumbnail: true), picture.DataContext);
        }
    }
}
