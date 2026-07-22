using System;
using System.Collections.Generic;
using System.Text.Json;

using Flower.Logging;
using Flower.Services;

namespace Flower.Tests;

public class LogSyncContractsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void LogEntryDto_FromEntry_maps_every_field()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var entry = new InMemoryLogEntry(timestamp, "Error", "Flower.Services.Foo", "Something failed", "System.Exception: boom");

        var dto = LogEntryDto.FromEntry(entry);

        Assert.Equal(timestamp, dto.Timestamp);
        Assert.Equal("Error", dto.Level);
        Assert.Equal("Flower.Services.Foo", dto.SourceContext);
        Assert.Equal("Something failed", dto.Message);
        Assert.Equal("System.Exception: boom", dto.Exception);
    }

    [Fact]
    public void LogReportDto_round_trips_through_json_including_nulls_and_empty_entries()
    {
        var original = new LogReportDto("fp-123", "Yanos's MacBook", DateTimeOffset.UtcNow, new List<LogEntryDto>
        {
            new(DateTimeOffset.UtcNow, "Information", null, "No source context or exception", null)
        });

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<LogReportDto>(json, JsonOptions);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.DeviceFingerprint, roundTripped!.DeviceFingerprint);
        Assert.Equal(original.Alias, roundTripped.Alias);
        Assert.Single(roundTripped.Entries);
        Assert.Null(roundTripped.Entries[0].SourceContext);
        Assert.Null(roundTripped.Entries[0].Exception);
        Assert.Equal(original.Entries[0].Message, roundTripped.Entries[0].Message);
    }

    [Fact]
    public void LogReportDto_round_trips_with_an_empty_entries_list()
    {
        var original = new LogReportDto("fp-456", "Some Device", DateTimeOffset.UtcNow, new List<LogEntryDto>());

        var json = JsonSerializer.Serialize(original, JsonOptions);
        var roundTripped = JsonSerializer.Deserialize<LogReportDto>(json, JsonOptions);

        Assert.NotNull(roundTripped);
        Assert.Empty(roundTripped!.Entries);
    }
}
