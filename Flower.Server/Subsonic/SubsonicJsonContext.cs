using System.Text.Json.Serialization;

namespace Flower.Server.Subsonic;

// subsonic-credentials.json, and nothing else.
//
// It was one entry in Flower.Core's FlowerCoreJsonContext, back when the
// credential store lived there. Nothing but this server ever read that file -
// a client holds no Subsonic credentials, it signs - so the store and its
// serialization came here together, which is what lets this folder be deleted
// without a dangling reference left in Flower.Core.
//
// Source-generated like the context it came from rather than reflection-based
// like the rest of this server: it costs nothing, and the shape is written to
// the owner's disk rather than to a socket.
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<SubsonicCredential>), TypeInfoPropertyName = "SubsonicCredentialList")]
internal partial class SubsonicJsonContext : JsonSerializerContext
{
}
