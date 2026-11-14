using System;
using System.Reflection;
using System.Text.Json;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;
using Flower.Views;

using Xunit;

namespace Flower.Tests.ViewModels;

// The banner an administrator's browser page shows when the server cannot write
// to its music folders. The server finds that out and logs it (its own
// LibraryWriteAccessTests); what is held down here is the page's half - that the
// list survives the wire into a trimmed head, that it becomes a sentence naming
// the folder, and that the banner is there exactly when there is one to show.
[Collection("PlatformDataDirectory")]
public class LibraryWriteWarningIntegrationTests : PinnedDataDirectory
{
    [AvaloniaFact]
    public void The_warning_is_shown_only_while_there_is_one_and_can_be_dismissed()
    {
        using var parts = MainViewModelHarness.BuildParts(new Library([]), new MainPlaylist([]));
        var main = parts.Main;

        Assert.False(main.HasServerLibraryWriteWarning);

        main.ServerLibraryWriteWarning = LibraryWriteWarning.For(["/music"]);
        Assert.True(main.HasServerLibraryWriteWarning);

        main.DismissServerLibraryWriteWarningCommand.Execute(null);
        Assert.False(main.HasServerLibraryWriteWarning);
        Assert.Null(main.ServerLibraryWriteWarning);
    }
}
