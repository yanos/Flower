using System.Text.Json.Serialization;

namespace Flower.Services
{
    // Trim/AOT-safe source-generated metadata for the OpenSubsonic wire envelope
    // this device does NOT get to choose the casing/format of (a real third-party
    // spec it has to match byte-for-byte) - used by OpenSubsonicClient only.
    // Flower's own app has an equivalent context for the same SubsonicEnvelope
    // type (Flower.Services.ExternalProtocolJsonContext) - duplicated rather
    // than shared because this one is internal to Flower.Core.
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString)]
    [JsonSerializable(typeof(SubsonicEnvelope))]
    internal partial class OpenSubsonicJsonContext : JsonSerializerContext
    {
    }
}
