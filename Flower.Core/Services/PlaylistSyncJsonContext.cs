using System.Text.Json.Serialization;

namespace Flower.Services;

// Trim/AOT-safe source-generated metadata for the playlist sync wire
// (GET /api/flower/v1/playlists and POST /playlists/changes - see
// PlaylistSyncContracts). Here for
// the same reason LibrarySyncJsonContext is: OriginPlaylistImporter lives in
// this project so the browser head can use it, and the app's FlowerJsonContext
// is internal to Flower.
//
// The options are the wire format rather than a preference, and are the ones
// both sides already write - the client through FlowerJsonContext, Flower.Server
// through SyncEndpoints' JsonOptions.
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PlaylistSyncManifestDto))]
[JsonSerializable(typeof(PlaylistChangesRequestDto))]
[JsonSerializable(typeof(PlaylistSyncPlaylistDto))]
[JsonSerializable(typeof(PlaylistChangesResponseDto))]
public partial class PlaylistSyncJsonContext : JsonSerializerContext
{
}
