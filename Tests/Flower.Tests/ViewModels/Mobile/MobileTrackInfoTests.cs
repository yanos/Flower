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

namespace Flower.Tests.ViewModels.Mobile;

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

    // A song on the paired server names the server where a file path would
    // be.
    [AvaloniaFact]
    public void A_song_on_the_paired_server_names_the_server_for_its_file()
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
        Assert.Equal("File", Value(sheet, "FileLabel"));

        vm.CloseSheetCommand.Execute(null);
        Show(vm, "Elsewhere");
        Assert.Equal("Source", Value(sheet, "FileLabel"));
        Assert.Equal("Lambda", Value(sheet, "PathValue"));

        Pump(300);
        window.Close();
    }

    // Track Info from the album menu edits every song of the album: the
    // common tags in the boxes, "Multiple values" where they differ, and only
    // what is typed applied to them all - the titles stay their own.
    [AvaloniaFact]
    public void Track_info_from_the_album_menu_edits_every_song_of_the_album()
    {
        Track Song(string title, uint n) => new()
        {
            Title = title, Album = "Avant Hard", Artists = "Add N To (X)", TrackNumber = n,
            OriginDeviceFingerprint = "server-fp", OriginTrackId = $"t{n}",
        };
        var tracks = new List<Track> { Song("Skills", 3), Song("Fyuz", 5), Song("Robot New York", 2) };
        using var parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
        parts.Parts.AppSettings.PairedServerFingerprint = "server-fp";
        parts.Parts.AppSettings.PairedServerGrantsAdmin = true;
        var vm = parts.Mobile;

        var window = new Window { Width = 390, Height = 844 };
        window.Styles.Add(new FluentTheme());
        window.Content = new MobileMainView { DataContext = vm };
        window.Show();
        Pump(300);

        vm.OpenAlbumActionsCommand.Execute(new Flower.ViewModels.Mobile.AlbumTileViewModel
        {
            Name = "Avant Hard", Artist = "Add N To (X)", Tracks = tracks, RepresentativeTrack = tracks[0],
        });
        vm.ViewAlbumActionTargetTrackInfoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var sheet = FindSheet(window);
        var titleBox = sheet.FindControl<TextBox>("TitleBox")!;
        var artistBox = sheet.FindControl<TextBox>("ArtistBox")!;
        Assert.Equal("", titleBox.Text);
        Assert.Equal("Multiple values", titleBox.PlaceholderText);
        Assert.Equal("Add N To (X)", artistBox.Text);

        artistBox.Text = "Add N to (X)";
        sheet.FindControl<Button>("SaveButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.All(tracks, t => Assert.Equal("Add N to (X)", t.Artists));
        Assert.Equal(new[] { "Skills", "Fyuz", "Robot New York" }, tracks.Select(t => t.Title));

        Pump(300);
        window.Close();
    }

    // One song's Track Info steps along the list it was opened from, a song
    // at a time, keeping what was typed for the song it leaves - and the
    // sheet over a whole album has nowhere to step.
    [AvaloniaFact]
    public void Track_info_for_one_song_steps_through_its_list_saving_on_the_way()
    {
        Track Song(string title, uint n) => new()
        {
            Title = title, Album = "A", Artists = "X", TrackNumber = n,
            OriginDeviceFingerprint = "server-fp", OriginTrackId = $"t{n}",
        };
        var tracks = new List<Track> { Song("One", 1), Song("Two", 2), Song("Three", 3) };
        using var parts = MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
        parts.Parts.AppSettings.PairedServerFingerprint = "server-fp";
        parts.Parts.AppSettings.PairedServerGrantsAdmin = true;
        var vm = parts.Mobile;

        var window = new Window { Width = 390, Height = 844 };
        window.Styles.Add(new FluentTheme());
        window.Content = new MobileMainView { DataContext = vm };
        window.Show();
        vm.SelectTabCommand.Execute(nameof(MobileTab.Songs));
        Pump(500);

        var order = vm.Main.Rows.Select(r => r.Track).ToList();
        Show(vm, order[0].Title!);
        var sheet = FindSheet(window);
        var titleBox = sheet.FindControl<TextBox>("TitleBox")!;
        Assert.True(vm.CanStepTrackInfo);
        Assert.False(vm.HasPreviousTrackInfo);
        Assert.True(vm.HasNextTrackInfo);

        var first = order[0];
        titleBox.Text = "Renamed";
        Click(sheet, "NextSongButton");

        Assert.Equal("Renamed", first.Title);
        Assert.Equal(order[1].Title, titleBox.Text);
        Assert.True(vm.HasPreviousTrackInfo);

        Click(sheet, "NextSongButton");
        Assert.Equal(order[2].Title, titleBox.Text);
        Assert.False(vm.HasNextTrackInfo);

        Click(sheet, "PreviousSongButton");
        Click(sheet, "PreviousSongButton");
        Assert.Equal("Renamed", titleBox.Text);

        vm.CloseSheetCommand.Execute(null);
        vm.OpenAlbumActionsCommand.Execute(new Flower.ViewModels.Mobile.AlbumTileViewModel
        {
            Name = "A", Artist = "X", Tracks = tracks, RepresentativeTrack = tracks[0],
        });
        vm.ViewAlbumActionTargetTrackInfoCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.CanStepTrackInfo);

        Pump(300);
        window.Close();
    }

    private static void Click(TrackInfoView sheet, string name)
    {
        sheet.FindControl<Button>(name)!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
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
