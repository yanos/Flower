using System;
using System.IO;
using System.Linq;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Serilog;
using Serilog.Extensions.Logging;

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

        public static string LogsDirectory => Path.Combine(AppDataDirectory.Path, "logs");

        // Call once, as early as possible in startup. Returns the path of this
        // run's log file purely for the "where do I find my logs" message.
        public static string Initialize()
        {
            Directory.CreateDirectory(LogsDirectory);
            DeleteOldLogs();

            var path = Path.Combine(LogsDirectory, $"flower-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.FromLogContext()
                .WriteTo.File(path, outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                // Same content as the file sink, just live in the terminal - added
                // specifically so sync activity (discovery, playlist/library sync
                // decisions, trust gate) can be watched in real time while testing,
                // rather than only readable after the fact from the log file.
                .WriteTo.Console(outputTemplate:
                    "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                // Feeds the in-app Log window's "This Device" live view and
                // the snapshot LibrarySyncService pushes to a paired Server -
                // see InMemoryLogStore/InMemoryLogEventSink.
                .WriteTo.Sink(new InMemoryLogEventSink(InMemoryLogStore.Instance))
                .CreateLogger();

            _factory = new SerilogLoggerFactory(Log.Logger, dispose: false);

            return path;
        }

        // For classes constructed ad-hoc (new PlaylistStore(), etc.) rather than
        // through the DI container - DI-resolved classes, and ad-hoc-constructed
        // ones with a real constructor to put an ILogger<T> parameter on (see
        // LibraryStore, CreateTypedLogger below), can just take ILogger<T>
        // directly instead once AddLogging is wired up (see App.axaml.cs), and
        // will get the same underlying factory either way.
        //
        // Falls back to a no-op logger if called before Initialize() rather than
        // throwing: some of these classes (Library, PlaylistControlViewModel,
        // PlaylistStore/AppSettingsStore) have a static logger field, evaluated
        // the first time the class is touched - in the real app that's always
        // after Initialize() (the first line of App.OnFrameworkInitializationCompleted),
        // but unit tests construct these classes directly without ever running
        // app startup, so silently discarding log output there is the right
        // behavior rather than crashing every test that touches a logged class.
        public static ILogger CreateLogger<T>() => CreateLogger(typeof(T).FullName ?? typeof(T).Name);

        public static ILogger CreateLogger(string categoryName) =>
            _factory?.CreateLogger(categoryName) ?? NullLogger.Instance;

        // For classes constructed at the composition root (App.axaml.cs) before
        // the DI container exists, but whose constructor still wants a proper
        // ILogger<T> - the same generic type the container would inject
        // automatically for a class it constructs itself (see MainViewModel) -
        // rather than the untyped ILogger CreateLogger<T>() above.
        public static ILogger<T> CreateTypedLogger<T>() =>
            _factory != null ? new Logger<T>(_factory) : NullLogger<T>.Instance;

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
