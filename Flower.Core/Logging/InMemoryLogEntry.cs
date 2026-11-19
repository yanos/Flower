using System;

namespace Flower.Logging
{
    // One captured log line, shared shape for both the local live view (see
    // InMemoryLogStore) and a synced peer's pushed snapshot (see
    // Flower.Services.LogEntryDto) - both render through ToDisplayLine so a
    // remote client's log looks identical to reading it locally.
    public sealed record InMemoryLogEntry(DateTimeOffset Timestamp, string Level, string? SourceContext, string Message, string? Exception)
    {
        // Mirrors AppLogging's own output template shape, just without
        // Serilog's formatter - this runs over already-rendered strings, not a
        // live LogEvent. Dated like the log file rather than timed like the
        // console: a server's log runs for days, and a time alone cannot say
        // which. LogLevelColorizer reads the stamp's length off this shape.
        public string ToDisplayLine()
        {
            var line = $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level.ToUpperInvariant()[..Math.Min(3, Level.Length)]}] {SourceContext}: {Message}";
            return Exception == null ? line : $"{line}{Environment.NewLine}{Exception}";
        }
    }
}
