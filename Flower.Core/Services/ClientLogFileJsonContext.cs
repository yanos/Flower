using System;
using System.Text.Json.Serialization;

namespace Flower.Services;

internal sealed record ClientLogMetadata(string Fingerprint, string Alias, DateTimeOffset ReceivedAt);

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ClientLogMetadata))]
[JsonSerializable(typeof(LogEntryDto))]
internal partial class ClientLogFileJsonContext : JsonSerializerContext
{
}
