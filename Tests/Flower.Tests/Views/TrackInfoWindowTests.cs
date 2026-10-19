using System;
using System.Collections.Generic;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.Views;

using Xunit;

namespace Flower.Tests.Views;

// The desktop window's boxes are bound to TrackInfoViewModel rather than
// filled in and read back by hand, and a binding is the one part of that a
// view model test cannot see.
[Collection("PlatformDataDirectory")]
public class TrackInfoWindowTests : PinnedDataDirectory
{
    public TrackInfoWindowTests() => TestIoc.EnsureConfigured();

    [AvaloniaFact]
    public void What_is_typed_in_a_box_is_what_OK_saves()
    {
        var path = SyntheticWav.CreateFile(DataDirectory, "debaser.wav", TimeSpan.FromSeconds(1), SyntheticWav.Marker(1));
        var song = new Track { Title = "Debaser", Album = "Doolittle", Artists = "Pixies", Path = path };
        var tracks = new List<Track> { song };
        using var parts = MainViewModelHarness.BuildParts(new Library(tracks), new MainPlaylist(tracks));
        var window = new TrackInfoWindow(tracks, 0, parts.Main);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var title = window.FindControl<TextBox>("TitleBox")!;
        Assert.Equal("Debaser", title.Text);
        title.Text = "Debaser (Demo)";
        window.FindControl<Button>("OkButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Debaser (Demo)", song.Title);
    }
}
