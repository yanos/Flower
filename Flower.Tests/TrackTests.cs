using System.Collections.Generic;

using Flower.Models;

namespace Flower.Tests;

public class TrackTests
{
    [Fact]
    public void TotalPlayCount_sums_own_imported_and_every_remote_devices_count()
    {
        var track = new Track
        {
            PlayCount = 3,
            ImportedPlayCount = 5,
            RemotePlayCounts = new Dictionary<string, int> { ["peer-1"] = 2, ["peer-2"] = 7 },
        };

        Assert.Equal(17, track.TotalPlayCount);
    }

    [Fact]
    public void TotalPlayCount_ignores_no_remote_devices_by_default()
    {
        var track = new Track { PlayCount = 4, ImportedPlayCount = 1 };

        Assert.Equal(5, track.TotalPlayCount);
    }
}
