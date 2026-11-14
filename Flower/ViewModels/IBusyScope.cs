using System;

using Flower.Importer;

namespace Flower.ViewModels;

// One piece of work the status bar is showing, open until disposed. An
// interface so what reports through one (TrackDownloadRunner) can be tested
// without the dispatcher BusyState marshals to.
public interface IBusyScope : IDisposable
{
    // Says something new about the work under way.
    void Say(string message);
}

public static class BusyScopeExtensions
{
    // A run over a known number of songs, as "what: 12 of 340 songs".
    // Silent for a run of none, which has nothing to count.
    public static IProgress<SongProgress> CountingSongs(this IBusyScope scope, string what) => new SongCount(scope, what);

    public static string DescribeSongCount(string what, SongProgress progress) =>
        $"{what}: {progress.Done:N0} of {progress.Total:N0} {(progress.Total == 1 ? "song" : "songs")}";

    // Reporting on the caller's thread, not Progress<T>'s captured context:
    // BusyState does its own marshalling, and better than a post per report.
    private sealed class SongCount(IBusyScope scope, string what) : IProgress<SongProgress>
    {
        public void Report(SongProgress value)
        {
            if (value.Total > 0)
                scope.Say(DescribeSongCount(what, value));
        }
    }
}
