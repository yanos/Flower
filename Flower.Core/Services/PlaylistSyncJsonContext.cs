using System.Text.Json.Serialization;

namespace Flower.Services;

// Trim/AOT-safe source-generated metadata for reading a playlist manifest off
// the wire (GET /api/flower/v1/playlists - see PlaylistSyncContracts). Here for
// the same reason LibrarySyncJsonContext is: OriginPlaylistImporter lives in
// this project so the browser head can use it, and the app's FlowerJsonContext
// is internal to Flower.
//
// The options are the wire format rather than a preference, and are the ones
// both serializing sides already write - SyncHttpServer through
// FlowerJsonContext, Flower.Server through SyncEndpoints' JsonOptions.
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PlaylistSyncManifestDto))]
public partial class PlaylistSyncJsonContext : JsonSerializerContext
{
}
