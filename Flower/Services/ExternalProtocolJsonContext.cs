using System.Text.Json.Serialization;

namespace Flower.Services
{
    // Trim/AOT-safe source-generated metadata for wire shapes this device
    // does NOT get to choose the casing/format of - real third-party specs it
    // has to match byte-for-byte: OpenSubsonic (SubsonicEnvelope, for actual
    // interop with Navidrome/Jellyfin-compat servers and any third-party
    // Subsonic client) and LocalSend's own open protocol (SyncInfoResponseDto,
    // the /api/localsend/v2/info response - NetworkDiscoveryService.cs also
    // reads this JSON by raw lowercase property name, e.g. "alias"/
    // "trustsCaller", so the camelCase policy below isn't just cosmetic).
    // Everything Flower controls both ends of (local files, its own
    // /api/flower/v1/* protocol) lives in FlowerJsonContext instead.
    // SubsonicEnvelope here is duplicated in Flower.Core's own
    // OpenSubsonicJsonContext (used by OpenSubsonicClient, which can't
    // reference this Flower-only context because it also covers
    // SyncInfoResponseDto below) - two source-generated contexts for the same
    // type is harmless, just some generated-code duplication.
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString)]
    [JsonSerializable(typeof(SubsonicEnvelope))]
    [JsonSerializable(typeof(SyncHttpServer.SyncInfoResponseDto))]
    internal partial class ExternalProtocolJsonContext : JsonSerializerContext
    {
    }
}
