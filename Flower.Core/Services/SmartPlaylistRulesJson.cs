using System;
using System.Text.Json;
using System.Text.Json.Serialization;

using Flower.Models;

namespace Flower.Services;

// How a smart playlist's rules become the playlists.rules column (Schema V6)
// and, later, a field on the sync wire.
//
// One blob, not a normalized smart_conditions table: the rules are only ever
// read and written whole, nothing queries across them, and they have to travel
// as a unit anyway - see docs/SMART-PLAYLIST-PLAN.md, "Persistence".
//
// Reading is deliberately forgiving. A rules blob can arrive from a peer, a
// newer version of Flower, or a hand-edited database, and none of those are
// worth failing the whole playlist load over: an unreadable blob degrades the
// playlist to an ordinary one holding whatever tracks were last materialized
// into playlist_tracks, which is the same tolerance PlaylistRepository already
// applies to a track id that no longer resolves.
public static class SmartPlaylistRulesJson
{
    public static string Write(SmartPlaylistRules rules) =>
        JsonSerializer.Serialize(rules, SmartPlaylistRulesJsonContext.Default.SmartPlaylistRules);

    public static SmartPlaylistRules? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize(json, SmartPlaylistRulesJsonContext.Default.SmartPlaylistRules);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

// Trim/AOT-safe source-generated metadata, for the same reason every other
// context in this folder is source-generated: this type has to serialize on
// iOS, where there is no JIT to build a reflection-based contract at runtime.
//
// Enums stay numbers - SmartField and SmartOperator say so in their own
// remarks, and the numbers are what a stored rule is written in terms of.
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SmartPlaylistRules))]
public partial class SmartPlaylistRulesJsonContext : JsonSerializerContext
{
}
