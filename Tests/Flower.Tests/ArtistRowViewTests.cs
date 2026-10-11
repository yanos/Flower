using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;
using Flower.Views.Mobile.Screens;

using Xunit;

namespace Flower.Tests;

// One artist row, ArtistRowView, on the Artists tab and in Search. What a tap
// does is the one thing that differs between them - each screen opens the
// artist its own way, so Back returns to it - and the row asks the screen it
// is in (IArtistRowHost) rather than naming a command itself. These hold that
// each screen's rows run that screen's command, with the artist's name.
[Collection("PlatformDataDirectory")]
public class ArtistRowViewTests : PinnedDataDirectory
{
    private static MainViewModelHarness.MobileParts Build()
    {
        var tracks = new List<Track>
        {
            new() { Title = "Heroes", Album = "Heroes", Artists = "David Bowie", Path = "/music/heroes.flac", DateAdded = DateTimeOffset.UtcNow },
            new() { Title = "Airbag", Album = "OK Computer", Artists = "Radiohead", Path = "/music/airbag.flac", DateAdded = DateTimeOffset.UtcNow },
        };
        var parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
        Dispatcher.UIThread.RunJobs();
        return parts;
    }

    // The row once it is on screen: both lists are filled off the UI thread
    // (Search on a debounce), so it is waited for rather than expected.
    private static ArtistRowView RowFor(Control screen, string artist)
    {
        var window = new Window { Width = 400, Height = 800 };
        window.Styles.Add(new FluentTheme());
        window.Content = screen;
        window.Show();

        var deadline = Environment.TickCount64 + 5000;
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var row = screen.GetVisualDescendants().OfType<ArtistRowView>()
                .SingleOrDefault(r => r.IsEffectivelyVisible && (r.DataContext as ArtistPickerRow)?.Name == artist);
            if (row != null)
                return row;
            Assert.True(Environment.TickCount64 < deadline, $"no row for {artist}");
            Thread.Sleep(10);
        }
    }

    [AvaloniaFact]
    public void A_row_on_the_artists_tab_opens_the_artist_from_there()
    {
        using var parts = Build();
        var vm = parts.Mobile;
        vm.SelectTabCommand.Execute(nameof(MobileTab.Artists));

        var row = RowFor(new ArtistPickerScreenView { DataContext = vm }, "Radiohead");

        Assert.Same(vm.SelectArtistCommand, row.Command);
        Assert.Equal("Radiohead", row.CommandParameter);
    }

    [AvaloniaFact]
    public void A_row_in_search_opens_the_artist_from_search()
    {
        using var parts = Build();
        var vm = parts.Mobile;
        vm.SelectTabCommand.Execute(nameof(MobileTab.Search));
        vm.SearchQuery = "Radio";

        var row = RowFor(new SearchResultsScreenView { DataContext = vm }, "Radiohead");

        Assert.Same(vm.SelectSearchArtistCommand, row.Command);
        Assert.Equal("Radiohead", row.CommandParameter);
    }
}
