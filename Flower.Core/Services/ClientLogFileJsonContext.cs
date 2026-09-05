using System;
using System.Text.Json.Serialization;

namespace Flower.Services;

internal sealed record ClientLogMetadata(string Fingerprint, string Alias, DateTimeOffset ReceivedAt);

// WriteIndented must stay false here, and this is the one on-disk format where
// that is true. The log files are .jsonl - one entry per line, read back a line
// at a time in LoadEntries - so an indented entry is not a prettier file, it is
// an unparseable one. Files that are whole JSON documents go through
// AtomicJsonFile, which indents them whatever their context says.
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ClientLogMetadata))]
[JsonSerializable(typeof(LogEntryDto))]
internal partial class ClientLogFileJsonContext : JsonSerializerContext
{
}
