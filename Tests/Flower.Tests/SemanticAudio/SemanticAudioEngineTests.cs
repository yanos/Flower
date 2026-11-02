using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Flower.Models;
using Flower.Persistence.Sql;
using Flower.SemanticAudio;
using Flower.Tests.TestSupport;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests.SemanticAudio;

// The engine from the switch going on to it going off again: the model is
// fetched, the library analysed and searchable, and turning it off leaves no
// row and no file (docs/VIBE-PLAYLIST-PLAN.md, "The settings page").
public class SemanticAudioEngineTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "flower-semantic-engine-" + Guid.NewGuid().ToString("N"));
    private readonly SemanticAudioRepository _repository;
    private readonly SemanticAudioModelStore _store;
    private readonly FakeClipSource _clips = new();
    private readonly FakeClapTextEncoder _text = new();
    private readonly Library _library;
    private readonly SemanticAudioEngine _engine;

    public SemanticAudioEngineTests()
    {
        Directory.CreateDirectory(_directory);
        _repository = new SemanticAudioRepository(new FlowerDb(Path.Combine(_directory, "flower.db")));

        // The tokenizer files have to be real ones; the encoders are fakes, so
        // their files are any bytes.
        var files = new Dictionary<string, byte[]>
        {
            ["audio.onnx"] = [1, 2, 3],
            ["text.onnx"] = [4, 5, 6],
            ["vocab.json"] = SemanticAudioReferences.Read("vocab.subset.json"),
            ["merges.txt"] = SemanticAudioReferences.Read("merges.subset.txt"),
        };
        var manifest = new SemanticAudioModelManifest(new Uri("https://example.test/release/"), "test/int8",
            Describe("audio.onnx", files), Describe("text.onnx", files), Describe("vocab.json", files), Describe("merges.txt", files));
        _store = new SemanticAudioModelStore(Path.Combine(_directory, "semantic-audio", "models"),
            new HttpClient(new Release(files)), NullLogger.Instance, manifest);

        var tracks = new List<Track>();
        for (var i = 0; i < 3; i++)
        {
            var path = Path.Combine(_directory, $"song{i}.flac");
            File.WriteAllBytes(path, [0]);
            _clips.Add(path, TimeSpan.FromSeconds(60));
            tracks.Add(new Track { Title = $"song {i}", Path = path, Duration = TimeSpan.FromSeconds(60) });
        }

        _library = new Library(tracks);
        _text.SetPrompt("anything", FakeClapTextEncoder.Axis(0));
        _engine = new SemanticAudioEngine(_library, _repository, _store, _clips, NullLogger.Instance,
            _ => new FakeClapAudioEncoder(), (_, _) => _text);
    }

    public void Dispose()
    {
        _engine.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A pooled handle outliving the test is not what is under test.
        }
    }

    private static SemanticAudioModelFile Describe(string name, Dictionary<string, byte[]> files) =>
        new(name, files[name].Length, Convert.ToHexStringLower(SHA256.HashData(files[name])));

    private void WaitFor(SemanticAudioState state)
    {
        var clock = Stopwatch.StartNew();
        while (_engine.Status.State != state)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"still {_engine.Status.State}, waiting for {state}: {_engine.Status.Error}");
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void Is_off_and_refuses_to_search_until_switched_on()
    {
        Assert.Equal(SemanticAudioState.Off, _engine.Status.State);
        Assert.Throws<SemanticAudioNotReadyException>(() => _engine.Rank(new SemanticAudioQuery("anything")));
    }

    [Fact]
    public void Switched_on_fetches_the_model_and_makes_the_library_searchable()
    {
        _engine.Configure(enabled: true, ClipGrid.DefaultClipsPerSong);
        WaitFor(SemanticAudioState.UpToDate);

        Assert.True(_store.IsPresent);
        Assert.Equal(3, _engine.Status.SongsSearchable);
        Assert.Equal(3, _engine.Status.Analysis.Done);
        Assert.Equal(3, _engine.Rank(new SemanticAudioQuery("anything")).Count);
    }

    [Fact]
    public void Switched_off_deletes_the_model_and_every_analysis()
    {
        _engine.Configure(enabled: true, ClipGrid.DefaultClipsPerSong);
        WaitFor(SemanticAudioState.UpToDate);

        _engine.Configure(enabled: false, ClipGrid.DefaultClipsPerSong);
        WaitFor(SemanticAudioState.Off);

        Assert.Empty(_repository.LoadSongs());
        Assert.Equal(0, _repository.ClipCount());
        Assert.False(Directory.Exists(_store.Directory));
        Assert.Throws<SemanticAudioNotReadyException>(() => _engine.Rank(new SemanticAudioQuery("anything")));
    }

    [Fact]
    public void Paused_for_a_library_scan_fetches_the_model_but_listens_to_nothing_until_resumed()
    {
        _engine.Pause();
        _engine.Configure(enabled: true, ClipGrid.DefaultClipsPerSong);
        WaitFor(SemanticAudioState.Paused);

        Assert.True(_store.IsPresent);
        Assert.Empty(_clips.Opens);

        _engine.Resume();
        WaitFor(SemanticAudioState.UpToDate);

        Assert.Equal(3, _engine.Status.SongsSearchable);
    }

    [Fact]
    public void A_host_with_no_decoder_says_so_once_instead_of_failing_every_song()
    {
        _clips.IsAvailable = false;

        _engine.Configure(enabled: true, ClipGrid.DefaultClipsPerSong);
        WaitFor(SemanticAudioState.Failed);

        Assert.Contains("decoder", _engine.Status.Error);
        Assert.Empty(_engine.Failures());
        Assert.Empty(_clips.Opens);
    }

    private sealed class Release(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(files.TryGetValue(request.RequestUri!.Segments[^1], out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
