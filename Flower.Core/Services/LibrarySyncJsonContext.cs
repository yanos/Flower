using System.Text.Json.Serialization;

namespace Flower.Services;

// Trim/AOT-safe source-generated metadata for reading a library manifest off the
// wire (GET /api/flower/v1/library - see LibrarySyncContracts). Here rather than
// in the app's FlowerJsonContext because RemoteLibraryImporter, which does the
// reading, is in this project so the browser head can use it too - and
// FlowerJsonContext is internal to Flower and covers types that cannot be
// referenced from here.
//
// The options are the wire format, not a preference: the serializing side
// (SyncHttpServer via FlowerJsonContext, Flower.Server via SyncEndpoints'
// JsonOptions) writes PascalCase with nulls omitted. Only the case-insensitive
// flag actually matters for a reader, but stating all three keeps this next to
// the shape it has to match rather than a step removed from it.
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(LibrarySyncManifestDto))]
public partial class LibrarySyncJsonContext : JsonSerializerContext
{
}
