using Flower.Models;
using Flower.Services;

namespace Flower.Tests;

public class TrackAvailabilityTests
{
    private static Track Placeholder(string? originDeviceFingerprint) => new()
    {
        Title = "Placeholder",
        Path = null,
        OriginDeviceFingerprint = originDeviceFingerprint,
    };

    private static Track Downloaded(string? originDeviceFingerprint) => new()
    {
        Title = "Downloaded",
        Path = "/music/downloaded.mp3",
        OriginDeviceFingerprint = originDeviceFingerprint,
    };

    [Fact]
    public void IsAvailable_is_true_for_a_placeholder_from_the_reachable_paired_server()
    {
        Assert.True(TrackAvailability.IsAvailable(Placeholder("abc"), pairedServerFingerprint: "abc", pairedServerReachable: true));
    }

    [Fact]
    public void IsAvailable_is_false_when_not_paired_with_any_server()
    {
        Assert.False(TrackAvailability.IsAvailable(Placeholder("abc"), pairedServerFingerprint: null, pairedServerReachable: false));
    }

    [Fact]
    public void IsAvailable_is_false_when_the_paired_server_is_unreachable()
    {
        Assert.False(TrackAvailability.IsAvailable(Placeholder("abc"), pairedServerFingerprint: "abc", pairedServerReachable: false));
    }

    [Fact]
    public void IsAvailable_is_false_for_a_stale_origin_fingerprint_from_a_prior_pairing()
    {
        Assert.False(TrackAvailability.IsAvailable(Placeholder("old-server"), pairedServerFingerprint: "new-server", pairedServerReachable: true));
    }

    [Fact]
    public void IsAvailable_is_false_for_an_already_downloaded_track()
    {
        Assert.False(TrackAvailability.IsAvailable(Downloaded("abc"), pairedServerFingerprint: "abc", pairedServerReachable: true));
    }
}
