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

    // The rest of the ViewModel layer got the same treatment - see
    // docs/ARCHITECTURE-REVIEW.md Tier 2.3/4.2. IAudioManager is the shared
    // source all of these hang off, which is why a leftover handler is not
    // merely untidy: an undisposed ViewModel from one test goes on reacting to
    // the next test's playback events on the same manager.
    [AvaloniaFact]
    public void A_disposed_PlaylistControlViewModel_stops_listening_to_the_audio_manager()
    {
        var parts = MainViewModelHarness.BuildParts(
            new Library(new List<Track> { T("First") }), new MainPlaylist(new List<Track>()));
        var raised = new List<string?>();
        parts.PlaylistControl.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        parts.Audio.RaisePlaying();
        Assert.Contains(nameof(PlaylistControlViewModel.IsPlaying), raised);

        raised.Clear();
        parts.Main.Dispose();
        parts.PlaylistControl.Dispose();
        parts.Audio.RaisePlaying();

        Assert.Empty(raised);
    }

    [AvaloniaFact]
    public void A_disposed_CurrentlyPlayingControlViewModel_stops_listening_to_the_audio_manager()
    {
        var parts = MainViewModelHarness.BuildParts(
            new Library(new List<Track> { T("First") }), new MainPlaylist(new List<Track>()));
        var raised = new List<string?>();
        parts.CurrentlyPlaying.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        parts.Audio.RaisePositionChanged();
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(nameof(CurrentlyPlayingControlViewModel.ElapsedTime), raised);

        raised.Clear();
        parts.Main.Dispose();
        parts.CurrentlyPlaying.Dispose();
        parts.Audio.RaisePositionChanged();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(raised);
    }

    // Mobile's shell wraps the desktop ViewModel rather than replacing it, so
    // it holds a second set of handlers on the same six sources.
    [AvaloniaFact]
    public void A_disposed_MobileMainViewModel_stops_listening_to_the_one_it_wraps()
    {
        var parts = MainViewModelHarness.BuildParts(
            new Library(new List<Track> { T("First") }), new MainPlaylist(new List<Track>()));
        var mobile = new Flower.ViewModels.Mobile.MobileMainViewModel(
            parts.Main, parts.PlaylistControl, parts.CurrentlyPlaying);
        var raised = new List<string?>();
        mobile.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // A rescan is what rebuilds mobile's own album grids and header.
        parts.Library.UpdateTracks(new List<Track> { T("First"), T("Second") });
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(nameof(Flower.ViewModels.Mobile.MobileMainViewModel.CurrentAlbumHeader), raised);

        raised.Clear();
        mobile.Dispose();
        parts.Library.UpdateTracks(new List<Track> { T("First") });
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(raised);

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
