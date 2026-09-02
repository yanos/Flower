using System.Text.Json.Serialization;

namespace Flower.Services;

// Trim/AOT-safe source-generated metadata for POST /api/flower/v1/plays - see
// PlayReportContracts. Here rather than in the app's own FlowerJsonContext for
// the same reason PlaylistSyncJsonContext is: OriginPlayReporter lives in this
// project so the browser head can use it, and FlowerJsonContext is internal to
// Flower.
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PlayReportDto))]
[JsonSerializable(typeof(TrackStateReportDto))]
public partial class PlayReportJsonContext : JsonSerializerContext
{
}
