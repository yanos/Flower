using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Flower.SemanticAudio;

public readonly record struct ModelDownloadProgress(long BytesDone, long BytesTotal);

// Transient: the connection failed rather than the release, and trying again
// from where it stopped may well work.
public sealed class ModelDownloadException(string message, Exception? inner = null, bool transient = false) : Exception(message, inner)
{
    public bool Transient { get; } = transient;
}

// The model files on this host, in <data dir>/semantic-audio/models/
// (docs/VIBE-PLAYLIST-PLAN.md, "Downloading"). A file counts as present only
// once its SHA-256 has matched the manifest's; until then it is a ".part"
// that a download resumes with a ranged GET, so a 250 MB file cut off at 90%
// costs the last 10% again rather than all of it.
public sealed class SemanticAudioModelStore(string directory, HttpClient http, ILogger logger, SemanticAudioModelManifest? manifest = null)
{
    // A file whose bytes fail the hash is thrown away and fetched again once;
    // a second failure is the release and this build disagreeing, which no
    // retry fixes.
    private const int Attempts = 2;

    // A body that sends nothing for this long is given up on, and the next
    // attempt resumes where it stopped. Without it a connection that stalls
    // without closing would hold the switch at "Downloading: 34%" forever.
    internal static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    // A connection lost is retried this many times, resuming each time,
    // before the switch shows the error and waits for Retry.
    private const int ConnectionAttempts = 5;
    internal static TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);

    public SemanticAudioModelManifest Manifest { get; } = manifest ?? SemanticAudioModelManifest.Current;

    public string Directory => directory;

    public string PathOf(SemanticAudioModelFile file) => Path.Combine(directory, file.Name);

    // Present means every file is there at its manifest size. The hash was
    // checked when the file was written, so a start doesn't re-read 330 MB.
    public bool IsPresent
    {
        get
        {
            foreach (var file in Manifest.Files)
            {
                var info = new FileInfo(PathOf(file));
                if (!info.Exists || info.Length != file.Size)
                    return false;
            }

            return true;
        }
    }

    public long BytesOnDisk
    {
        get
        {
            if (!System.IO.Directory.Exists(directory))
                return 0;

            long total = 0;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
                total += file.Length;
            return total;
        }
    }

    public async Task DownloadAsync(IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(directory);
        DeleteWhatTheManifestDoesNotName();
        long done = 0;
        var total = Manifest.TotalBytes;

        foreach (var file in Manifest.Files)
        {
            var path = PathOf(file);
            var present = new FileInfo(path);
            if (present.Exists && present.Length == file.Size)
            {
                done += file.Size;
                progress?.Report(new ModelDownloadProgress(done, total));
                continue;
            }

            var before = done;
            for (var attempt = 1; ; attempt++)
            {
                await FetchResumingAsync(file, bytes => progress?.Report(new ModelDownloadProgress(before + bytes, total)), cancellationToken);
                var part = path + ".part";
                if (await HashAsync(part, cancellationToken) == file.Sha256)
                {
                    File.Move(part, path, overwrite: true);
                    break;
                }

                File.Delete(part);
                logger.LogWarning("{File} did not match its SHA-256 (attempt {Attempt} of {Attempts})", file.Name, attempt, Attempts);
                if (attempt == Attempts)
                    throw new ModelDownloadException($"{file.Name} downloaded twice and matched its SHA-256 neither time.");
            }

            done += file.Size;
            progress?.Report(new ModelDownloadProgress(done, total));
        }
    }

    // A build that downloads a new model leaves the old one's files behind
    // otherwise - the first export's audio encoder is 80 MB that nothing
    // would ever read again. Only a manifest file, or one being downloaded,
    // stays.
    private void DeleteWhatTheManifestDoesNotName()
    {
        var wanted = Manifest.Files.SelectMany(f => new[] { f.Name, f.Name + ".part" }).ToHashSet(StringComparer.Ordinal);
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles())
        {
            if (wanted.Contains(file.Name))
                continue;

            logger.LogInformation("Deleting {File}, which this model does not use", file.Name);
            file.Delete();
        }
    }

    // Deletes the model, for the master switch going off.
    public void Delete()
    {
        if (System.IO.Directory.Exists(directory))
            System.IO.Directory.Delete(directory, recursive: true);
    }

    private async Task FetchResumingAsync(SemanticAudioModelFile file, Action<long> reportBytes, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await FetchAsync(file, reportBytes, cancellationToken);
                return;
            }
            catch (ModelDownloadException ex) when (ex.Transient && attempt < ConnectionAttempts)
            {
                logger.LogInformation("{Message} Trying again in {Delay}.", ex.Message, ReconnectDelay);
                await Task.Delay(ReconnectDelay, cancellationToken);
            }
        }
    }

    private async Task FetchAsync(SemanticAudioModelFile file, Action<long> reportBytes, CancellationToken cancellationToken)
    {
        var part = PathOf(file) + ".part";
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > file.Size)
        {
            File.Delete(part);
            have = 0;
        }

        if (have == file.Size)
            return;

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Manifest.BaseUrl, file.Name));
        if (have > 0)
            request.Headers.Range = new RangeHeaderValue(have, null);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelDownloadException($"Couldn't reach {request.RequestUri}: {ex.Message}", ex, transient: true);
        }

        using (response)
        {
            // A server that ignores the range sends the whole file again.
            if (have > 0 && response.StatusCode == HttpStatusCode.OK)
                have = 0;
            else if (!response.IsSuccessStatusCode)
                throw new ModelDownloadException($"{request.RequestUri} answered {(int)response.StatusCode} {response.ReasonPhrase}.");

            await using var output = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[1 << 16];
                reportBytes(have);
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    var read = await body.ReadAsync(buffer, stall.Token);
                    if (read == 0)
                        break;

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    have += read;
                    if (have > file.Size)
                        throw new ModelDownloadException($"{file.Name} is larger than the {file.Size} bytes this build expects.");
                    reportBytes(have);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ModelDownloadException($"{file.Name} stopped arriving at {have} of {file.Size} bytes.", transient: true);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                throw new ModelDownloadException($"{file.Name} was cut off at {have} of {file.Size} bytes: {ex.Message}", ex, transient: true);
            }
        }

        if (have != file.Size)
            throw new ModelDownloadException($"{file.Name} stopped at {have} of {file.Size} bytes.", transient: true);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }
}
