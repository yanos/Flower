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

namespace Flower.Tests;

// The banner an administrator's browser page shows when the server cannot write
// to its music folders. The server finds that out and logs it (its own
// LibraryWriteAccessTests); what is held down here is the page's half - that the
// list survives the wire into a trimmed head, that it becomes a sentence naming
// the folder, and that the banner is there exactly when there is one to show.
[Collection("PlatformDataDirectory")]
public class LibraryWriteWarningTests : PinnedDataDirectory
{
    // MainView cannot be built without a ColumnManager in Ioc.Default - see
    // MainViewServerSettingsPageTests.
    public LibraryWriteWarningTests() => TestIoc.EnsureConfigured();

    [Fact]
    public void A_server_that_can_write_everywhere_has_nothing_to_say()
    {
        Assert.Null(LibraryWriteWarning.For([]));
        // A server from before the field existed sends none at all.
        Assert.Null(LibraryWriteWarning.For(null));
    }

    [Fact]
    public void The_warning_names_the_folder_and_what_will_be_refused()
    {
        var warning = LibraryWriteWarning.For(["/music"]);

        Assert.Contains("/music", warning);
        Assert.Contains("cannot write", warning);
        Assert.Contains("uploads", warning);
        // And what still works, so it does not read as the server being broken.
        Assert.Contains("Songs still play", warning);
    }

    [Fact]
    public void Several_folders_are_all_named()
    {
        var warning = LibraryWriteWarning.For(["/music", "/mnt/more"]);

        Assert.Contains("/music", warning);
        Assert.Contains("/mnt/more", warning);
    }

    // Through the client's own options, which is what the browser head uses:
    // camelCase off the server, source-generated metadata, no reflection.
    [Fact]
    public void The_list_arrives_off_the_wire_in_the_shape_the_server_sends_it()
    {
        var options = (JsonSerializerOptions)typeof(ServerAdminClient)
            .GetField("Json", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        var status = JsonSerializer.Deserialize<AdminLibraryStatusDto>(
            """{"rescanning":false,"trackCount":16114,"lastCompletedAt":null,"lastError":null,"unwritableFolders":["/music"]}""",
            options);
        var older = JsonSerializer.Deserialize<AdminLibraryStatusDto>(
            """{"rescanning":false,"trackCount":3,"lastCompletedAt":null,"lastError":null}""",
            options);

        Assert.Equal(["/music"], status!.UnwritableFolders);
        Assert.Null(older!.UnwritableFolders);
    }

    [AvaloniaFact]
    public void The_banner_is_there_only_while_there_is_a_warning_and_can_be_dismissed()
    {
        using var parts = MainViewModelHarness.BuildParts(new Library([]), new MainPlaylist([]));
        var view = new MainView { DataContext = parts.Main };
        var window = new Window { Width = 900, Height = 500, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var banner = view.FindControl<Border>("LibraryWriteWarningBanner")!;

        Assert.False(banner.IsVisible);

        parts.Main.ServerLibraryWriteWarning = LibraryWriteWarning.For(["/music"]);
        Dispatcher.UIThread.RunJobs();

        Assert.True(banner.IsVisible);
        Assert.True(banner.Bounds.Height > 0);
        parts.Main.DismissServerLibraryWriteWarningCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(banner.IsVisible);
        Assert.Null(parts.Main.ServerLibraryWriteWarning);

        window.Close();
    }
}
