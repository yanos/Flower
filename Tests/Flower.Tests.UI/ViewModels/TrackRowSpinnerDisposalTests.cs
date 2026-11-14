using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Audio;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Material.Icons;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests.ViewModels;

public class TrackRowSpinnerDisposalTests
{
    private static TrackRowViewModel Row(Track track, bool first = true, int groupSize = 1) =>
        new() { Track = track, IsFirstInAlbumGroup = first, AlbumGroupSize = groupSize };

    // The spinner's timer is owned by the row, not the recycled container -
    // and a row discarded mid-download used to leave a 16ms DispatcherTimer
    // registered forever, one per orphaned row.
    // Lets the row's real 16ms DispatcherTimer come due, then pumps it. Just
    // calling RunJobs immediately proves nothing: the timer would not have
    // fired yet either way.
    // Runs the dispatcher's real main loop for long enough that the row's 16ms
    // DispatcherTimer actually comes due and fires. RunJobs() alone is not
    // enough - it drains queued operations but never advances the timer queue,
    // so the spinner would read as "not turning" whether or not the timer was
    // still alive, and the test would assert nothing.
    private static void LetTheSpinnerTick(int milliseconds = 120)
    {
        using var cts = new CancellationTokenSource(milliseconds);
        Dispatcher.UIThread.MainLoop(cts.Token);
    }

    // A row discarded mid-download used to leave its timer registered on the
    // dispatcher forever, with the Tick closure keeping the ViewModel alive and
    // burning a 60fps wakeup - one per orphaned row of a batch download.
    [AvaloniaFact]
    public void Dispose_stops_the_download_spinner_and_resets_its_angle()
    {
        var row = Row(new Track { Path = null });
        row.IsDownloading = true;
        LetTheSpinnerTick();

        row.Dispose();
        Assert.Equal(0, row.SpinAngle);

        LetTheSpinnerTick();
        Assert.Equal(0, row.SpinAngle);
    }
}
