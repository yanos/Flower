using System.Text.Json.Serialization;

namespace Flower.Services
{
    // Trim/AOT-safe source-generated metadata for the OpenSubsonic wire envelope
    // this device does NOT get to choose the casing/format of (a real third-party
    // spec it has to match byte-for-byte) - used by OpenSubsonicClient only.
    // Flower's own app (SyncHttpServer et al.) has an equivalent context for the
    // same SubsonicEnvelope type (Flower.Services.ExternalProtocolJsonContext) -
    // duplicated rather than shared because that one also covers
    // SyncHttpServer.SyncInfoResponseDto, which lives in the Flower project and
    // can't be referenced from here.
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString)]
    [JsonSerializable(typeof(SubsonicEnvelope))]
    internal partial class OpenSubsonicJsonContext : JsonSerializerContext
    {
    }
}
