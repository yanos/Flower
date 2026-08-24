using System.Text.Json.Serialization;

namespace Flower.Services;

// Trim/AOT-safe source-generated metadata for the LocalSend-derived identity
// handshake (SyncProtocol.InfoPath). Public and in Flower.Core because it is a
// shape both ends have to agree on: Flower.Server's /info endpoint writes it,
// and the client reads it. The camelCase policy is the wire format, not a
// preference - NetworkDiscoveryService reads the response by raw property
// name.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(SyncInfoResponseDto))]
public partial class SyncProtocolJsonContext : JsonSerializerContext
{
}
