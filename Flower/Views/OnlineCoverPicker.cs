using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Flower.Services;
using Flower.ViewModels;

namespace Flower.Views;

// Find Online, once for both heads' Track Info: the covers OnlineCoverArt
// found for an album, downloaded whole and shown on an ArtworkViewer one at a
// time, the most pixels first (OnlineCoverArt.DownloadAllAsync), and only Use
// This Cover writes one. The search itself stays the host's, since what it
// searches for is read from the host's boxes and its button says "Searching…"
// for only as long as the search takes.
internal static class OnlineCoverPicker
{
    // The boxes rather than the song, so a misspelt album can be corrected and
    // searched again before Save - the first of each that says anything.
    public static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    // What to say under the cover when a search leaves nothing to show, or
    // null when it found something.
    public static string? NothingFound(IReadOnlyList<OnlineCoverArt.Candidate>? found, string album, string title)
    {
        if (found is null)
            return "Couldn't search online. Check the connection, then try again.";
        if (found.Count > 0)
            return null;
        var named = string.IsNullOrWhiteSpace(album) ? title : album;
        return $"No cover found online for “{named}”. Check the album and artist, then try again.";
    }

    /// <summary>
    /// Downloads what <paramref name="found"/> names and steps through it on
    /// <paramref name="viewer"/>, already open, until a cover is taken - which
    /// <paramref name="use"/> writes, under <paramref name="updatingFiles"/>'s
    /// count - or the viewer is closed. Returns what to say under the cover
    /// when nothing could be downloaded, having closed the viewer; null
    /// otherwise.
    /// </summary>
    public static async Task<string?> ChooseAsync(
        ArtworkViewer viewer,
        OnlineCoverArt finder,
        IReadOnlyList<OnlineCoverArt.Candidate> found,
        Func<byte[], string, Task> use,
        Func<string> updatingFiles,
        CancellationToken ct)
    {
        viewer.SetPosition(0, 1);
        viewer.ShowStatus("Downloading…", busy: true);

        IReadOnlyList<OnlineCoverArt.Download> downloads;
        try
        {
            downloads = await finder.DownloadAllAsync(found, ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        if (!viewer.IsOpen || ct.IsCancellationRequested)
            return null;
        if (downloads.Count == 0)
        {
            viewer.Close();
            return "The covers found online couldn't be downloaded. Try again in a moment.";
        }

        var index = 0;
        var shown = 0;
        byte[]? picked = null;

        async Task ShowCandidateAsync()
        {
            var showing = ++shown;
            var (candidate, bytes) = downloads[index];
            picked = null;
            viewer.SetPosition(index, downloads.Count);
            viewer.ShowStatus("Loading…", busy: true);

            var decoded = await Task.Run(() => ArtworkViewer.DecodeForScreen(bytes));
            if (showing != shown || !viewer.IsOpen)
            {
                decoded?.Bitmap.Dispose();
                return;
            }
            if (decoded is not { } picture)
            {
                viewer.ShowStatus("This cover couldn't be read.");
                return;
            }

            picked = bytes;
            var source = OnlineCoverArt.SourceDomain(candidate.ArtworkUrl) is { } domain ? $" · {domain}" : "";
            var position = downloads.Count > 1 ? $" · {index + 1} of {downloads.Count}" : "";
            viewer.ShowPicture(picture.Bitmap,
                string.IsNullOrEmpty(candidate.Artist) ? candidate.Album : $"{candidate.Album} — {candidate.Artist}",
                TrackInfoViewModel.Resolution(picture.Size) + source + position);
        }

        viewer.PreviousRequested += async (_, _) =>
        {
            index--;
            await ShowCandidateAsync();
        };
        viewer.NextRequested += async (_, _) =>
        {
            index++;
            await ShowCandidateAsync();
        };
        viewer.Accepted += async (_, _) =>
        {
            if (picked is not { } bytes)
                return;
            viewer.SetBusy(true, updatingFiles());
            await use(bytes, LocalAlbumArtReader.MimeTypeForBytes(bytes) ?? "image/jpeg");
            viewer.Close();
        };

        await ShowCandidateAsync();
        return null;
    }
}
