using System.Collections.Generic;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Persistence;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests;

// The phone's Unpair used to act on the tap. Unpairing removes every song this
// device has not downloaded, and getting them back takes a new code from the
// server's owner, so it now asks first, the way desktop's two Unpair buttons
// do - see MobileMainViewModel.UnpairServerCommand.
[Collection("PlatformDataDirectory")]
public class MobileUnpairSheetTests : PinnedDataDirectory
{
    private static MainViewModelHarness.MobileParts BuildPaired()
    {
        var settings = new AppSettings
        {
            PairedServerFingerprint    = "fp-attic",
            PairedServerAlias          = "Attic",
            PairedServerTrustConfirmed = true,
        };
        var parts = MainViewModelHarness.BuildParts(new Library(new List<Track>()), new MainPlaylist(new List<Track>()), settings);
        var mobile = new MobileMainViewModel(parts.Main, parts.PlaylistControl, parts.CurrentlyPlaying, NullLogger<MobileMainViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        return new MainViewModelHarness.MobileParts(mobile, parts);
    }

    [AvaloniaFact]
    public void Tapping_unpair_asks_instead_of_unpairing()
    {
        using var scope = BuildPaired();
        scope.Mobile.OpenSettingsCommand.Execute(null);

        scope.Mobile.UnpairServerCommand.Execute(null);

        Assert.True(scope.Mobile.IsShowingConfirmUnpairServer);
        Assert.Equal("fp-attic", scope.Mobile.Main.PairedServerFingerprint);
        Assert.Equal(MainViewModel.UnpairConsequences("Attic"), scope.Mobile.ConfirmUnpairServerMessage);
    }

    [AvaloniaFact]
    public void Cancelling_keeps_the_pairing_and_goes_back_to_settings()
    {
        using var scope = BuildPaired();
        scope.Mobile.OpenSettingsCommand.Execute(null);
        scope.Mobile.UnpairServerCommand.Execute(null);

        scope.Mobile.CancelUnpairServerCommand.Execute(null);

        Assert.True(scope.Mobile.IsShowingSettings);
        Assert.Equal("fp-attic", scope.Mobile.Main.PairedServerFingerprint);
    }

    [AvaloniaFact]
    public void Confirming_unpairs_and_goes_back_to_settings()
    {
        using var scope = BuildPaired();
        scope.Mobile.OpenSettingsCommand.Execute(null);
        scope.Mobile.UnpairServerCommand.Execute(null);

        scope.Mobile.ConfirmUnpairServerCommand.Execute(null);

        Assert.True(scope.Mobile.IsShowingSettings);
        Assert.Null(scope.Mobile.Main.PairedServerFingerprint);
    }
}
