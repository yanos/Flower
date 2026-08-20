using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Flower.Persistence
{
    // Trim/AOT-safe source-generated metadata for the local files this project
    // owns (device-key.json, trusted-peers.json, denied-peers.json,
    // subsonic-credentials.json). Mirrors
    // Flower's own FlowerJsonContext, which covers everything else Flower
    // persists (settings.json, library.json, etc.) - split in two because
    // FlowerJsonContext also covers types that live in the Flower project and
    // can't be referenced from here.
    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(DeviceKeyMaterial))]
    [JsonSerializable(typeof(List<TrustedPeer>), TypeInfoPropertyName = "TrustedPeerList")]
    [JsonSerializable(typeof(List<DeniedPeer>), TypeInfoPropertyName = "DeniedPeerList")]
    [JsonSerializable(typeof(List<SubsonicCredential>), TypeInfoPropertyName = "SubsonicCredentialList")]
    internal partial class FlowerCoreJsonContext : JsonSerializerContext
    {
    }
}
