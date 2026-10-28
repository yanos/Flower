using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Logging;
using Flower.Persistence;
using Flower.ViewModels;

using Serilog.Events;

using Xunit;

namespace Flower.Tests.Logging;

// The log viewer's own rules, over a log handed to it: repeated lines grouped
// into one, what Export writes, and the text size's steps.
public class LogViewerViewModelTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 22, 27, TimeSpan.Zero);

    private static InMemoryLogEntry Entry(int second, string message, string source = "Flower.Sync", string level = "Information") =>
        new(At.AddSeconds(second), level, source, message, null);

    private static LogViewerViewModel Make(bool grouping)
    {
        var vm = new LogViewerViewModel(new AppSettings());
        vm.IsGroupingRepeats = grouping;
        return vm;
    }

    [Fact]
    public void Consecutive_repeats_from_one_source_become_one_line_saying_how_many_and_since_when()
    {
        var vm = Make(grouping: true);

        vm.ShowLog([
            Entry(0, "retrying"), Entry(1, "retrying"), Entry(2, "retrying"),
            Entry(3, "retrying", source: "Flower.Other"),
            Entry(4, "done"),
            Entry(5, "retrying"),
        ]);

        Assert.Equal(4, vm.DisplayLines.Count);
        Assert.Contains("retrying  ×3 · since 12:22:27", vm.DisplayLines[0]);
        Assert.StartsWith(At.AddSeconds(2).ToString("HH:mm:ss.fff"), vm.DisplayLines[0]);
        Assert.DoesNotContain("×", vm.DisplayLines[1]);
        Assert.DoesNotContain("×", vm.DisplayLines[3]);
    }

    [Fact]
    public void Turned_off_every_line_shows_again()
    {
        var vm = Make(grouping: true);
        vm.ShowLog([Entry(0, "retrying"), Entry(1, "retrying")]);

        vm.IsGroupingRepeats = false;

        Assert.Equal(2, vm.DisplayLines.Count);
    }

    // A live log: a repeat of the line last on screen rewrites that line,
    // and only something new is appended.
    [AvaloniaFact]
    public void A_repeat_arriving_rewrites_the_last_line_rather_than_adding_one()
    {
        var vm = Make(grouping: true);
        vm.ShowLog([Entry(0, "retrying")]);
        var replaced = new List<string>();
        var appended = new List<IReadOnlyList<string>>();
        vm.LastLineReplaced += (_, line) => replaced.Add(line);
        vm.LinesAppended += (_, lines) => appended.Add(lines);

        vm.Append(Entry(1, "retrying"));
        vm.Append(Entry(2, "done"));
        vm.Append(Entry(3, "done"));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("×2", Assert.Single(replaced));
        var batch = Assert.Single(appended);
        Assert.Contains("done  ×2", Assert.Single(batch));
        Assert.Equal(2, vm.DisplayLines.Count);
    }

    [Fact]
    public void Export_is_the_whole_log_whatever_is_filtered_or_grouped()
    {
        var vm = Make(grouping: true);
        vm.ShowLog([Entry(0, "retrying", level: "Debug"), Entry(1, "retrying", level: "Debug"), Entry(2, "done")]);
        vm.MinimumLevel = LogEventLevel.Information;
        vm.FilterText = "done";

        var lines = vm.ExportText.Split(Environment.NewLine);

        Assert.Equal(3, lines.Length);
        Assert.Single(vm.DisplayLines);
    }

    [Fact]
    public void Text_size_steps_through_the_sizes_and_stops_at_either_end()
    {
        var vm = new LogViewerViewModel(new AppSettings { LogFontSize = 12 });

        vm.LargerText();
        Assert.Equal(13, vm.FontSize);
        Assert.Equal("13 px", vm.FontSizeLabel);

        for (var i = 0; i < 20; i++)
            vm.SmallerText();
        Assert.Equal(LogViewerViewModel.FontSizeOptions[0], vm.FontSize);
        Assert.False(vm.CanMakeTextSmaller);

        for (var i = 0; i < 20; i++)
            vm.LargerText();
        Assert.Equal(LogViewerViewModel.FontSizeOptions[^1], vm.FontSize);
        Assert.False(vm.CanMakeTextLarger);
    }
}
