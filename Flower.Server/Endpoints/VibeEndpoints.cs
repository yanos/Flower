using System.Text.Json;

using Flower.Models;
using Flower.SemanticAudio;
using Flower.Server.Services;
using Flower.Services;

namespace Flower.Server.Endpoints;

// Vibe Playlists for a paired device (docs/VIBE-PLAYLIST-PLAN.md, "The wire"):
// a preview while the editor is typed in, and more songs when an endless
// queue has played most of its pool - and Endless Play's songs for a queue
// that has run out. Mapped in SyncEndpoints' group, so every
// request here is signed and charged to its device's Vibe budget - the one
// thing a listener can spend is the server's CPU, a text encode per prompt it
// hasn't seen, and every paired device may do it, admin or not.
//
// The prompt is never logged above Debug: what someone wants to listen to is
// their own business, even on a shared server.
public static class VibeEndpoints
{
    // Longer than any sentence anyone types, and well short of what the
    // tokenizer truncates at anyway (77 tokens).
    public const int MaxPromptLength = 500;

    // Far more than a session adds: one extension is ExtendSize songs.
    private const int MaxExcluded = 5000;

    // "The last few songs": a device sends five (PlaylistControlViewModel's
    // EndlessSeeds), and more would only blur them.
    private const int MaxSeeds = 20;

    public static IResult Preview(HttpContext context, SemanticAudioService vibe, JsonSerializerOptions json)
    {
        VibePreviewRequestDto? request;
        try
        {
            request = JsonSerializer.Deserialize<VibePreviewRequestDto>(RequestGate.BodyOf(context), json);
        }
        catch (JsonException)
        {
            return Problems.BadRequest();
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > MaxPromptLength)
            return Problems.BadRequest("A preview needs a sentence of at most 500 characters.");

        if (Refusal(vibe) is { } refusal)
            return refusal;

        try
        {
            var songs = vibe.Pools.Preview(request.Prompt, request.Instrumental);
            return Results.Json(new VibeSongsDto(songs.Select(PlaylistSyncMapper.ToDto).ToList()), json);
        }
        catch (SemanticAudioNotReadyException)
        {
            return NotReady();
        }
    }

    // Only the caller's own playlists: a listener's playlists are kept apart
    // from everyone else's (Listeners), and one that names another's is told
    // there is no such playlist rather than that there is one it can't have.
    public static IResult Extend(
        HttpContext context, string id, Library library, SemanticAudioService vibe, string? listener, JsonSerializerOptions json)
    {
        if (listener is null)
            return Problems.Of(StatusCodes.Status401Unauthorized, ProblemCodes.SignatureInvalid);
        if (!Guid.TryParse(id, out var playlistId))
            return Problems.NotFound();

        VibeExtendRequestDto? request;
        try
        {
            request = JsonSerializer.Deserialize<VibeExtendRequestDto>(RequestGate.BodyOf(context), json);
        }
        catch (JsonException)
        {
            return Problems.BadRequest();
        }

        if (request is null || request.Exclude is { Count: > MaxExcluded })
            return Problems.BadRequest();

        var playlist = library.Playlists.FirstOrDefault(p => p.Id == playlistId);
        if (playlist is not { IsVibe: true } || playlist.Listener != listener)
            return Problems.NotFound();

        if (Refusal(vibe) is { } refusal)
            return refusal;

        var exclude = new HashSet<Guid>();
        foreach (var key in request.Exclude ?? [])
        {
            if (EntityId.FromWire(key) is { } trackId)
                exclude.Add(trackId);
        }

        try
        {
            var songs = vibe.Pools.Extend(playlist, exclude);
            return Results.Json(new VibeSongsDto(songs.Select(PlaylistSyncMapper.ToDto).ToList()), json);
        }
        catch (SemanticAudioNotReadyException)
        {
            return NotReady();
        }
    }

    // Not a listener's playlist, so anyone paired may ask; the songs are the
    // library's, which every paired device already has.
    public static IResult Endless(HttpContext context, Library library, SemanticAudioService vibe, JsonSerializerOptions json)
    {
        EndlessPlayRequestDto? request;
        try
        {
            request = JsonSerializer.Deserialize<EndlessPlayRequestDto>(RequestGate.BodyOf(context), json);
        }
        catch (JsonException)
        {
            return Problems.BadRequest();
        }

        if (request?.Seeds is not { Count: > 0 and <= MaxSeeds } || request.Exclude is { Count: > MaxExcluded })
            return Problems.BadRequest($"Endless Play needs 1 to {MaxSeeds} songs to go on from.");

        if (!vibe.RunsEndlessPlay || !vibe.Engine.Enabled)
            return Problems.Of(StatusCodes.Status409Conflict, ProblemCodes.EndlessPlayOff, "Endless Play is off on this server.");

        var seeds = request.Seeds.Select(EntityId.FromWire).OfType<Guid>().ToList();
        var exclude = (request.Exclude ?? []).Select(EntityId.FromWire).OfType<Guid>().ToHashSet();
        try
        {
            var byId = library.Snapshot.ById;
            var songs = vibe.Engine.Similar(seeds, exclude)
                .Select(match => byId.GetValueOrDefault(match.TrackId))
                .OfType<Track>()
                .Select(PlaylistSyncMapper.ToDto)
                .ToList();
            return Results.Json(new VibeSongsDto(songs), json);
        }
        catch (SemanticAudioNotReadyException)
        {
            return NotReady();
        }
    }

    private static IResult? Refusal(SemanticAudioService vibe) =>
        vibe.RunsVibePlaylists && vibe.Engine.Enabled
            ? null
            : Problems.Of(StatusCodes.Status409Conflict, ProblemCodes.VibeOff, "Vibe Playlists are off on this server.");

    private static IResult NotReady() =>
        Problems.Of(StatusCodes.Status409Conflict, ProblemCodes.VibeNotReady, "No song has been analysed yet.");
}
