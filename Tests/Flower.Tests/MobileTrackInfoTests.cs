using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;
using Flower.Views.Mobile;

using Xunit;

namespace Flower.Tests;

// The phone's Track Info sheet shows the same listening record the desktop
// window does - how often a song has been played, and when it last was -
// which it had no rows for at all.
[Collection("PlatformDataDirectory")]
public class MobileTrackInfoTests : PinnedDataDirectory
{
    [AvaloniaFact]
    public void Track_info_shows_the_play_count_and_when_it_was_last_played()
    {
        var lastPlayed = new DateTimeOffset(2026, 9, 12, 21, 30, 0, TimeSpan.Zero);
        var played = new Track
        {
            Title = "Played", Path = "/music/played.mp3", Album = "A", Artists = "X",
            PlayCount = 3, ImportedPlayCount = 4, LastPlayedAt = lastPlayed,
        };
        var never = new Track { Title = "Never", Path = "/music/never.mp3", Album = "A", Artists = "X" };
        var tracks = new List<Track> { played, never };
        using var parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
        var vm = parts.Mobile;

        var window = new Window { Width = 390, Height = 844 };
        window.Styles.Add(new FluentTheme());
        window.Content = new MobileMainView { DataContext = vm };
        window.Show();
        vm.SelectTabCommand.Execute(nameof(MobileTab.Songs));
        Pump(500);

        Show(vm, "Played");
        var sheet = FindSheet(window);
        Assert.Equal("7", Value(sheet, "PlayCountValue"));
        var local = lastPlayed.LocalDateTime;
        Assert.Equal($"{local.ToString("MMM d, yyyy", CultureInfo.CurrentCulture)} {local.ToShortTimeString()}",
            Value(sheet, "LastPlayedValue"));

        vm.CloseSheetCommand.Execute(null);
        Show(vm, "Never");
        Assert.Equal("0", Value(sheet, "PlayCountValue"));
        Assert.Equal("—", Value(sheet, "LastPlayedValue"));

        Pump(300);
        window.Close();
    }

    // The card beside the cover says what it is the cover of, and follows
    // the boxes as they are typed in. A song on the paired server names the
    // server where a file path would be.
    [AvaloniaFact]
    public void The_cover_card_names_the_album_artist_and_year_as_they_are_edited()
    {
        var local = new Track { Title = "Local", Path = "/music/local.mp3", Album = "A", Artists = "X", Year = "2003", Genre = "Electronic" };
        var elsewhere = new Track { Title = "Elsewhere", Album = "B", Artists = "Y", OriginDeviceFingerprint = "server-fp" };
        var tracks = new List<Track> { local, elsewhere };
        using var parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
        parts.Parts.AppSettings.PairedServerFingerprint = "server-fp";
        parts.Parts.AppSettings.PairedServerAlias = "Lambda";
        var vm = parts.Mobile;

        var window = new Window { Width = 390, Height = 844 };
        window.Styles.Add(new FluentTheme());
        window.Content = new MobileMainView { DataContext = vm };
        window.Show();
        vm.SelectTabCommand.Execute(nameof(MobileTab.Songs));
        Pump(500);

        Show(vm, "Local");
        var sheet = FindSheet(window);
        Assert.Equal("A", Value(sheet, "AlbumHeading"));
        Assert.Equal("X", Value(sheet, "ArtistHeading"));
        Assert.Equal("2003", Value(sheet, "YearHeading"));
        Assert.Equal("File", Value(sheet, "FileLabel"));

        sheet.FindControl<TextBox>("AlbumBox")!.Text = "Renamed";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Renamed", Value(sheet, "AlbumHeading"));

        vm.CloseSheetCommand.Execute(null);
        Show(vm, "Elsewhere");
        Assert.Equal("B", Value(sheet, "AlbumHeading"));
        Assert.Equal("Source", Value(sheet, "FileLabel"));
        Assert.Equal("Lambda", Value(sheet, "PathValue"));

        Pump(300);
        window.Close();
    }

    private static void Show(MobileMainViewModel vm, string title)
    {
        vm.OpenTrackActionsCommand.Execute(vm.Main.Rows.First(r => r.Track.Title == title));
        vm.ViewTrackInfoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
    }

    private static TrackInfoView FindSheet(Window window) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<TrackInfoView>().Single();

    private static string? Value(TrackInfoView sheet, string name) => sheet.FindControl<TextBlock>(name)!.Text;

    private static void Pump(int milliseconds)
    {
        using var cts = new CancellationTokenSource(milliseconds);
        Dispatcher.UIThread.MainLoop(cts.Token);
    }
}
