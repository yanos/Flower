using System.Text.Json;
using System.Text.Json.Nodes;

namespace Flower.Server.Configuration;

// Writes the operator-editable half of the configuration back to
// flower-server.json in the data directory - the file Program.cs layers over
// appsettings.json with reloadOnChange: true, so a write here is picked up by
// IOptionsMonitor without restarting anything.
//
// Read-modify-write over a JsonNode rather than serializing a fresh
// FlowerServerOptions: the seeded file (see ServerDataDirectory.SeedSettingsFile)
// is mostly underscore-prefixed documentation keys, and an operator may well have
// added their own. Re-emitting the whole object from the options type would throw
// all of that away the first time anyone touched a checkbox in the browser.
//
// DataDirectory is never written: it is the setting that located this file, and
// Program.cs deliberately pushes a resolved absolute path over the top of it.
public static class ServerSettingsWriter
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public static async Task WriteAsync(string dataDirectory, IReadOnlyDictionary<string, JsonNode?> values, CancellationToken ct = default)
    {
        var path = Path.Combine(dataDirectory, ServerDataDirectory.SettingsFileName);

        await WriteLock.WaitAsync(ct);
        try
        {
            JsonObject root;
            if (File.Exists(path))
            {
                // A file we cannot parse is a file we must not silently replace -
                // it is the operator's, and it may be one typo away from correct.
                var existing = JsonNode.Parse(
                    await File.ReadAllTextAsync(path, ct),
                    documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                root = existing as JsonObject ?? throw new InvalidDataException(
                    $"{ServerDataDirectory.SettingsFileName} is not a JSON object.");
            }
            else
            {
                root = new JsonObject();
            }

            if (root[FlowerServerOptions.SectionName] is not JsonObject section)
            {
                section = new JsonObject();
                root[FlowerServerOptions.SectionName] = section;
            }

            foreach (var (key, value) in values)
            {
                if (string.Equals(key, nameof(FlowerServerOptions.DataDirectory), StringComparison.Ordinal))
                    continue;
                section[key] = value;
            }

            // Written beside the target and moved into place: a half-written
            // settings file is one the next boot cannot parse, and this is the
            // file that carries the library paths.
            var temporary = path + ".tmp";
            await File.WriteAllTextAsync(
                temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            WriteLock.Release();
        }
    }
}
