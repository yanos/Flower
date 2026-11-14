using System;
using System.IO;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;

using Flower.Persistence;
using Flower.Tests.TestSupport;
using Flower.Views;

using Xunit;

namespace Flower.Tests.MusicLibrary.Import;

// The first launch on a Mac with a Music.app library asks before using it. What
// is worth holding still is that nothing is taken *before* the answer, and that
// each answer leaves the settings exactly where the next launch needs them -
// both failures would be quiet ones: a library that filled itself while the
// question was still on screen, or a "no" that is asked again every launch.
[Collection("PlatformDataDirectory")]
public class ITunesLibraryOfferWindowTests : PinnedDataDirectory
{
    private const string Folder = "/Users/test/Music/Music/Media.localized/";

    // ── The window ────────────────────────────────────────────────────────

    private static (ITunesLibraryOfferWindow Dialog, System.Threading.Tasks.Task<ITunesLibraryOfferAnswer?> Answer) Ask()
    {
        var owner = new Window();
        owner.Show();
        var dialog = new ITunesLibraryOfferWindow(Folder);
        var answer = dialog.ShowDialog<ITunesLibraryOfferAnswer?>(owner);
        Dispatcher.UIThread.RunJobs();
        return (dialog, answer);
    }

    private static void Click(Window dialog, string buttonName)
    {
        dialog.FindControl<Button>(buttonName)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void It_names_the_folder_and_offers_both_imports_ticked()
    {
        var (dialog, _) = Ask();

        Assert.Equal(Folder, dialog.FindControl<TextBlock>("FolderText")!.Text);
        Assert.True(dialog.FindControl<CheckBox>("SyncPlayCountCheckBox")!.IsChecked);
        Assert.True(dialog.FindControl<CheckBox>("SyncDateAddedCheckBox")!.IsChecked);

        dialog.Close();
    }

    [AvaloniaFact]
    public void Adding_answers_with_what_is_ticked()
    {
        var (dialog, answer) = Ask();
        dialog.FindControl<CheckBox>("SyncDateAddedCheckBox")!.IsChecked = false;

        Click(dialog, "AddButton");

        Assert.True(answer.IsCompleted);
        Assert.Equal(new ITunesLibraryOfferAnswer(SyncPlayCount: true, SyncDateAdded: false), answer.Result);
    }

    [AvaloniaFact]
    public void Declining_answers_with_nothing_whatever_is_ticked()
    {
        var (dialog, answer) = Ask();

        Click(dialog, "DeclineButton");

        Assert.True(answer.IsCompleted);
        Assert.Null(answer.Result);
    }

    // Closing the window by its own button is a no, not a question left open:
    // the startup rescan is waiting on this answer.
    [AvaloniaFact]
    public void Closing_the_window_is_a_no()
    {
        var (dialog, answer) = Ask();

        dialog.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.True(answer.IsCompleted);
        Assert.Null(answer.Result);
    }
}
