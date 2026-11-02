using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using FFAudio;

using Flower.Models;

using Microsoft.Extensions.Logging;

namespace Flower.SemanticAudio;

// Where a pass is: songs done of songs eligible, and what it is on now. The
// two rates are measured over the pass's last few hundred songs: songs for
// "about 3 h left", clips for what another clip setting would cost here.
public sealed record SemanticAudioProgress(
    int Done,
    int Eligible,
    int Failed,
    string? CurrentTitle,
    string? CurrentArtist,
    double? SongsPerSecond,
    double? ClipsPerSecond = null)
{
    public static readonly SemanticAudioProgress None = new(0, 0, 0, null, null, null);

    public bool Finished => Done >= Eligible;
}

// Listens to the library's songs once, clip by clip, and keeps what it heard
// (docs/VIBE-PLAYLIST-PLAN.md, "Analysing songs"). One pass works through
// every song that is missing a clip the current setting selects, or whose
// clips are stale - heard by another export of the model, or from audio that
// has changed since (AudioStamp) - and commits each song as it finishes, so a
// restart loses at most the song in progress. A tag edit doesn't make a song
// stale: the audio is the same.
public sealed class SemanticAudioAnalyzer(
    SemanticAudioRepository repository,
    IClipSource clips,
    Func<IClapAudioEncoder> openEncoder,
    string model,
    ILogger logger,
    TimeProvider? time = null,
    Func<string, bool>? fileExists = null)
{
    // "Recently added" for the order of work: a new album becomes findable
    // before the back catalogue is finished.
    internal static readonly TimeSpan RecentlyAdded = TimeSpan.FromDays(30);

    // The rate behind "about 3 h left" is measured over this many songs.
    private const int RateWindow = 200;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;
    private SemanticAudioProgress _progress = SemanticAudioProgress.None;

    // A song finished, with every clip it now has (what the index is built
    // from), and songs that have left the library and lost theirs.
    public event Action<AnalysedSong, IReadOnlyDictionary<int, float[]>>? SongAnalysed;
    public event Action<IReadOnlyCollection<Guid>>? SongsRemoved;

    public SemanticAudioProgress Progress => Volatile.Read(ref _progress);

    // What identifies the audio a song's clips were heard from: its length
    // and format as the last scan read them, and when the server last
    // replaced its file (Track.FileReplacedAt, set only when a file's audio is
    // replaced - a re-rip, a re-encode). Deliberately not the file's own size
    // and modification time: tags and artwork live in the file, so every tag
    // edit would have the song heard again, and an artwork job across the
    // library would have all of it heard again.
    public static string AudioStamp(Track track) => string.Join(':',
        (long)track.Duration.TotalMilliseconds,
        track.Codec ?? "",
        track.SampleRate,
        track.Channels,
        track.BitsPerSample,
        track.Bitrate,
        track.FileReplacedAt?.UtcTicks ?? 0);

    internal sealed record WorkItem(Track Track, string Path, string Stamp, AnalysedSong? Existing, bool Stale);

    internal sealed record Plan(IReadOnlyList<WorkItem> Work, int Eligible, int Failed, IReadOnlyCollection<Guid> Gone);

    // Which songs a pass has to do, in the order it does them.
    internal static Plan MakePlan(
        IReadOnlyList<Track> tracks,
        IReadOnlyDictionary<Guid, AnalysedSong> songs,
        int clipsPerSong,
        string model,
        Func<string, bool> fileExists,
        DateTimeOffset now)
    {
        var present = new HashSet<Guid>(tracks.Count);
        var work = new List<WorkItem>();
        var eligible = 0;
        var failed = 0;
        foreach (var track in tracks)
        {
            present.Add(track.Id);
            if (track.Path is not { Length: > 0 } path || !fileExists(path))
                continue;

            var stamp = AudioStamp(track);
            eligible++;
            if (!songs.TryGetValue(track.Id, out var song))
            {
                work.Add(new WorkItem(track, path, stamp, null, false));
                continue;
            }

            if (song.Model != model || song.FileStamp != stamp)
            {
                work.Add(new WorkItem(track, path, stamp, song, true));
                continue;
            }

            // A file that couldn't be heard is tried again only once it changes.
            if (song.Failed)
            {
                failed++;
                continue;
            }

            if (ClipGrid.Select(song.WindowCount, song.Duration, clipsPerSong, song.Windows).Any(k => !song.Windows.Contains(k)))
                work.Add(new WorkItem(track, path, stamp, song, false));
        }

        var gone = songs.Keys.Where(id => !present.Contains(id)).ToList();
        var ordered = work
            .OrderByDescending(w => now - w.Track.DateAdded <= RecentlyAdded)
            .ThenByDescending(w => now - w.Track.DateAdded <= RecentlyAdded ? w.Track.DateAdded.UtcTicks : w.Track.TotalPlayCount)
            .ThenByDescending(w => w.Track.DateAdded)
            .ToList();
        return new Plan(ordered, eligible, failed, gone);
    }

    // One pass over the library. Synchronous and CPU-bound: the engine runs it
    // on a thread of its own, below normal priority.
    public void RunPass(IReadOnlyList<Track> tracks, int clipsPerSong, CancellationToken cancellationToken)
    {
        var plan = MakePlan(tracks, repository.LoadSongs(), clipsPerSong, model, _fileExists, _time.GetUtcNow());
        if (plan.Gone.Count > 0)
        {
            repository.Delete(plan.Gone);
            SongsRemoved?.Invoke(plan.Gone);
        }

        var done = plan.Eligible - plan.Work.Count;
        var failed = plan.Failed;
        Publish(new SemanticAudioProgress(done, plan.Eligible, failed, null, null, null));
        if (plan.Work.Count == 0)
            return;

        logger.LogInformation("Semantic audio: {Count} of {Eligible} songs to analyse", plan.Work.Count, plan.Eligible);
        // When each song finished, and how many clips had been heard by then.
        var finishedAt = new Queue<(long At, long Clips)>();
        long clipsHeard = 0;
        using var encoder = openEncoder();
        foreach (var item in plan.Work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Publish(_progress with { CurrentTitle = item.Track.Title, CurrentArtist = item.Track.Artists });

            var heard = Analyse(item, clipsPerSong, encoder, cancellationToken);
            if (heard < 0)
                failed++;
            else
                clipsHeard += heard;

            done++;
            finishedAt.Enqueue((_time.GetTimestamp(), clipsHeard));
            if (finishedAt.Count > RateWindow)
                finishedAt.Dequeue();
            var (songs, clips) = Rates(finishedAt);
            Publish(new SemanticAudioProgress(done, plan.Eligible, failed, null, null, songs, clips));
        }
    }

    // The clips heard, or -1 when the song couldn't be analysed.

    private int Analyse(WorkItem item, int clipsPerSong, IClapAudioEncoder encoder, CancellationToken cancellationToken)
    {
        var have = item.Stale || item.Existing is null ? new HashSet<int>() : new HashSet<int>(item.Existing.Windows);
        var heard = new Dictionary<int, float[]>();
        TimeSpan duration;
        int windowCount;
        try
        {
            using var reader = clips.Open(item.Path);
            duration = reader.Duration ?? item.Track.Duration;
            if (duration < TimeSpan.FromSeconds(0.1))
                return Fail(item, "too short to hold any audio");

            windowCount = ClipGrid.WindowCount(duration);
            foreach (var window in ClipGrid.Select(windowCount, duration, clipsPerSong, have).Where(k => !have.Contains(k)).Order())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var samples = reader.ReadWindow(window);
                if (samples.Length == 0)
                    continue;

                heard[window] = encoder.Encode(ClipWaveform.Prepare(samples));
            }
        }
        catch (DecodeException ex)
        {
            return Fail(item, ex.Message);
        }
        catch (IOException ex)
        {
            return Fail(item, ex.Message);
        }

        if (heard.Count == 0 && have.Count == 0)
            return Fail(item, "no audio could be read");

        var song = new AnalysedSong(item.Track.Id, model, item.Stamp, duration, windowCount, _time.GetUtcNow(), null,
            have.Union(heard.Keys).ToHashSet());
        repository.SaveSong(song, heard, replace: item.Stale);

        var all = have.Count == 0 ? heard : repository.LoadClips([item.Track.Id]).GetValueOrDefault(item.Track.Id) ?? heard;
        SongAnalysed?.Invoke(song, all);
        return heard.Count;
    }

    private int Fail(WorkItem item, string reason)
    {
        logger.LogInformation("Semantic audio: couldn't analyse {Path}: {Reason}", item.Path, reason);
        var song = new AnalysedSong(item.Track.Id, model, item.Stamp, item.Track.Duration, 0, _time.GetUtcNow(), reason, new HashSet<int>());
        repository.SaveSong(song, new Dictionary<int, float[]>(), replace: true);
        SongsRemoved?.Invoke([item.Track.Id]);
        return -1;
    }

    private (double? Songs, double? Clips) Rates(Queue<(long At, long Clips)> finishedAt)
    {
        if (finishedAt.Count < 2)
            return (null, null);

        var first = finishedAt.Peek();
        var last = finishedAt.Last();
        var seconds = _time.GetElapsedTime(first.At, last.At).TotalSeconds;
        if (seconds <= 0)
            return (null, null);

        var clips = last.Clips - first.Clips;
        return ((finishedAt.Count - 1) / seconds, clips > 0 ? clips / seconds : null);
    }

    private void Publish(SemanticAudioProgress progress) => Volatile.Write(ref _progress, progress);
}
