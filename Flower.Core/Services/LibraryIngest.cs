using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Logging;
using Flower.Models;

namespace Flower.Services;

// What became of one step of an upload. Everything but the first two is a
// refusal, and the kinds are kept apart because the device on the other end
// does something different about each - see LibraryMirrorService.
public enum IngestOutcome
{
    // More bytes are wanted, from Status.Offset.
    Accepted,

    // The file is in the library; Status.TrackId is the song.
    Completed,

    // The request itself is wrong - a path that climbs out of the folder, a
    // file type no scan would take, a file that is not audio once it arrives.
    // Asking again will not help.
    Invalid,

    // A different file already sits where this one wants to go. Nothing is
    // overwritten: which of the two is right is not something a sync can know.
    Conflict,

    // Nowhere to write - no library folder, or one this process cannot write
    // to. Not about this file; nothing else will upload either until an
    // operator changes something.
    Unavailable,

    // No such upload in progress - a server restarted mid-file answers this,
    // and beginning again picks the staged bytes back up - or no such song,
    // for a move.
    UnknownUpload,

    // Every byte arrived and they do not add up to the file that was promised.
    Corrupt,
}

public sealed record IngestResult(
    IngestOutcome Outcome, LibraryUploadStatusDto? Status = null, string? Error = null, LibraryMoveResponseDto? Moved = null);

// A file arriving from one of the owner's devices, taken into this server's
// library: the "ingest" docs/SYNC-PLAN.md says the star topology was missing.
// A server can only serve music it has, and until this the only way music got
// there was somebody copying it onto the server's disk by hand.
//
// It lands where it was. The device says what the file is called below its own
// library folder ("Angine de Poitrine/Vol.II/01 Fabienk.mp3"), and that is
// where it goes below this server's - the tree is the library's own
// organisation, not layout, and a library that arrives as a flat folder of ids
// is one nobody can find anything in afterwards. That is the mirror image of
// what a download already does with TrackDto.RelativePath.
//
// Which makes this the one place a path arrives from the wire, where every
// other route here was careful to accept only catalog ids. So the path is
// taken apart and rebuilt rather than trusted: no segment may be "." or "..",
// no folder may be hidden, the type must be one a scan would import, and the
// result must still be inside the folder it was resolved against. And no file
// in the library is ever overwritten by a different one - a name that is taken
// is a refusal. The two things that do give way are a file that was removed
// from the library and is only waiting to be cleaned up, and the old version
// of a song whose own device has sent a new one (ReplacesTrackId).
//
// In pieces, because a signed request has to be buffered whole before its
// signature can be checked (see AdminEndpoints' filter) and a lossless album
// track is larger than anything this server will buffer. Each piece is an
// ordinary signed request, so the trust boundary needed no second way in; and
// because the pieces are staged on disk under the file's own hash, an upload
// cut off by a phone going to sleep resumes instead of starting over.
//
// In Flower.Core rather than the server project for the reason
// LibraryRemoval is: SimulatedFlowerServer answers a client test through this
// same class, so the scenario tests exercise the code the server runs.
public sealed class LibraryIngest
{
    // One piece. Under the server's 20 MB body ceiling with room to spare, and
    // small enough that losing one to a dropped connection costs seconds.
    public const int MaxChunkBytes = 8 * 1024 * 1024;

    // A sanity bound rather than a policy: a four-hour DJ set in FLAC is about
    // 1.5 GB, and nothing that is a song is larger than this.
    public const long MaxFileBytes = 4L * 1024 * 1024 * 1024;

    private const int MaxRelativePathLength = 1024;

    // How long the bytes of an upload nobody came back for are kept.
    private static readonly TimeSpan StagedFileLifetime = TimeSpan.FromDays(7);

    private readonly Library _library;
    private readonly Func<IReadOnlyList<string>> _libraryRoots;
    private readonly string _stagingDirectory;
    private readonly Importer.Importer _importer;
    private readonly ILogger _logger;

    // One upload at a time. An owner's device sends them one after another
    // anyway, and serialising here is what lets "is this name taken" and
    // "move the file there" be two steps.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    private sealed record Session(
        string Target, string RelativePath, long Length, string Sha256, DateTimeOffset DateAdded, bool Replaces);

    public LibraryIngest(
        Library library, Func<IReadOnlyList<string>> libraryRoots, string stagingDirectory,
        Importer.Importer importer, ILogger logger)
    {
        _library = library;
        _libraryRoots = libraryRoots;
        _stagingDirectory = stagingDirectory;
        _importer = importer;
        _logger = logger;
    }

    public async Task<IngestResult> BeginAsync(LibraryUploadRequestDto request, CancellationToken ct = default)
    {
        if (!IsSha256(request.Sha256))
            return Refuse(IngestOutcome.Invalid, "The file's checksum is not a SHA-256.");
        if (request.Length is <= 0 or > MaxFileBytes)
            return Refuse(IngestOutcome.Invalid, "The file is empty, or larger than this server accepts.");

        // A new version of a song already here goes where that song's file
        // is. The path the device names is its own; where this server keeps
        // the song is this server's, and a move is a separate request.
        string target;
        var replaces = request.ReplacesTrackId is { Length: > 0 };
        if (replaces)
        {
            if (_library.Find(request.ReplacesTrackId) is not { Path: { } current })
                return Refuse(IngestOutcome.Invalid, "This server no longer has the song that file replaces.");
            target = current;
        }
        else if (ResolveTarget(request.RelativePath, _libraryRoots(), out target) is { } refusal)
        {
            return refusal;
        }

        var sha256 = request.Sha256.ToLowerInvariant();
        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(target))
            {
                // The same bytes under the same name is not a conflict, it is
                // a file that got here some other way - copied by hand, or
                // removed from the library and left on disk. Taking it in is
                // what the upload was for. For a replacement it means there is
                // nothing to replace: the server's copy already is this one.
                if (await HasContentAsync(target, request.Length, sha256, ct))
                {
                    return replaces
                        ? new IngestResult(IngestOutcome.Completed, new LibraryUploadStatusDto(
                            null, request.Length, request.ReplacesTrackId, null, _library.ChangeToken))
                        : Adopt(target, request.RelativePath, request.Length, request.DateAdded);
                }

                // A different file is in the way, and it stays - unless it is
                // one that was removed from the library and set aside, which
                // is a file waiting to be cleaned up, not one anybody is
                // keeping. That gives way (see CompleteAsync).
                if (!replaces && !_library.IsExcludedPath(target))
                {
                    return Refuse(IngestOutcome.Conflict,
                        $"This server already has a different file at {request.RelativePath}.");
                }
            }

            if (!CanWriteWhere(target))
                return Refuse(IngestOutcome.Unavailable, CannotWrite);

            Directory.CreateDirectory(_stagingDirectory);
            SweepStagedFiles();

            var uploadId = $"{sha256}-{request.Length}";
            var part = PartPath(uploadId);
            var offset = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (offset > request.Length)
            {
                File.Delete(part);
                offset = 0;
            }

            var session = new Session(target, request.RelativePath, request.Length, sha256, request.DateAdded, replaces);
            _sessions[uploadId] = session;

            // Every byte is already staged: an earlier attempt got them all
            // here and did not get to hear so.
            if (offset == request.Length)
                return await CompleteAsync(uploadId, session, ct);

            return new IngestResult(IngestOutcome.Accepted, new LibraryUploadStatusDto(uploadId, offset));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IngestResult> AppendAsync(
        string uploadId, long offset, ReadOnlyMemory<byte> chunk, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_sessions.TryGetValue(uploadId, out var session))
                return Refuse(IngestOutcome.UnknownUpload, "No such upload is in progress.");

            var part = PartPath(uploadId);
            var staged = File.Exists(part) ? new FileInfo(part).Length : 0;

            // Not where this server left off - a retry of a piece that did
            // land, most likely. Nothing is written; the answer says where to
            // carry on from, and the device does.
            if (offset != staged)
                return new IngestResult(IngestOutcome.Accepted, new LibraryUploadStatusDto(uploadId, staged));

            if (chunk.Length == 0 || chunk.Length > MaxChunkBytes || staged + chunk.Length > session.Length)
                return Refuse(IngestOutcome.Invalid, "That piece does not fit the file it belongs to.");

            await using (var stream = new FileStream(part, FileMode.Append, FileAccess.Write, FileShare.None))
                await stream.WriteAsync(chunk, ct);

            staged += chunk.Length;
            if (staged < session.Length)
                return new IngestResult(IngestOutcome.Accepted, new LibraryUploadStatusDto(uploadId, staged));

            return await CompleteAsync(uploadId, session, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Callers hold _gate.
    private async Task<IngestResult> CompleteAsync(string uploadId, Session session, CancellationToken ct)
    {
        var part = PartPath(uploadId);
        if (!await HasContentAsync(part, session.Length, session.Sha256, ct))
        {
            // Gone, so the next attempt starts clean rather than resuming
            // onto bytes already known to be wrong.
            _sessions.Remove(uploadId);
            File.Delete(part);
            _logger.LogWarning("An upload of {RelativePath} arrived in full and failed its checksum; discarded", session.RelativePath);
            return Refuse(IngestOutcome.Corrupt, "The file did not arrive intact.");
        }

        if (session.Replaces)
            return Replace(uploadId, session, part);

        try
        {
            if (File.Exists(session.Target))
            {
                if (!_library.IsExcludedPath(session.Target))
                {
                    _sessions.Remove(uploadId);
                    File.Delete(part);
                    return Refuse(IngestOutcome.Conflict, $"This server already has a different file at {session.RelativePath}.");
                }

                // Set aside and now in the way of the file that replaces it.
                // To the trash where there is one, since nobody has yet said
                // "delete it for good" - that is what Removed Songs is for.
                if (!FileTrash.TryMoveToTrash(session.Target))
                    File.Delete(session.Target);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(session.Target)!);
            File.Move(part, session.Target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The staged bytes stay: this is about the music folder, not the
            // file, and once it is fixed the next attempt completes from here
            // without sending anything again.
            _logger.LogWarning(ex, "Could not move an uploaded file into place at {Path}", LogPath.Short(session.Target));
            return Refuse(IngestOutcome.Unavailable, CannotWrite);
        }

        _sessions.Remove(uploadId);
        return Adopt(session.Target, session.RelativePath, session.Length, session.DateAdded, uploaded: true);
    }

    // A new version of a file the library already has, swapped in under it.
    // The old one is moved aside first and only let go once the new one has
    // been read back as a song: a replacement the importer cannot read must
    // not cost the library the copy it could.
    //
    // Callers hold _gate.
    private IngestResult Replace(string uploadId, Session session, string part)
    {
        var target = session.Target;
        var aside = Path.Combine(Path.GetDirectoryName(target)!, $".flower-replaced-{Guid.NewGuid():N}");
        var hadOld = File.Exists(target);

        // Read while the old file still has a name TagLib can make sense of.
        var oldArt = hadOld ? LocalAlbumArtReader.EmbeddedIn(target) : null;
        try
        {
            if (hadOld)
                File.Move(target, aside);
            File.Move(part, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (hadOld && File.Exists(aside) && !File.Exists(target))
                File.Move(aside, target);
            _logger.LogWarning(ex, "Could not replace {Path} with its uploaded version", LogPath.Short(target));
            return Refuse(IngestOutcome.Unavailable, CannotWrite);
        }

        _sessions.Remove(uploadId);
        if (_importer.ImportFile(target) is not { } track)
        {
            TryDelete(target);
            if (hadOld)
                File.Move(aside, target);
            return Refuse(IngestOutcome.Invalid, $"The new version of {session.RelativePath} is not an audio file this server can read.");
        }

        if (hadOld)
            TryDelete(aside);

        // New tags are an edit, made wherever the file came from, and dated
        // now so that every other device takes them (Track.TagsEditedAt). A
        // new version with the same tags - new artwork, a re-encode - is not
        // one, and asks nothing of anybody else's copy.
        // The picture likewise (Track.ArtEditedAt).
        var now = DateTimeOffset.UtcNow;
        var before = _library.Tracks.FirstOrDefault(t => string.Equals(t.Path, target, StringComparison.OrdinalIgnoreCase));
        DateTimeOffset? edited = before != null && TrackTags.Of(before) != TrackTags.Of(track) ? now : null;
        DateTimeOffset? repainted = hadOld && !TrackArtwork.Same(oldArt, LocalAlbumArtReader.EmbeddedIn(target)) ? now : null;

        // The same path as before, so this is the same song with new tags:
        // its id, its plays and its star carry over (Library.AddScannedTrack).
        //
        // And a new version is a new file, whatever else it is: dated, so
        // every other copy of the song is replaced with it (Track.FileReplacedAt).
        var replaced = _library.AddScannedTrack(track, tagsEditedAt: edited, artEditedAt: repainted, fileReplacedAt: now);
        _logger.LogInformation("Replaced {RelativePath} with a newer version of the same song ({TrackId})",
            session.RelativePath, replaced.Id.ToKey());

        return new IngestResult(IngestOutcome.Completed,
            new LibraryUploadStatusDto(
                null, session.Length, replaced.Id.ToKey(), replaced.DateAdded, _library.ChangeToken, edited, repainted, now));
    }

    // An owner's device has moved one of its own files, and this server's copy
    // goes to the same place below the folder it is already under. Nothing
    // about the song changes but where its file is - which is the reason to do
    // it here, as a move, rather than leave a scan to find one file missing
    // and another one new.
    public async Task<IngestResult> MoveAsync(LibraryMoveRequestDto request, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_library.Find(request.TrackId) is not { Path: { } from } track)
                return Refuse(IngestOutcome.UnknownUpload, "This server does not have that song.");

            // Under whichever library folder it is in now. A move rearranges a
            // folder; it does not carry a file from one drive to another.
            var roots = _libraryRoots();
            var root = LibraryFolders.RootOf(from, roots);
            if (ResolveTarget(request.RelativePath, root != null ? [root] : roots, out var target) is { } refusal)
                return refusal;

            if (!string.Equals(Path.GetExtension(from), Path.GetExtension(target), StringComparison.OrdinalIgnoreCase))
                return Refuse(IngestOutcome.Invalid, "A move cannot change what kind of file a song is.");

            if (!string.Equals(from, target, StringComparison.Ordinal))
            {
                try
                {
                    // Only a different file is in the way. The same file under
                    // a name that differs in case alone is the move itself, on
                    // a filesystem that does not tell the two names apart.
                    var sameFile = !OperatingSystem.IsLinux()
                                   && string.Equals(from, target, StringComparison.OrdinalIgnoreCase);
                    if (!sameFile && File.Exists(target))
                    {
                        if (!_library.IsExcludedPath(target))
                            return Refuse(IngestOutcome.Conflict, $"This server already has a file at {request.RelativePath}.");
                        if (!FileTrash.TryMoveToTrash(target))
                            File.Delete(target);
                    }

                    if (!File.Exists(from))
                        return Refuse(IngestOutcome.Invalid, "This server's file for that song is missing.");

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(from, target);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not move {From} to {To}", LogPath.Short(from), LogPath.Short(target));
                    return Refuse(IngestOutcome.Unavailable, CannotWrite);
                }

                _library.RestoreExcludedPaths([target]);
                _library.MoveTrack(track, target);
                LibraryFolders.RemoveEmptyFolders(Path.GetDirectoryName(from), root);
                _logger.LogInformation("Moved {TrackId} to {RelativePath}", track.Id.ToKey(), request.RelativePath);
            }

            return new IngestResult(IngestOutcome.Completed,
                Moved: new LibraryMoveResponseDto(request.RelativePath, _library.ChangeToken));
        }
        finally
        {
            _gate.Release();
        }
    }

    private const string CannotWrite = "This server cannot write to its music folder - it may be mounted read-only.";

    // A file that is in place, taken into the library.
    private IngestResult Adopt(string target, string relativePath, long length, DateTimeOffset dateAdded, bool uploaded = false)
    {
        if (_importer.ImportFile(target) is not { } track)
        {
            // Only what this upload itself put there is taken back out. A file
            // that was already on disk is somebody's, readable or not.
            if (uploaded)
                TryDelete(target);
            return Refuse(IngestOutcome.Invalid, $"{relativePath} is not an audio file this server can read.");
        }

        // A file removed from the library and kept on disk is kept out of
        // every scan (Library.RemoveTracks). An owner's device offering it
        // again is the owner putting it back.
        _library.RestoreExcludedPaths([target]);

        var added = _library.AddScannedTrack(track, dateAdded);
        _logger.LogInformation("Took {RelativePath} into the library as {TrackId}", relativePath, added.Id.ToKey());

        return new IngestResult(IngestOutcome.Completed,
            new LibraryUploadStatusDto(null, length, added.Id.ToKey(), added.DateAdded, _library.ChangeToken));
    }

    // Null when the path is one to accept, with where it resolves to.
    //
    // The first configured folder that exists is where uploads go. One
    // destination, stated, rather than a guess at which of several folders a
    // device's file "belongs" in - the device's own folders are its own
    // business, and only the part of the path below them travels.
    internal static IngestResult? ResolveTarget(string? relativePath, IReadOnlyList<string> roots, out string target)
    {
        target = "";

        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > MaxRelativePathLength)
            return Refuse(IngestOutcome.Invalid, "A file name is required.");

        var segments = relativePath.Split('/');
        var invalid = Path.GetInvalidFileNameChars();
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var isFolder = i < segments.Length - 1;
            if (segment.Length == 0 || segment is "." or ".."
                || segment.IndexOfAny(invalid) >= 0
                || segment.Any(char.IsControl)
                // The same folders a scan skips (Importer.IsUnderHiddenFolder):
                // a file put under one would be in the library until the next
                // scan and then silently not.
                || (isFolder && segment.StartsWith('.')))
            {
                return Refuse(IngestOutcome.Invalid, $"{relativePath} is not a path this server will write to.");
            }
        }

        if (!Importer.Importer.IsImportable(segments[^1]))
            return Refuse(IngestOutcome.Invalid, $"{relativePath} is not a kind of file this server imports.");

        var root = roots.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r));
        if (root == null)
            return Refuse(IngestOutcome.Unavailable, "This server has no music folder to put the file in.");

        var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine([rootPath, .. segments]));

        // Belt and braces over the segment checks above: whatever the
        // platform made of the name, it has to still be inside the folder.
        if (!full.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return Refuse(IngestOutcome.Invalid, $"{relativePath} is not a path this server will write to.");

        target = full;
        return null;
    }

    // Asked before the first byte is sent rather than found out after the
    // last one. A music folder mounted read-only, or owned by a user this
    // process is not, is an ordinary way to run a server, and a device that
    // learned of it from a failed move would have uploaded a whole album to
    // hear it.
    private static bool CanWriteWhere(string target)
    {
        var folder = Path.GetDirectoryName(target);
        while (folder != null && !Directory.Exists(folder))
            folder = Path.GetDirectoryName(folder);
        return folder != null && LibraryFolders.CanWriteIn(folder);
    }

    private static async Task<bool> HasContentAsync(string path, long length, string sha256, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != length)
            return false;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return string.Equals(Convert.ToHexStringLower(hash), sha256, StringComparison.Ordinal);
    }

    private void SweepStagedFiles()
    {
        try
        {
            var cutoff = DateTime.UtcNow - StagedFileLifetime;
            foreach (var staged in Directory.EnumerateFiles(_stagingDirectory, "*.part"))
            {
                if (File.GetLastWriteTimeUtc(staged) < cutoff)
                    File.Delete(staged);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not tidy abandoned uploads out of {Folder}", _stagingDirectory);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not take an unreadable upload back out of {Path}", LogPath.Short(path));
        }
    }

    // The id is the file's own hash and length, so it is also a safe file
    // name - IsSha256 has already held it to hex.
    private string PartPath(string uploadId) => Path.Combine(_stagingDirectory, uploadId + ".part");

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static IngestResult Refuse(IngestOutcome outcome, string error) => new(outcome, null, error);
}
