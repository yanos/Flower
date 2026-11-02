using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Flower.SemanticAudio;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests.SemanticAudio;

// Downloading the model (docs/VIBE-PLAYLIST-PLAN.md, "Downloading"), against
// a release faked at the HttpMessageHandler with two small files.
public class SemanticAudioModelStoreTests : IDisposable
{
    private static readonly byte[] First = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("first file ", 500)));
    private static readonly byte[] Second = Encoding.ASCII.GetBytes("second");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "flower-model-store-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRelease _release = new();

    public SemanticAudioModelStoreTests()
    {
        _release.Files["audio.onnx"] = First;
        _release.Files["text.onnx"] = Second;
        _release.Files["vocab.json"] = Second;
        _release.Files["merges.txt"] = Second;
        SemanticAudioModelStore.ReconnectDelay = TimeSpan.Zero;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static SemanticAudioModelFile Describe(string name, byte[] bytes) =>
        new(name, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));

    private static readonly SemanticAudioModelManifest Manifest = new(
        new Uri("https://example.test/release/"),
        "test/int8",
        Describe("audio.onnx", First),
        Describe("text.onnx", Second),
        Describe("vocab.json", Second),
        Describe("merges.txt", Second));

    private SemanticAudioModelStore Store() =>
        new(_directory, new HttpClient(_release), NullLogger.Instance, Manifest);

    [Fact]
    public async Task Downloads_every_file_and_counts_them_present()
    {
        var store = Store();
        var reports = new List<ModelDownloadProgress>();

        await store.DownloadAsync(new Progress(reports), CancellationToken.None);

        Assert.True(store.IsPresent);
        Assert.Equal(First, File.ReadAllBytes(Path.Combine(_directory, "audio.onnx")));
        Assert.Equal(new ModelDownloadProgress(Manifest.TotalBytes, Manifest.TotalBytes), reports[^1]);
    }

    [Fact]
    public async Task Resumes_a_file_it_was_cut_off_in()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Path.Combine(_directory, "audio.onnx.part"), First[..1000]);

        await Store().DownloadAsync(null, CancellationToken.None);

        Assert.Equal("bytes=1000-", _release.Ranges["audio.onnx"]);
        Assert.Equal(First, File.ReadAllBytes(Path.Combine(_directory, "audio.onnx")));
    }

    [Fact]
    public async Task Picks_up_where_a_dropped_connection_left_off()
    {
        _release.CutOff["audio.onnx"] = 2000;

        await Store().DownloadAsync(null, CancellationToken.None);

        Assert.Equal("bytes=2000-", _release.Ranges["audio.onnx"]);
        Assert.Equal(First, File.ReadAllBytes(Path.Combine(_directory, "audio.onnx")));
    }

    [Fact]
    public async Task Starts_again_when_the_server_ignores_the_range()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Path.Combine(_directory, "audio.onnx.part"), First[..1000]);
        _release.IgnoresRanges = true;

        await Store().DownloadAsync(null, CancellationToken.None);

        Assert.Equal(First, File.ReadAllBytes(Path.Combine(_directory, "audio.onnx")));
    }

    [Fact]
    public async Task Gives_up_on_a_file_that_fails_its_hash_twice_and_keeps_none_of_it()
    {
        _release.Files["text.onnx"] = Encoding.ASCII.GetBytes("SECOND");
        var store = Store();

        await Assert.ThrowsAsync<ModelDownloadException>(() => store.DownloadAsync(null, CancellationToken.None));

        Assert.False(store.IsPresent);
        Assert.Equal(2, _release.Requests.Count(r => r == "text.onnx"));
        Assert.False(File.Exists(Path.Combine(_directory, "text.onnx")));
        Assert.False(File.Exists(Path.Combine(_directory, "text.onnx.part")));
    }

    [Fact]
    public async Task Fetches_only_what_is_missing()
    {
        var store = Store();
        await store.DownloadAsync(null, CancellationToken.None);
        File.Delete(Path.Combine(_directory, "vocab.json"));
        _release.Requests.Clear();

        await store.DownloadAsync(null, CancellationToken.None);

        Assert.Equal(["vocab.json"], _release.Requests);
    }

    [Fact]
    public async Task Delete_leaves_nothing_behind()
    {
        var store = Store();
        await store.DownloadAsync(null, CancellationToken.None);

        store.Delete();

        Assert.False(Directory.Exists(_directory));
        Assert.Equal(0, store.BytesOnDisk);
    }

    private sealed class Progress(List<ModelDownloadProgress> reports) : IProgress<ModelDownloadProgress>
    {
        public void Report(ModelDownloadProgress value) => reports.Add(value);
    }

    private sealed class FakeRelease : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new();
        public Dictionary<string, string> Ranges { get; } = new();
        public List<string> Requests { get; } = new();
        public bool IgnoresRanges { get; set; }

        // The next whole-file GET of a name drops its connection after this
        // many bytes.
        public Dictionary<string, int> CutOff { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var name = request.RequestUri!.Segments[^1];
            Requests.Add(name);
            if (!Files.TryGetValue(name, out var bytes))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            if (request.Headers.Range is { } range && !IgnoresRanges)
            {
                Ranges[name] = range.ToString();
                var from = (int)range.Ranges.Single().From!.Value;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(bytes[from..]),
                });
            }

            if (CutOff.Remove(name, out var at))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Dropping(bytes, at)) });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    // Reads like the file up to a point, then like a connection reset.
    private sealed class Dropping(byte[] bytes, int at) : MemoryStream(bytes[..at])
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            return read > 0 ? read : throw new IOException("Connection reset by peer");
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = base.Read(buffer.Span);
            return read > 0 ? ValueTask.FromResult(read) : throw new IOException("Connection reset by peer");
        }
    }
}
