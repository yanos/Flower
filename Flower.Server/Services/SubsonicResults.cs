using System.Text.Json;
using System.Text.Json.Serialization;

using Flower.Services;

namespace Flower.Server.Services;

// Builds the "subsonic-response" envelope every /rest/* call replies with,
// using Flower.Core's own OpenSubsonicContracts types - the same shapes
// OpenSubsonicClient parses on the way in, reused directly on the way out
// rather than a server-side duplicate of the same wire fields.
public static class SubsonicResults
{
    private const string ApiVersion = "1.16.1";

    // Reflection-based (not source-generated) - unlike the mobile/desktop
    // client, Flower.Server isn't trimmed/AOT, so the extra startup cost of
    // reflection is a non-issue and this avoids a third JsonSerializerContext
    // duplicating the same SubsonicEnvelope shape (see OpenSubsonicJsonContext's
    // and ExternalProtocolJsonContext's own doc comments on why they're already
    // two, not one).
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
        Child? song = null,
        SearchResult3? searchResult3 = null,
        Flower.Services.Playlists? playlists = null,
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
