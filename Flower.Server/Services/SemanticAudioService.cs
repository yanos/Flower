using Flower.Models;
using Flower.Persistence.Sql;
using Flower.SemanticAudio;
using Flower.Server.Configuration;
using Flower.Services;

using Microsoft.Extensions.Options;

namespace Flower.Server.Services;

// Where the model is downloaded from: the release SemanticAudioModelManifest
// names, over a client of its own. A type so a test can hand it a handler
// that refuses, as PublicAddressProbe is handed one - a test that turns Vibe
// Playlists on must not fetch 330 MB from GitHub.
public sealed class SemanticAudioModelSource(HttpMessageHandler? handler = null)
{
    public HttpClient CreateClient()
    {
        if (handler is null)
            return OnlineLookup.CreateClient();

        var http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.TryParseAdd(OnlineLookup.UserAgent);
        return http;
    }
}

// Vibe Playlists on the server (docs/VIBE-PLAYLIST-PLAN.md, "Where it runs"):
// Flower.Core's engine, handed ONNX Runtime's encoders and FFAudio's decoder,
// kept in step with the owner's two settings and paused while a library scan
// runs. One for the process, for every device paired here.
//
// A hosted service so it starts after Program has started the startup scan:
// the first pass then waits for that scan rather than racing it, while the
// model downloads and the index loads meanwhile.
public sealed class SemanticAudioService : IHostedService, IDisposable
{
    // ONNX Runtime's threads for each session. One: the work happens on the
    // engine's own thread, below normal priority, and a server's cores are
    // there to serve streams first. The first pass over a large library is
    // an overnight job either way (the plan's "Clips").
    private const int EncoderThreads = 1;

    private readonly IOptionsMonitor<FlowerServerOptions> _options;
    private readonly LibraryRescanCoordinator _rescans;
    private IDisposable? _optionsChanged;

    public SemanticAudioService(
        Library library,
        FlowerDb db,
        IOptionsMonitor<FlowerServerOptions> options,
        LibraryRescanCoordinator rescans,
        SemanticAudioModelSource source,
        ILoggerFactory loggers)
    {
        _options = options;
        _rescans = rescans;

        var logger = loggers.CreateLogger<SemanticAudioEngine>();
        var http = source.CreateClient();
        Engine = new SemanticAudioEngine(
            library,
            new SemanticAudioRepository(db),
            new SemanticAudioModelStore(Path.Combine(options.CurrentValue.DataDirectory, "semantic-audio", "models"), http, logger),
            new FfmpegClipSource(),
            logger,
            path => new OnnxClapAudioEncoder(path, EncoderThreads),
            (path, tokenizer) => new OnnxClapTextEncoder(path, tokenizer, EncoderThreads));
        Report = new SemanticAudioReport(Engine, library);
        Pools = new VibePlaylistPools(Engine, library, loggers.CreateLogger<VibePlaylistPools>());
    }

    public SemanticAudioEngine Engine { get; }

    public SemanticAudioReport Report { get; }

    // Every Vibe Playlist's songs, ranked here for every device paired.
    public VibePlaylistPools Pools { get; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _rescans.Started += Engine.WaitForScan;
        _rescans.Completed += Engine.ScanDone;

        // The startup scan is usually running by now. Checked twice around
        // the wait, so a scan ending in between still lets it go on.
        if (_rescans.IsRunning)
            Engine.WaitForScan();
        if (!_rescans.IsRunning)
            Engine.ScanDone();

        Apply(_options.CurrentValue);
        _optionsChanged = _options.OnChange(Apply);
        Pools.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _rescans.Started -= Engine.WaitForScan;
        _rescans.Completed -= Engine.ScanDone;
        _optionsChanged?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Pools.Dispose();
        Engine.Dispose();
    }

    // The settings page's PUT, and its Pause and Resume, apply what they
    // saved at once, not when the configuration watcher gets round to it;
    // the engine ignores a repeat. The songs are listened to while either
    // feature needs them, and Vibe Playlists and Endless Play each refuse on
    // their own switch (VibeEndpoints).
    public void Apply(FlowerServerOptions options)
    {
        Engine.SetPaused(options.SemanticAudioPaused);
        Engine.Configure(
            options.RunsSemanticAudio,
            ClipGrid.IsValidSetting(options.VibeClipsPerSong) ? options.VibeClipsPerSong : ClipGrid.DefaultClipsPerSong);
        RunsVibePlaylists = options.RunsVibePlaylists;
        RunsEndlessPlay = options.RunsEndlessPlay;
    }

    // Each feature's switches as last applied, which is what the routes ask:
    // the engine runs for either.
    public bool RunsVibePlaylists { get; private set; }

    public bool RunsEndlessPlay { get; private set; }
}
