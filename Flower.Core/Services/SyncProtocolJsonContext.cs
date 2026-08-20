using System.Text.Json.Serialization;

namespace Flower.Services;

// Trim/AOT-safe source-generated metadata for the LocalSend-derived identity
// handshake (SyncProtocol.InfoPath). Public and in Flower.Core because both
// ends now serialize it: the app's SyncHttpServer and Flower.Server's own
// /info endpoint. The camelCase policy is the wire format, not a preference -
// NetworkDiscoveryService reads the response by raw property name.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(SyncInfoResponseDto))]
public partial class SyncProtocolJsonContext : JsonSerializerContext
{
}
