using System;
using System.Collections.Generic;

using Flower.Logging;

namespace Flower.Services;

// Wire shape for POST /api/flower/v1/log/report - a Client pushes its own
// recent log lines to its paired Server as one extra step inside the same
// sync session LibrarySyncService.SyncWithAsync already runs (see that
// class's PushLogSnapshotAsync). Never pulled by a Server - see
// SyncRolePolicy's own doc comment on why a Server never dials out.
public sealed record LogEntryDto(DateTimeOffset Timestamp, string Level, string? SourceContext, string Message, string? Exception)
{
    public static LogEntryDto FromEntry(InMemoryLogEntry entry) =>
        new(entry.Timestamp, entry.Level, entry.SourceContext, entry.Message, entry.Exception);
}

public sealed record LogReportDto(string DeviceFingerprint, string Alias, DateTimeOffset CapturedAt, List<LogEntryDto> Entries);
