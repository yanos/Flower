using System;
using System.Collections.Generic;

using Flower.Logging;

namespace Flower.Services;

// Wire shape for POST /api/flower/v1/log/report - a device pushes its own
// recent log lines to its paired server as one extra step inside the same sync
// session LibrarySyncService.SyncWithAsync already runs (see that class's
// PushLogSnapshotAsync). Always a push, never a pull: a server does not dial
// out, and the device it would want to ask is usually a phone that is asleep.
public sealed record LogEntryDto(DateTimeOffset Timestamp, string Level, string? SourceContext, string Message, string? Exception)
{
    public static LogEntryDto FromEntry(InMemoryLogEntry entry) =>
        new(entry.Timestamp, entry.Level, entry.SourceContext, entry.Message, entry.Exception);
}

public sealed record LogReportDto(string DeviceFingerprint, string Alias, DateTimeOffset CapturedAt, List<LogEntryDto> Entries);

// What the server already holds for one device, so the client can send only
// what is missing instead of re-offering its whole retained week for the server
// to hash and discard. Served by GET /api/flower/v1/log/watermark and returned
// again as the body of every POST /log/report, so a client that is already
// pushing never has to ask a second time.
//
// The event id is not decoration: log timestamps are only as fine-grained as
// the platform clock (coarse enough on Windows for a burst to share one), so a
// timestamp alone would force a choice between re-sending the newest line
// forever and silently dropping its neighbours. The pair is exact - the client
// sends everything ordered after it by ClientLogStore.Ordered.
//
// Both null when the server has nothing for this device at all: send the lot.
public sealed record LogWatermarkDto(DateTimeOffset? LastEntryTimestamp, string? LastEventId);
