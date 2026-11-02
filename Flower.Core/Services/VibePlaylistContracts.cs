using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Flower.Services;

// The wire for making and playing Vibe Playlists on a paired device
// (docs/VIBE-PLAYLIST-PLAN.md, "The wire"). Songs travel as playlist entries
// (PlaylistSyncTrackDto), so a device resolves them the way it resolves a
// synced playlist's: by the server's id, then by tags.

// POST /api/flower/v1/vibe/preview: what the editor shows while typing pauses.
public sealed record VibePreviewRequestDto(string Prompt, bool Instrumental = false);

// POST /api/flower/v1/vibe-playlists/{id}/extend: the endless queue running
// low. Exclude names the songs the device already has for this session beyond
// the pool (earlier extensions), by the server's id; the pool itself the
// server knows.
public sealed record VibeExtendRequestDto(List<string>? Exclude = null);

// The answer to both: songs, best first.
public sealed record VibeSongsDto(List<PlaylistSyncTrackDto> Tracks);

public static class VibeRoutes
{
    public const string Preview = "/api/flower/v1/vibe/preview";

    public static string Extend(System.Guid playlistId) => $"/api/flower/v1/vibe-playlists/{playlistId:D}/extend";
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(VibePreviewRequestDto))]
[JsonSerializable(typeof(VibeExtendRequestDto))]
[JsonSerializable(typeof(VibeSongsDto))]
public partial class VibePlaylistJsonContext : JsonSerializerContext
{
}
