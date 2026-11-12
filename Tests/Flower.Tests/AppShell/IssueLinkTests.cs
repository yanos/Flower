using System;
using System.Linq;

using Flower.Logging;
using Flower.Services;

using Xunit;

namespace Flower.Tests.AppShell;

// The GitHub issue a crash banner opens: its title and body already written,
// the home folder written as ~, and short enough that GitHub will open it.
public class IssueLinkTests
{
    private const string Home = "/Users/someone";
    private static readonly DateTimeOffset When = new(2026, 11, 12, 9, 30, 0, TimeSpan.Zero);

    private static string Query(Uri link, string name)
    {
        foreach (var pair in link.Query.TrimStart('?').Split('&'))
        {
            var separator = pair.IndexOf('=');
            if (pair[..separator] == name)
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        throw new Xunit.Sdk.XunitException($"No {name} in {link}");
    }

    private static PendingCrash Crash(string details, string? version = "0.9.0+abc", string? logFile = null) =>
        new("System.InvalidOperationException: The queue was empty", details, version, logFile, When);

    [Fact]
    public void A_crash_is_titled_by_its_exception_and_carries_its_stack_and_build()
    {
        var link = IssueLink.ForCrash(Crash("   at Flower.Playback.Next()\n   at Flower.App.Run()"), "0.9.1", "macOS 26.1 (Arm64)", Home);

        Assert.StartsWith(IssueLink.NewIssuePage + "?", link.AbsoluteUri);
        Assert.Equal("Crash: System.InvalidOperationException: The queue was empty", Query(link, "title"));
        var body = Query(link, "body");
        Assert.Contains("at Flower.Playback.Next()", body);
        Assert.Contains("0.9.0+abc", body);
        Assert.Contains("macOS 26.1 (Arm64)", body);
    }

    [Fact]
    public void The_home_folder_is_written_as_a_tilde_wherever_it_appears()
    {
        var crash = Crash($"   at Flower.Importer.Scan() in {Home}/Music/Secret Album/01.flac", logFile: $"{Home}/Library/Application Support/Flower/logs/flower-1.log");

        var body = Query(IssueLink.ForCrash(crash, "0.9.1", "macOS", Home), "body");

        Assert.DoesNotContain("someone", body);
        Assert.Contains("~/Music/Secret Album/01.flac", body);
        Assert.Contains("~/Library/Application Support/Flower/logs/flower-1.log", body);
    }

    [Fact]
    public void A_windows_home_folder_is_redacted_whichever_way_its_slashes_lean()
    {
        var text = IssueLink.Redact(@"C:\Users\someone\Music\a.flac and C:/Users/someone/logs", @"C:\Users\someone");

        Assert.Equal(@"~\Music\a.flac and ~/logs", text);
    }

    // GitHub refuses a URL past about 8 KB, so a deep stack keeps its top -
    // where it threw - and loses its outermost frames.
    [Fact]
    public void A_stack_too_long_for_a_link_keeps_its_top_frames_and_still_fits()
    {
        var frames = string.Join('\n', Enumerable.Range(0, 2000).Select(i => $"   at Flower.Deep.Frame{i}()"));

        var link = IssueLink.ForCrash(Crash(frames), "0.9.1", "Linux", Home);

        Assert.True(link.AbsoluteUri.Length <= IssueLink.MaxLength, $"{link.AbsoluteUri.Length} characters");
        var body = Query(link, "body");
        Assert.Contains("Frame0()", body);
        Assert.DoesNotContain("Frame1999()", body);
        Assert.Contains("the log has the rest", body);
        Assert.Contains("Linux", body);
    }

    // Found by the scanner at the next launch, a native crash's build is not
    // known; the body says which build found it instead of guessing.
    [Fact]
    public void A_crash_from_an_unknown_build_names_the_build_that_found_it()
    {
        var body = Query(IssueLink.ForCrash(Crash("", version: null), "0.9.1", "Linux", Home), "body");

        Assert.Contains("found by 0.9.1", body);
    }

    [Fact]
    public void The_general_link_carries_the_build_and_platform()
    {
        var body = Query(IssueLink.General("0.9.1+abc", "Windows 11 (X64)"), "body");

        Assert.Contains("0.9.1+abc", body);
        Assert.Contains("Windows 11 (X64)", body);
    }
}
