using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Flower.Persistence;

namespace Flower.Services;

// One paired device's rolling log history, keyed by the fingerprint the
// signature check validated rather than whatever fingerprint the request body
// claims. ReceivedAt is the time of its newest snapshot, while Entries spans
// every deduplicated line retained from that device over the last week.
public sealed record ClientLogSnapshot(string Fingerprint, string Alias, DateTimeOffset ReceivedAt, IReadOnlyList<LogEntryDto> Entries);

// Durable server-side history of logs pushed by paired clients. On disk it is
// deliberately inspectable without Flower:
//
//   logs/devices/Mr Telephone--5306d3.../2026-08-28T00-00-00Z.logs.jsonl
//
// One JSON object per line makes an active day's file append-only. Clients send
// overlapping in-memory snapshots, so stable event hashes prevent duplicates;
// reads compact malformed/expired lines and whole device folders disappear
// after seven quiet days.
public sealed class ClientLogStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private const string MetadataFileName = "device.json";
    private const string LogFilePattern = "*.logs.jsonl";

    private readonly object _lock = new();
    private readonly string _rootDirectory;

    public ClientLogStore(string rootDirectory)
    {
        _rootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(_rootDirectory);
        lock (_lock)
            PruneAll(DateTimeOffset.UtcNow);
    }

    // string argument is the fingerprint whose history just changed.
    public event EventHandler<string>? SnapshotUpdated;

    public void SetSnapshot(string fingerprint, string alias, IReadOnlyList<LogEntryDto> entries, DateTimeOffset receivedAt)
    {
        lock (_lock)
        {
            var directory = FindDeviceDirectory(fingerprint) ?? CreateDeviceDirectory(alias, fingerprint);
            var history = LoadEntries(directory, receivedAt);
            var known = history.Select(EventId).ToHashSet(StringComparer.Ordinal);
            var cutoff = receivedAt.Subtract(Retention);

            foreach (var group in entries
                         .Where(entry => entry.Timestamp >= cutoff && known.Add(EventId(entry)))
                         .GroupBy(entry => LogFileName(entry.Timestamp)))
            {
                AppendEntries(Path.Combine(directory, group.Key), group);
            }

            AtomicJsonFile.Write(
                Path.Combine(directory, MetadataFileName),
                new ClientLogMetadata(fingerprint, alias, receivedAt),
                ClientLogFileJsonContext.Default.ClientLogMetadata);
        }

        SnapshotUpdated?.Invoke(this, fingerprint);
    }

    public ClientLogSnapshot? Get(string fingerprint)
    {
        lock (_lock)
        {
            var directory = FindDeviceDirectory(fingerprint);
            return directory == null ? null : LoadSnapshot(directory, DateTimeOffset.UtcNow);
        }
    }

    public IReadOnlyList<ClientLogSnapshot> All()
    {
        lock (_lock)
        {
            if (!Directory.Exists(_rootDirectory))
                return [];

            return Directory.EnumerateDirectories(_rootDirectory)
                .ToList()
                .Select(directory => LoadSnapshot(directory, DateTimeOffset.UtcNow))
                .Where(snapshot => snapshot != null)
                .Select(snapshot => snapshot!)
                .OrderBy(snapshot => snapshot.Alias, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private ClientLogSnapshot? LoadSnapshot(string directory, DateTimeOffset now)
    {
        var metadata = AtomicJsonFile.Read(
            Path.Combine(directory, MetadataFileName),
            ClientLogFileJsonContext.Default.ClientLogMetadata);
        if (metadata == null)
            return null;

        var entries = LoadEntries(directory, now);
        if (metadata.ReceivedAt < now.Subtract(Retention) && entries.Count == 0)
        {
            TryDeleteDirectory(directory);
            return null;
        }

        return new ClientLogSnapshot(metadata.Fingerprint, metadata.Alias, metadata.ReceivedAt, entries);
    }

    private List<LogEntryDto> LoadEntries(string directory, DateTimeOffset now)
    {
        var cutoff = now.Subtract(Retention);
        var entries = new Dictionary<string, LogEntryDto>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, LogFilePattern).ToList())
        {
            var retained = new List<LogEntryDto>();
            var needsRewrite = false;
            foreach (var line in File.ReadLines(path))
            {
                LogEntryDto? entry;
                try
                {
                    entry = JsonSerializer.Deserialize(line, ClientLogFileJsonContext.Default.LogEntryDto);
                }
                catch (JsonException)
                {
                    entry = null;
                }

                if (entry == null || entry.Timestamp < cutoff)
                {
                    needsRewrite = true;
                    continue;
                }

                if (!entries.TryAdd(EventId(entry), entry))
                {
                    needsRewrite = true;
                    continue;
                }
                retained.Add(entry);
            }

            if (retained.Count == 0)
                TryDeleteFile(path);
            else if (needsRewrite)
                RewriteEntries(path, retained);
        }

        return entries.Values
            .OrderBy(entry => entry.Timestamp)
            .ThenBy(EventId, StringComparer.Ordinal)
            .ToList();
    }

    private void PruneAll(DateTimeOffset now)
    {
        if (!Directory.Exists(_rootDirectory))
            return;

        foreach (var directory in Directory.EnumerateDirectories(_rootDirectory).ToList())
            _ = LoadSnapshot(directory, now);
    }

    private string? FindDeviceDirectory(string fingerprint)
    {
        if (!Directory.Exists(_rootDirectory))
            return null;

        var suffix = $"--{SafeSegment(fingerprint, 96)}";
        return Directory.EnumerateDirectories(_rootDirectory)
            .FirstOrDefault(path => Path.GetFileName(path).EndsWith(suffix, StringComparison.Ordinal));
    }

    private string CreateDeviceDirectory(string alias, string fingerprint)
    {
        var path = Path.Combine(
            _rootDirectory,
            $"{SafeSegment(alias, 64)}--{SafeSegment(fingerprint, 96)}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string SafeSegment(string value, int maxLength)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value
            .Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.');
        if (sanitized.Length == 0)
            sanitized = "device";
        return sanitized.Length <= maxLength ? sanitized : sanitized[..maxLength];
    }

    private static string LogFileName(DateTimeOffset timestamp) =>
        $"{timestamp.UtcDateTime:yyyy-MM-dd'T'00-00-00'Z'}.logs.jsonl";

    private static void AppendEntries(string path, IEnumerable<LogEntryDto> entries)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        foreach (var entry in entries)
            writer.WriteLine(JsonSerializer.Serialize(entry, ClientLogFileJsonContext.Default.LogEntryDto));
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static void RewriteEntries(string path, IReadOnlyList<LogEntryDto> entries)
    {
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            foreach (var entry in entries)
                writer.WriteLine(JsonSerializer.Serialize(entry, ClientLogFileJsonContext.Default.LogEntryDto));
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort. The next read tries the same expired file again.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort. The next read/prune tries the quiet device again.
        }
    }

    private static string EventId(LogEntryDto entry)
    {
        var identity = new StringBuilder()
            .Append(entry.Timestamp.UtcTicks.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(Field(entry.Level)).Append('|')
            .Append(Field(entry.SourceContext)).Append('|')
            .Append(Field(entry.Message)).Append('|')
            .Append(Field(entry.Exception))
            .ToString();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    // Length-prefixing keeps null, empty and delimiter-containing strings
    // distinct before hashing.
    private static string Field(string? value) => value == null ? "-1:" : $"{value.Length}:{value}";
}
