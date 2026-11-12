using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Sentry;
using Sentry.Protocol;

namespace Flower.Logging
{
    // What leaves a device, made anonymous. The rules (docs/PRIVACY.md):
    // exception types and the names of the
    // methods on the stack stay, because they are what fixes the bug; anything
    // that could name a person, a song, a folder or a server goes.
    public static class CrashScrubber
    {
        // An absolute path, Unix or Windows. Folder names have spaces in them
        // ("/Music/Secret Artist/01 Song.flac"), so a path runs on across
        // spaces to the first thing that ends like a file name - and with no
        // such ending, to the end of the line. Too much hidden is a less
        // useful report; too little is someone's library. The extension
        // survives (<path>.flac says which decoder), the names do not.
        private static readonly Regex AbsolutePath = new(
            @"(?:[A-Za-z]:[\\/]|\\\\|(?<![\w.~])/)(?:[^'""<>|\r\n]*?(?<ext>\.[A-Za-z0-9]{1,5})(?=[\s'""<>|,;:!)\]]|$)|[^'""<>|\r\n]*)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex Url = new(@"\b[a-z][a-z0-9+.-]*://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Where a message interpolates a name: 'Song Title', "Album".
        private static readonly Regex Quoted = new(@"'[^'\r\n]*'|""[^""\r\n]*""|‘[^’\r\n]*’|“[^”\r\n]*”", RegexOptions.Compiled);

        public static string Text(string text, string? homeDirectory)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            if (!string.IsNullOrEmpty(homeDirectory) && homeDirectory.Length > 1)
            {
                var home = homeDirectory.TrimEnd('/', '\\');
                text = text.Replace(home, "~", StringComparison.OrdinalIgnoreCase)
                    .Replace(home.Replace('\\', '/'), "~", StringComparison.OrdinalIgnoreCase);
            }

            text = Url.Replace(text, "<url>");
            text = text.Replace("~/", "/").Replace("~\\", "\\");
            text = AbsolutePath.Replace(text, match => "<path>" + match.Groups["ext"].Value);
            return Quoted.Replace(text, "'…'");
        }

        // A source file's path, kept to its name: which file threw is useful,
        // where the build machine keeps it is not.
        public static string? FileName(string? path) =>
            string.IsNullOrEmpty(path) ? path : Path.GetFileName(path.Replace('\\', '/'));

        public static void Scrub(SentryEvent sentryEvent, string? homeDirectory, string? installId = null)
        {
            ScrubIdentity(sentryEvent, installId);
            sentryEvent.ServerName = null;

            if (sentryEvent.Message is { } message)
            {
                message.Formatted = message.Formatted is { } formatted ? Text(formatted, homeDirectory) : null;
                message.Message = message.Message is { } template ? Text(template, homeDirectory) : null;
                message.Params = null;
            }

            foreach (var exception in sentryEvent.SentryExceptions ?? Enumerable.Empty<SentryException>())
            {
                if (exception.Value is { } value)
                    exception.Value = Text(value, homeDirectory);
                foreach (var frame in exception.Stacktrace?.Frames ?? Enumerable.Empty<SentryStackFrame>())
                {
                    frame.FileName = FileName(frame.FileName);
                    frame.AbsolutePath = FileName(frame.AbsolutePath);
                    frame.Vars.Clear();
                }
            }

            foreach (var thread in sentryEvent.SentryThreads ?? Enumerable.Empty<SentryThread>())
            {
                thread.Name = null;
                foreach (var frame in thread.Stacktrace?.Frames ?? Enumerable.Empty<SentryStackFrame>())
                {
                    frame.FileName = FileName(frame.FileName);
                    frame.AbsolutePath = FileName(frame.AbsolutePath);
                }
            }

            // Symbols are matched by their debug ID; where the build machine
            // kept the PDB is only its user's name.
            foreach (var image in sentryEvent.DebugImages ?? Enumerable.Empty<DebugImage>())
            {
                image.CodeFile = FileName(image.CodeFile);
                image.DebugFile = FileName(image.DebugFile);
            }

            foreach (var key in sentryEvent.Extra.Keys.ToList())
            {
                if (sentryEvent.Extra[key] is string extra)
                    sentryEvent.SetExtra(key, Text(extra, homeDirectory));
            }

        }

        // A trace: its spans' descriptions are URLs and queries - an online
        // lookup's carries the song it looks up - so they get the same rules.
        public static void Scrub(SentryTransaction transaction, string? homeDirectory, string? installId = null)
        {
            ScrubIdentity(transaction, installId);
            foreach (var span in transaction.Spans)
            {
                if (span.Description is { } description)
                    span.Description = Text(description, homeDirectory);
                foreach (var key in span.Data.Keys.ToList())
                {
                    if (span.Data[key] is string value)
                        span.SetData(key, Text(value, homeDirectory));
                }
            }
        }

        // Nothing that says who or where: the device's name, its boot time (as
        // good as a serial number), the identifiers Sentry's phone SDKs add of
        // their own - iOS's device_app_hash, Android's
        // device_unique_identifier - which are permanent, so they would
        // outlive the install ID they duplicate, switch or no switch, and its
        // location - Sentry works a city out
        // from the connection's address unless the event names one, so it
        // names an address that has none. The only identity left is
        // CrashReportingId's random one, which counts installs and says
        // nothing about them. And a server's request: the route stays; its
        // query (a stream ticket is a credential), headers, cookies and body
        // go. The machine's name, an event's alone, goes in Scrub.
        public static void ScrubIdentity(IEventLike eventLike, string? installId = null)
        {
            eventLike.User = new SentryUser { Id = installId, IpAddress = "0.0.0.0" };
            eventLike.Contexts.Device.Name = null;
            eventLike.Contexts.Device.BootTime = null;
            eventLike.Contexts.App.Hash = null;
            eventLike.Contexts.Device.DeviceUniqueIdentifier = null;

            var request = eventLike.Request;
            request.QueryString = null;
            request.Cookies = null;
            request.Data = null;
            request.Headers.Clear();
            request.Env.Clear();
            if (request.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var parsed))
                request.Url = parsed.AbsolutePath;
        }
    }
}
