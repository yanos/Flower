using System.Linq;

using Avalonia.Headless.XUnit;

using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels.Mobile;

using Xunit;

namespace Flower.Tests.ViewModels.Mobile;

// A flower:// link opened from outside the app, on a phone: it lands in
// Settings, where the address box is and where what happened is said.
[Collection("PlatformDataDirectory")]
public class MobilePairingLinkTests : PinnedDataDirectory
{
    private static MainViewModelHarness.MobileParts Build()
    {
        var tracks = Enumerable.Range(0, 2).Select(i => new Track
        {
            Title = $"Track {i}", Path = $"/music/{i}.mp3", Album = "Album", Artists = "Artist",
        }).ToList();
        return MainViewModelHarness.BuildMobile(new Library(tracks), new MainPlaylist(tracks));
    }

    private static readonly PairingInvite Invite = new(["https://38.133.38.247:4534"], "YRR5M", "new-server-fp");

    // Switching servers is an explicit unpair first, never a link's side effect.
    [AvaloniaFact]
    public void A_link_for_another_server_while_paired_says_to_unpair_first()
    {
        using var parts = Build();
        parts.Parts.AppSettings.PairedServerFingerprint = "current-server-fp";
        parts.Parts.AppSettings.PairedServerAlias = "Basement";

        parts.Mobile.Pairing.OpenPairingLink(Invite);

        Assert.Equal(MobileSheet.Settings, parts.Mobile.ActiveSheet);
        Assert.Contains("unpair from Basement first", parts.Mobile.Pairing.ManualServerStatus);
        Assert.Equal("", parts.Mobile.Pairing.ManualServerAddress);
    }

    [AvaloniaFact]
    public void A_link_for_the_server_already_paired_with_has_nothing_to_do()
    {
        using var parts = Build();
        parts.Parts.AppSettings.PairedServerFingerprint = "new-server-fp";
        parts.Parts.AppSettings.PairedServerAlias = "Basement";

        parts.Mobile.Pairing.OpenPairingLink(Invite);

        Assert.Contains("already paired with Basement", parts.Mobile.Pairing.ManualServerStatus);
    }
}
