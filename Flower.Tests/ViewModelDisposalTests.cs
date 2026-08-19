using System.Collections.Generic;
using System.Linq;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests;

// MainViewModel attaches to sixteen event sources in its constructor and, until
// now, implemented no IDisposable - harmless only because it is a
// process-lifetime singleton, which nothing enforced, and the reason a test
// could never build one, use it, and let it go.
// See docs/ARCHITECTURE-REVIEW.md Tier 2.3.
public class ViewModelDisposalTests : PinnedDataDirectory
{
    private static Track T(string title) =>
        new() { Title = title, Album = "Album", Artists = "Artist", Path = $"/music/{title}.mp3" };

    // Driven through PlaylistControlViewModel rather than the Library, because
    // this is an assertion about the subscription and not about the value:
    // MainViewModel.SelectedTrack *forwards* to the same object either way, so
    // what disposal changes is whether the change is announced.
    [AvaloniaFact]
    public void A_disposed_MainViewModel_stops_re_raising_what_it_no_longer_listens_to()
    {
        var parts = MainViewModelHarness.BuildParts(
            new Library(new List<Track> { T("First") }), new MainPlaylist(new List<Track>()));
        var raised = new List<string?>();
        parts.Main.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        parts.Main.Dispose();
        parts.PlaylistControl.SelectedTrack = T("Second");

        Assert.DoesNotContain(nameof(MainViewModel.SelectedTrack), raised);
    }

    // The live one is what makes the above an assertion about disposal rather
    // than about the plumbing never having worked.
    [AvaloniaFact]
    public void An_undisposed_MainViewModel_does_re_raise_it()
    {
        var parts = MainViewModelHarness.BuildParts(
            new Library(new List<Track> { T("First") }), new MainPlaylist(new List<Track>()));
        var raised = new List<string?>();
        parts.Main.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        parts.PlaylistControl.SelectedTrack = T("Second");

        Assert.Contains(nameof(MainViewModel.SelectedTrack), raised);

        parts.Main.Dispose();
    }

    [Fact]
    public void A_subscription_bag_unsubscribes_once_however_often_it_is_disposed()
    {
        var source = new EventSource();
        var bag = new SubscriptionBag();
        var seen = 0;

        bag.Add<System.EventHandler>((_, _) => seen++,
            h => source.Fired += h, h => source.Fired -= h);

        source.Raise();
        Assert.Equal(1, seen);

        bag.Dispose();
        bag.Dispose();

        source.Raise();
        Assert.Equal(1, seen);
        Assert.Equal(0, bag.Count);
    }

    private sealed class EventSource
    {
        public event System.EventHandler? Fired;
        public void Raise() => Fired?.Invoke(this, System.EventArgs.Empty);
    }
}
