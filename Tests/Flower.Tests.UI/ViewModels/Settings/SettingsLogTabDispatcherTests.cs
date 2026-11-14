using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

using AvaloniaEdit;

using Flower.Logging;
using Flower.Persistence;
using Flower.ViewModels;
using Flower.Views;

using Serilog.Events;

using Xunit;

namespace Flower.Tests.ViewModels.Settings;

// The Logs tab of a *server's* settings - the one screen that shows everybody's
// log: the server's own, and the last snapshot each device on its roster pushed
// to it (see ClientLogStore on the receiving side). A client's own Log window
// deliberately shows only itself, so this is where the rows that used to be in
// that window's sidebar went - and, since it is the same LogViewerViewModel
// behind both, they arrive with the filtering and level picker the flat
// 500-line TextBox here never had.
public class SettingsLogTabDispatcherTests : Flower.Tests.TestSupport.SettingsLogTabHarness
{
    // Loads the real XAML for the viewer control the Logs tab hosts, and pins
    // the behaviour that only shows up once it is on screen: it paints what the
    // ViewModel already holds when it attaches. The tab is realized long after
    // the log was fetched, so a control that waited for the next event would sit
    // blank until something changed.
    [AvaloniaFact]
    public async Task The_viewer_paints_the_lines_that_were_loaded_before_it_came_on_screen()
    {
        _backend.ServerLog = Lines("a line loaded before the pane existed");
        var panel = await MakeAsync();

        var viewer = new LogViewer { DataContext = panel.LogViewer };
        var window = new Window { Content = viewer, Width = 800, Height = 600 };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var editor = viewer.GetVisualDescendants().OfType<TextEditor>().Single();
        Assert.Contains("a line loaded before the pane existed", editor.Text);

        window.Close();
    }

    // Everything below is the tab following a live log rather than waiting to be
    // asked. SettingsPanel ticks FollowLogAsync on a timer while the Logs tab is
    // the one on screen; these call the tick directly, since waiting out real
    // two-second intervals would be testing DispatcherTimer instead.

    // AvaloniaFact for the Dispatcher alone: the viewer coalesces appended
    // entries on a background dispatch (see LogViewerViewModel.Append), so
    // without one to run there is nothing to coalesce them onto.
    [AvaloniaFact]
    public async Task A_poll_appends_only_what_has_been_logged_since()
    {
        _backend.ServerLog = Lines("first");
        var panel = await MakeAsync();

        _backend.ServerLog.AddRange(Lines("second"));
        await panel.FollowLogAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, panel.LogViewer.DisplayLines.Count);
        Assert.Contains("first", panel.LogViewer.DisplayLines[0]);
        Assert.Contains("second", panel.LogViewer.DisplayLines[1]);
    }

    // A full read of a remote server's log can take longer than the two seconds
    // between ticks, and the tick used to go out from the same cursor - so it
    // brought back the whole buffer a second time and appended it underneath
    // the copy the full read had just painted. Whichever read lands second has
    // to see that the cursor moved while it was away, and drop its answer.
    [AvaloniaFact]
    public async Task A_poll_racing_a_full_read_does_not_show_the_log_twice()
    {
        _backend.ServerLog = Lines("first", "second");
        var panel = await MakeAsync();

        var gate = new TaskCompletionSource();
        _backend.ServerLogGate = gate;
        var refresh = panel.RefreshLogCommand.ExecuteAsync(null);
        var poll = panel.FollowLogAsync();
        _backend.ServerLogGate = null;
        gate.SetResult();
        await Task.WhenAll(refresh, poll);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, panel.LogViewer.DisplayLines.Count);

        _backend.ServerLog.AddRange(Lines("third"));
        await panel.FollowLogAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, panel.LogViewer.DisplayLines.Count);
        Assert.Contains("third", panel.LogViewer.DisplayLines[2]);
    }
}
