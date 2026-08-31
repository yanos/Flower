using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Serilog;
using Serilog.Events;

using Flower.Persistence;

// This class hands out Microsoft.Extensions.Logging's ILogger everywhere, never
// Serilog's own ILogger - alias it explicitly since both are in scope here.
using ILogger = Microsoft.Extensions.Logging.ILogger;
using ILoggerFactory = Microsoft.Extensions.Logging.ILoggerFactory;

namespace Flower.Logging
{
    // Bootstraps file logging once at startup (see App.axaml.cs). One log file
    // per launch, not per day - a single run's activity should never be split
    // across files, since correlating "everything that happened in the session
    // where the bug occurred" is exactly what you need when reading these back.
    // Application code logs through Microsoft.Extensions.Logging's ILogger (via
    // CreateLogger<T> below), never Serilog's own types directly - Serilog is
    // purely the file-writing engine underneath.
    public static class AppLogging
    {
        // Keeps the newest 10 runs' worth of logs. Deleted eagerly on the next
        // startup rather than e.g. a timer, since the app isn't always running.
        private const int MaxRetainedFiles = 10;

        private static ILoggerFactory? _factory;

        private static readonly IReadOnlyDictionary<string, LogEventLevel> EmptyOverrides =
            new Dictionary<string, LogEventLevel>();

        public static string LogsDirectory => Path.Combine(AppDataDirectory.Path, "logs");

        // Call once, as early as possible in startup - configures Serilog's sinks
        // (file/console/in-memory) only. Returns the path of this run's log file
        // purely for the "where do I find my logs" message. Doesn't produce a
        // usable ILogger by itself - see UseLoggerFactory below, called right
        // after this from App.axaml.cs once the DI container's own
        // AddLogging(builder => builder.AddSerilog()) has built a factory
        // wrapping the Log.Logger just configured here.
        // fileSizeLimitBytes caps this run's file and rolls to a numbered
        // sibling when it fills, for a host that stays up for weeks at a time
        // (Flower.Server) rather than for the length of a desktop session. Left
        // null - one unbounded file per launch - for the app itself, where a
        // run is short enough that a size cap would only ever split a log
        // nobody needed split. Note the interaction with DeleteOldLogs below:
        // retention counts *files*, so a rolling host keeps the newest 10
        // segments rather than the newest 10 runs.
        // minimumLevel is the floor for every sink. Debug by default, which is
        // what this used to hard-code. Verbose is the opt-in: the per-tick lines
        // (discovery polls, LibVLC callback tracing) log at Trace precisely so
        // they cost nothing until somebody is chasing a bug and asks for them -
        // at the default floor they are never written at all.
        // categoryOverrides raises or lowers that floor for one source-context
        // prefix - "Microsoft.AspNetCore" to mute the framework's per-request
        // narration, "Flower" to let this app's own Debug lines through under a
        // higher floor. This is the only level gate that does anything: the
        // Microsoft.Extensions.Logging filters that look like they sit in front
        // of it do not, because AddSerilog registers a provider-scoped Trace
        // rule that outranks them (see LogLevelSettings in Flower.Server, which
        // is where the server's Logging:LogLevel section gets translated into
        // these arguments).
        public static string Initialize(
            long? fileSizeLimitBytes = null,
            LogEventLevel minimumLevel = LogEventLevel.Debug,
            IReadOnlyDictionary<string, LogEventLevel>? categoryOverrides = null)
        {
            Directory.CreateDirectory(LogsDirectory);
            DeleteOldLogs();

            var path = Path.Combine(LogsDirectory, $"flower-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");

            var configuration = new LoggerConfiguration()
                .MinimumLevel.Is(minimumLevel);
            foreach (var (category, level) in categoryOverrides ?? EmptyOverrides)
                configuration = configuration.MinimumLevel.Override(category, level);

            Log.Logger = configuration
                .Enrich.FromLogContext()
                .WriteTo.File(path,
                    outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                    fileSizeLimitBytes: fileSizeLimitBytes,
                    rollOnFileSizeLimit: fileSizeLimitBytes != null,
                    retainedFileCountLimit: null)
                // Same content as the file sink, just live in the terminal - added
                // specifically so sync activity (discovery, playlist/library sync
                // decisions, trust gate) can be watched in real time while testing,
                // rather than only readable after the fact from the log file.
                .WriteTo.Console(outputTemplate:
                    "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                // Feeds the in-app Log window's live view of this device and
                // the snapshot LibrarySyncService pushes to a paired Server -
                // see InMemoryLogStore/InMemoryLogEventSink.
                .WriteTo.Sink(new InMemoryLogEventSink(InMemoryLogStore.Instance))
                .CreateLogger();

            return path;
        }

        // Backs every CreateLogger/CreateTypedLogger call below with the same
        // Microsoft.Extensions.Logging.ILoggerFactory the DI container itself
        // uses (built via services.AddLogging(builder => builder.AddSerilog())
        // near the top of App.axaml.cs's Bootstrap, right after Initialize()
        // above) - so ad-hoc-constructed classes (LibraryStore, DeviceKeyStore,
        // etc., built with `new` before the container has anything to inject
        // into) and genuinely-static call sites (RubberBandScroll, AlbumArtLoader
        // - a Control/static class Avalonia or app code constructs directly, not
        // DI-resolved) end up logging through the exact same pipeline as
        // constructor-injected ILogger<T> does for DI-resolved classes, rather
        // than a second independent factory wrapping the same Log.Logger.
        public static void UseLoggerFactory(ILoggerFactory factory) => _factory = factory;

        // For classes constructed ad-hoc (new PlaylistStore(), etc.) rather than
        // through the DI container - DI-resolved classes, and ad-hoc-constructed
        // ones with a real constructor to put an ILogger<T> parameter on (see
        // LibraryStore, CreateTypedLogger below), can just take ILogger<T>
        // directly instead once AddLogging is wired up (see App.axaml.cs), and
        // will get the same underlying factory either way.
        //
        // Never throws when called before UseLoggerFactory, and - the part that
        // matters - never *pins* itself to a no-op logger either. Many of these
        // call sites are static logger fields, evaluated the first time their
        // class is touched, and not all of them are touched after startup:
        // anything a platform entry point constructs (Flower.iOS's AppDelegate
        // sets up PlatformMdns/PlatformAudioSession in CustomizeAppBuilder)
        // runs *before* App.OnFrameworkInitializationCompleted, which is where
        // Initialize()/UseLoggerFactory() live. Handing those a plain
        // NullLogger.Instance silently disabled that class's logging for the
        // rest of the process - a whole file's worth of diagnostics that looked
        // present in the source and never reached the log.
        //
        // So the returned logger resolves the factory on use and caches it once
        // it appears. Unit tests, which construct logged classes without ever
        // running app startup, still get a no-op - the factory just never
        // arrives - which is the right behaviour there rather than crashing.
        public static ILogger CreateLogger<T>() => CreateLogger(typeof(T).FullName ?? typeof(T).Name);

        public static ILogger CreateLogger(string categoryName) => new DeferredLogger(categoryName);

        // For classes constructed at the composition root (App.axaml.cs) before
        // the DI container exists, but whose constructor still wants a proper
        // ILogger<T> - the same generic type the container would inject
        // automatically for a class it constructs itself (see MainViewModel) -
        // rather than the untyped ILogger CreateLogger<T>() above.
        public static ILogger<T> CreateTypedLogger<T>() => new DeferredLogger<T>();

        // Resolves _factory lazily instead of at construction, so a logger
        // handed out before UseLoggerFactory starts working the moment it runs
        // - see CreateLogger's remarks for the bug this exists to prevent.
        // Caching the resolved logger keeps the hot path (IsEnabled on a Trace
        // line) to a field read; the race to fill it is benign, since every
        // racer computes the same value from the same factory.
        private class DeferredLogger : ILogger
        {
            private readonly string _categoryName;
            private ILogger? _resolved;

            public DeferredLogger(string categoryName) => _categoryName = categoryName;

            protected ILogger Inner
            {
                get
                {
                    if (_resolved != null)
                        return _resolved;

                    var factory = _factory;
                    if (factory == null)
                        return NullLogger.Instance;

                    return _resolved = factory.CreateLogger(_categoryName);
                }
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => Inner.BeginScope(state);

            public bool IsEnabled(LogLevel logLevel) => Inner.IsEnabled(logLevel);

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                Inner.Log(logLevel, eventId, state, exception, formatter);
        }

        // The ILogger<T> flavour of the same deferral. Category name matches
        // what the DI container's own Logger<T> would produce, so a class that
        // later moves from CreateTypedLogger to constructor injection keeps
        // logging under the same name.
        private sealed class DeferredLogger<T> : DeferredLogger, ILogger<T>
        {
            public DeferredLogger()
                : base(typeof(T).FullName ?? typeof(T).Name)
            {
            }
        }

        // Flushes buffered log entries to disk - call on shutdown (see
        // MainWindow's Closing handler) so the last few lines of a session
        // aren't lost the same way library.json saves used to be.
        public static void Shutdown() => Log.CloseAndFlush();

        private static void DeleteOldLogs()
        {
            var files = new DirectoryInfo(LogsDirectory)
                .GetFiles("flower-*.log")
                .OrderByDescending(f => f.Name) // the timestamp in the name sorts chronologically
                .Skip(MaxRetainedFiles - 1) // leave room for the file this run is about to create
                .ToList();

            foreach (var file in files)
            {
                try
                {
                    file.Delete();
                }
                catch
                {
                    // Best effort - e.g. another instance still has it open. Not
                    // worth failing startup over a leftover log file.
                }
            }
        }
    }
}
