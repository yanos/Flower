using System.Text.Json;
using System.Text.Json.Serialization;

using Flower.Services;

namespace Flower.Server.Services;

// Builds the "subsonic-response" envelope every /rest/* call replies with,
// over the shapes in SubsonicContracts.cs beside it. Part of the adapter, not
// the catalog: nothing outside /rest constructs one of these.
public static class SubsonicResults
{
    private const string ApiVersion = "1.16.1";

    // Reflection-based (not source-generated) - unlike the mobile/desktop
    // client, Flower.Server isn't trimmed/AOT, so the extra startup cost of
    // reflection is a non-issue and there is no JsonSerializerContext here to
    // keep in step with the shapes next door.
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IResult Ok(
        ArtistsID3? artists = null,
        ArtistWithAlbumsID3? artist = null,
        AlbumWithSongsID3? album = null,
        AlbumList2? albumList2 = null,
        TrackDto? song = null,
        SearchResult3? searchResult3 = null,
        SubsonicPlaylists? playlists = null,
        PlaylistWithSongsDto? playlist = null)
    {
        var envelope = new SubsonicEnvelope
        {
            Response = new SubsonicResponse
            {
                Status = "ok",
                Version = ApiVersion,
                Artists = artists,
                Artist = artist,
                Album = album,
                AlbumList2 = albumList2,
                Song = song,
                SearchResult3 = searchResult3,
                Playlists = playlists,
                Playlist = playlist,
            },
        };
        return Microsoft.AspNetCore.Http.Results.Json(envelope, JsonOptions);
    }

    public static IResult Failed(int code, string message)
    {
        var envelope = new SubsonicEnvelope
        {
            Response = new SubsonicResponse
            {
                Status = "failed",
                Version = ApiVersion,
                Error = new SubsonicError(code, message),
            },
        };
        return Microsoft.AspNetCore.Http.Results.Json(envelope, JsonOptions);
    }
}
