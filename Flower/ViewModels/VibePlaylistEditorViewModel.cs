using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Flower.Models;
using Flower.SemanticAudio;
using Flower.Services;

namespace Flower.ViewModels;

// Backs New Vibe Playlist… and Edit Vibe Playlist… (docs/VIBE-PLAYLIST-PLAN.md):
// a name, the sentence, the No vocals switch, and the songs the sentence
// would make, asked of the server whenever typing pauses. Knows nothing about
// windows - a dialog on the desktop and a panel in the browser host the same
// view over it.
public sealed class VibePlaylistEditorViewModel : ViewModelBase
{
    // The same pause the smart playlist editor waits for. Not readonly, so a
    // test can wait for none.
    internal static TimeSpan TypingPause = TimeSpan.FromSeconds(1);

    // Phase 0's prompts, so the box says by example what it is for.
    public const string PromptPlaceholder = "music for a rainy day";

    private readonly Playlist _playlist;
    private readonly Library _library;
    private readonly IVibePlaylists _vibes;
    private readonly bool _isNew;

    private CancellationTokenSource? _previewCts;
    private Task _previewPass = Task.CompletedTask;

    public VibePlaylistEditorViewModel(Playlist playlist, Library library, IVibePlaylists vibes, bool isNew = false)
    {
        _playlist = playlist;
        _library = library;
        _vibes = vibes;
        _isNew = isNew;

        _name = isNew ? string.Empty : playlist.Name;
        _prompt = playlist.VibePrompt ?? string.Empty;
        _instrumental = playlist.VibeInstrumental;

        if (_prompt.Length > 0)
            SchedulePreview(afterTypingPause: false);
    }

    public Playlist Playlist => _playlist;

    public bool IsNew => _isNew;

    public string Title => _isNew ? "New Vibe Playlist" : "Edit Vibe Playlist";

    private string _name;
    public string Name
    {
        get => _name;
        set
        {
            _name = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    // What the name becomes when none was typed: the sentence itself, which is
    // the best description of the playlist there is.
    public string NamePlaceholder => _prompt.Trim() is { Length: > 0 } prompt ? Capitalized(prompt) : "Playlist Name";

    private string _prompt;
    public string Prompt
    {
        get => _prompt;
        set
        {
            value ??= string.Empty;
            if (value == _prompt)
                return;

            _prompt = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NamePlaceholder));
            OnPropertyChanged(nameof(CanSave));

            // A sentence that says "instrumental" or "without lyrics" turns
            // the switch on, and the words are left out of what is asked
            // (SemanticAudioFilters.Parse). It never turns it off: that is
            // the switch's to say.
            if (!_instrumental && SemanticAudioFilters.Parse(value).AsksInstrumental)
                Instrumental = true;

            SchedulePreview(afterTypingPause: true);
        }
    }

    private bool _instrumental;
    public bool Instrumental
    {
        get => _instrumental;
        set
        {
            if (value == _instrumental)
                return;

            _instrumental = value;
            OnPropertyChanged();
            SchedulePreview(afterTypingPause: false);
        }
    }

    public bool CanSave => _prompt.Trim().Length > 0;

    // ── Preview ──────────────────────────────────────────────────────────────

    private IReadOnlyList<Track> _previewTracks = [];
    public IReadOnlyList<Track> PreviewTracks
    {
        get => _previewTracks;
        private set
        {
            _previewTracks = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasPreview));
        }
    }

    public bool HasPreview => _previewTracks.Count > 0;

    private bool _isPreviewing;
    public bool IsPreviewing
    {
        get => _isPreviewing;
        private set
        {
            _isPreviewing = value;
            OnPropertyChanged();
        }
    }

    // One line under the box: what the list is, or why there is none.
    private string _previewSummary = "Describe how the songs should sound, and the best matches show here.";
    public string PreviewSummary
    {
        get => _previewSummary;
        private set
        {
            _previewSummary = value;
            OnPropertyChanged();
        }
    }

    // Lets a test wait for the preview it caused.
    public Task PreviewPass => _previewPass;

    private void SchedulePreview(bool afterTypingPause)
    {
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        _previewPass = RunPreviewAsync(afterTypingPause ? TypingPause : TimeSpan.Zero, _previewCts.Token);
    }

    private void StopPreview() => _previewCts?.Cancel();

    private async Task RunPreviewAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, token);

            var prompt = _prompt.Trim();
            if (prompt.Length == 0)
            {
                PreviewTracks = [];
                PreviewSummary = "Describe how the songs should sound, and the best matches show here.";
                return;
            }

            IsPreviewing = true;
            var preview = await _vibes.PreviewAsync(prompt, _instrumental, token);
            if (token.IsCancellationRequested)
                return;

            IsPreviewing = false;
            if (preview.Refusal == VibeRefusal.Busy)
                return;

            PreviewTracks = preview.Tracks;
            PreviewSummary = SummaryOf(preview);
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke started its own.
        }
    }

    private static string SummaryOf(VibePreview preview) => preview.Refusal switch
    {
        VibeRefusal.Off => "Vibe Playlists are off on the server. Its owner can turn them on in its AI Features settings.",
        VibeRefusal.NotReady => "The server hasn't listened to any songs yet. Try again once it has started.",
        VibeRefusal.Unreachable => "Couldn't reach the server to find songs. You can still save, and the songs come when it's back.",
        _ when preview.Tracks.Count == 0 => "No song sounds like that yet.",
        _ => $"The {preview.Tracks.Count} best matches. Playing it keeps going with more like them.",
    };

    // ── Save / cancel ────────────────────────────────────────────────────────

    // True when the playlist was saved, which is also when the dialog may close.
    public bool Save()
    {
        var prompt = _prompt.Trim();
        if (prompt.Length == 0)
            return false;

        var name = string.IsNullOrWhiteSpace(Name) ? Capitalized(prompt) : Name.Trim();
        if (name != _playlist.Name)
            _playlist.Name = name;

        var changed = prompt != _playlist.VibePrompt || _instrumental != _playlist.VibeInstrumental;
        _playlist.VibeInstrumental = _instrumental;
        // Touches UpdatedAt, which is what sync carries to the server: it
        // ranks the pool from the sentence and every device takes that.
        _playlist.VibePrompt = prompt;

        // Until the server's pool arrives, the songs the preview showed: the
        // same head of the same ranking. Not an edit, like the server's own.
        if (changed && _previewTracks.Count > 0)
        {
            _playlist.Materialize(_previewTracks);
            _library.SavePlaylists();
        }

        StopPreview();
        return true;
    }

    // A playlist made only to be edited goes again when the editor is
    // cancelled, as a new smart playlist does.
    public void Cancel()
    {
        StopPreview();
        if (_isNew)
            _library.RemovePlaylist(_playlist);
    }

    private static string Capitalized(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}

// What MainViewModel hands the view when the Vibe Playlist editor should open.
public sealed record VibePlaylistEditorEventArgs(Playlist Playlist, bool IsNew);
