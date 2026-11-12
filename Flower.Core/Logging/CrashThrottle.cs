using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using Sentry;
using Sentry.Protocol;

namespace Flower.Logging
{
    // How often one device reports the same problem: at most once an hour.
    // Not once ever, because how often a crash happens and to how many people
    // is what decides which to fix first - so every occurrence is counted, and
    // the ones held back ride along with the next report as its "occurrences".
    // Not every time, because a crash in a loop on one device would spend the
    // month's shared quota for everyone. Sentry's event count is then how many
    // device-hours it happened in, its user count how many installs
    // (CrashReportingId), and the occurrences the rest.
    //
    // Counts held back by a problem that then never comes again within the
    // file's memory are never reported: the price of not sending a report on
    // a timer of its own.
    //
    // Keyed by release plus what Sentry would group on - the exception types
    // and the innermost frames, or the message for a native crash - so a new
    // version starts afresh.
    public static class CrashThrottle
    {
        public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

        // A problem not seen for this long is forgotten, held counts and all.
        private static readonly TimeSpan Memory = TimeSpan.FromDays(30);

        private static readonly object Gate = new();

        public static string FilePath => Path.Combine(AppLogging.LogsDirectory, "crash-throttle.txt");

        // How many times it happened since this device last reported it, this
        // time included - or 0 to hold it back.
        public static int Admit(SentryEvent sentryEvent, DateTimeOffset now) =>
            Admit(Key(sentryEvent), sentryEvent.Release ?? "", now);

        public static int Admit(string key, string release, DateTimeOffset now)
        {
            var id = $"{release} {key}".Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
            lock (Gate)
            {
                var entries = Read(now);
                entries.TryGetValue(id, out var entry);
                if (entry.LastSent != default && now - entry.LastSent < Interval)
                {
                    entries[id] = (entry.LastSent, entry.Held + 1, now);
                    Write(entries);
                    return 0;
                }

                entries[id] = (now, 0, now);
                Write(entries);
                return entry.Held + 1;
            }
        }

        public static string Key(SentryEvent sentryEvent)
        {
            var exceptions = sentryEvent.SentryExceptions?.ToList();
            if (exceptions is not { Count: > 0 })
                return "message:" + (sentryEvent.Message?.Formatted ?? sentryEvent.Message?.Message ?? "");

            // Sentry lists a stack's frames caller first, so the innermost -
            // where it threw - are at the end.
            return string.Join(" | ", exceptions.Select(exception =>
            {
                var frames = exception.Stacktrace?.Frames ?? new List<SentryStackFrame>();
                var top = frames.Skip(Math.Max(0, frames.Count - 3)).Select(frame => frame.Function ?? "?");
                return $"{exception.Type}@{string.Join(">", top)}";
            }));
        }

        // One line a problem: when it was last reported, how many times it
        // has happened since, when it was last seen, and its key.
        private static Dictionary<string, (DateTimeOffset LastSent, int Held, DateTimeOffset LastSeen)> Read(DateTimeOffset now)
        {
            var entries = new Dictionary<string, (DateTimeOffset, int, DateTimeOffset)>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(FilePath))
                    return entries;

                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var fields = line.Split('\t', 4);
                    if (fields.Length == 4
                        && long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sent)
                        && int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var held)
                        && long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seen)
                        && now - DateTimeOffset.FromUnixTimeSeconds(seen) < Memory)
                    {
                        entries[fields[3]] = (DateTimeOffset.FromUnixTimeSeconds(sent), held, DateTimeOffset.FromUnixTimeSeconds(seen));
                    }
                }
            }
            catch
            {
                // Unreadable: start afresh, which at worst sends one report early.
            }

            return entries;
        }

        private static void Write(Dictionary<string, (DateTimeOffset LastSent, int Held, DateTimeOffset LastSeen)> entries)
        {
            try
            {
                Directory.CreateDirectory(AppLogging.LogsDirectory);
                File.WriteAllLines(FilePath, entries.Select(entry => string.Join('\t',
                    entry.Value.LastSent.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                    entry.Value.Held.ToString(CultureInfo.InvariantCulture),
                    entry.Value.LastSeen.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                    entry.Key)));
            }
            catch
            {
                // Unwritable: the report still goes; only the count is lost.
            }
        }
    }
}
