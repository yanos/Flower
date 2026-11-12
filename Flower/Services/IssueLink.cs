using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

using Flower.Logging;

namespace Flower.Services;

// A new GitHub issue with its title and body already written - the whole of
// Flower's crash reporting (docs/CRASH-REPORTING-PLAN.md, Option A). No
// token, no server, no account of Flower's own: the link opens GitHub's
// new-issue page in the browser, and the person reads what it says before
// anything is sent. That page is the review step the plan asks for, since a
// stack trace can carry real paths.
//
// What can be redacted here is: the home folder, which names the person, is
// written as ~. The log file is not attached - a link cannot carry one - so
// the body says where it is and that it names songs and folders.
public static class IssueLink
{
    public const string NewIssuePage = "https://github.com/" + GitHubReleaseUpdater.Repository + "/issues/new";

    // Not GitHub's own ceiling (it answers 500 from about 7 KB) but a lower
    // one: someone signed out is sent to log in first, carrying the link in
    // return_to, and past roughly 5.7 KB GitHub drops return_to - the person
    // logs in to a blank page and the report is gone. Measured signed out in
    // November 2026; this leaves room under it.
    public const int MaxLength = 5000;

    // An issue title past this is cut: GitHub's own limit is far higher, but
    // a title that long is a message pasted into the wrong box.
    private const int MaxTitle = 140;
    private const int MaxSummary = 600;

    private const string Trimmed = "… (cut to fit a link; the log has the rest)";

    public static string Platform =>
        $"{RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.OSArchitecture})";

    public static string? HomeDirectory
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(home) ? null : home;
        }
    }

    // The About box's and Settings' "Report an issue": nothing to tell but
    // which build and where.
    public static Uri General(string version, string platform)
    {
        var body = new StringBuilder()
            .AppendLine("What happened, and what did you expect instead?")
            .AppendLine()
            .AppendLine()
            .AppendLine()
            .AppendLine("---")
            .Append("Flower ").Append(version).Append(" · ").AppendLine(platform)
            .ToString();
        return Build("", body, label: null);
    }

    public static Uri ForCrash(PendingCrash crash, string version, string platform, string? homeDirectory)
    {
        var summary = Cut(Redact(crash.Summary, homeDirectory), MaxSummary);
        var details = Redact(crash.Details, homeDirectory).Replace("\r\n", "\n").TrimEnd();
        var logFile = crash.LogFile is { Length: > 0 } log ? Redact(log, homeDirectory) : null;
        var title = Cut($"Crash: {summary.Split('\n', 2)[0]}", MaxTitle);

        string Body(string shownDetails)
        {
            var body = new StringBuilder()
                .AppendLine("Flower quit unexpectedly. What were you doing just before?")
                .AppendLine()
                .AppendLine()
                .AppendLine()
                .AppendLine("### What happened")
                .AppendLine()
                .AppendLine(summary);
            if (shownDetails.Length > 0)
            {
                body.AppendLine()
                    .AppendLine("````text")
                    .AppendLine(shownDetails)
                    .AppendLine("````");
            }

            body.AppendLine()
                .AppendLine("### Where")
                .AppendLine()
                .Append("- Flower ").AppendLine(crash.Version ?? $"(not known - found by {version} at the next launch)")
                .Append("- ").AppendLine(platform)
                .Append("- ").AppendLine(crash.When.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture));
            if (logFile != null)
            {
                body.AppendLine()
                    .AppendLine("### The log")
                    .AppendLine()
                    .Append("The rest of the story is in `").Append(logFile).AppendLine("`.")
                    .AppendLine("Dragging it into this box helps a lot. It names your songs and folders, so read it over first.");
            }

            return body.ToString();
        }

        // As much of the details as fits, from the top - the throwing frame
        // and its callers are what says where; the outermost frames rarely do.
        var lines = details.Length == 0 ? Array.Empty<string>() : details.Split('\n');
        var full = Build(title, Body(details), "bug");
        if (full.AbsoluteUri.Length <= MaxLength)
            return full;

        int fits = 0, doesNot = lines.Length;
        while (doesNot - fits > 1)
        {
            var middle = (fits + doesNot) / 2;
            if (Build(title, Body(Kept(lines, middle)), "bug").AbsoluteUri.Length <= MaxLength)
                fits = middle;
            else
                doesNot = middle;
        }

        return Build(title, Body(Kept(lines, fits)), "bug");
    }

    private static string Kept(string[] lines, int count) =>
        string.Join('\n', lines.Take(count).Append(Trimmed));

    // The home folder as ~, written either way round on Windows, where a
    // path in a stack trace and one in a log line can disagree on slashes.
    public static string Redact(string text, string? homeDirectory)
    {
        if (string.IsNullOrEmpty(homeDirectory) || homeDirectory.Length < 2)
            return text;

        var home = homeDirectory.TrimEnd('/', '\\');
        return text
            .Replace(home, "~", StringComparison.OrdinalIgnoreCase)
            .Replace(home.Replace('\\', '/'), "~", StringComparison.OrdinalIgnoreCase);
    }

    private static string Cut(string text, int length) =>
        text.Length <= length ? text : text[..(length - 1)] + "…";

    private static Uri Build(string title, string body, string? label)
    {
        var query = new StringBuilder("?title=").Append(Uri.EscapeDataString(title))
            .Append("&body=").Append(Uri.EscapeDataString(body));
        if (label != null)
            query.Append("&labels=").Append(Uri.EscapeDataString(label));
        return new Uri(NewIssuePage + query);
    }
}
