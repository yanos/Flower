using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;

using AndroidX.Core.App;
using AndroidX.Core.Content;

using Flower.Importer;
using Flower.Models;

namespace Flower.Android;

// Android has no arbitrary filesystem access under scoped storage, so unlike the
// desktop/iOS Importer this always reads from MediaStore's indexed audio table
// rather than walking a configured folder; libraryPaths is accepted only to satisfy
// IMusicImporter and is otherwise unused.
public class AndroidMediaStoreImporter : IMusicImporter
{
    private const int PermissionRequestCode = 4201;
    private static TaskCompletionSource<bool>? _permissionTcs;

    private readonly Activity _activity;

    public AndroidMediaStoreImporter(Activity activity)
    {
        _activity = activity;
    }

    // Called from MainActivity.OnRequestPermissionsResult, which is where Android
    // delivers the result of the runtime permission prompt requested below.
    public static void HandlePermissionResult(int requestCode, Permission[] grantResults)
    {
        if (requestCode != PermissionRequestCode) return;
        _permissionTcs?.TrySetResult(grantResults.Length > 0 && grantResults[0] == Permission.Granted);
    }

    public async Task<List<Track>> ImportAsync(IEnumerable<string>? libraryPaths = null)
    {
        if (!await EnsurePermissionAsync())
            return new List<Track>();

        return await Task.Run(QueryMediaStore);
    }

    private async Task<bool> EnsurePermissionAsync()
    {
        string permission = Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
            ? Manifest.Permission.ReadMediaAudio!
            : Manifest.Permission.ReadExternalStorage!;

        if (ContextCompat.CheckSelfPermission(_activity, permission) == Permission.Granted)
            return true;

        _permissionTcs = new TaskCompletionSource<bool>();
        ActivityCompat.RequestPermissions(_activity, new[] { permission }, PermissionRequestCode);
        return await _permissionTcs.Task;
    }

    private List<Track> QueryMediaStore()
    {
        var tracks = new List<Track>();
        var resolver = _activity.ContentResolver;
        var contentUri = MediaStore.Audio.Media.ExternalContentUri;
        if (resolver == null || contentUri == null) return tracks;

        var projection = new[]
        {
            MediaStore.Audio.Media.InterfaceConsts.Id,
            MediaStore.Audio.Media.InterfaceConsts.Title,
            MediaStore.Audio.Media.InterfaceConsts.Artist,
            MediaStore.Audio.Media.InterfaceConsts.Album,
            MediaStore.Audio.Media.InterfaceConsts.Duration,
            MediaStore.Audio.Media.InterfaceConsts.Year,
            MediaStore.Audio.Media.InterfaceConsts.Track,
        };
        // MediaStore's "is_music" column is a legacy heuristic that's frequently NULL
        // rather than 1 even for genuine music files (confirmed on this device) - a
        // "WHERE is_music != 0" filter would silently exclude everything, since SQL
        // NULL comparisons are neither true nor false. audio/media is already
        // audio-only by definition, so no filtering is needed here.
        using var cursor = resolver.Query(contentUri, projection, null, null, null);
        if (cursor == null) return tracks;

        var idCol = cursor.GetColumnIndexOrThrow(MediaStore.Audio.Media.InterfaceConsts.Id);
        var titleCol = cursor.GetColumnIndex(MediaStore.Audio.Media.InterfaceConsts.Title);
        var artistCol = cursor.GetColumnIndex(MediaStore.Audio.Media.InterfaceConsts.Artist);
        var albumCol = cursor.GetColumnIndex(MediaStore.Audio.Media.InterfaceConsts.Album);
        var durationCol = cursor.GetColumnIndex(MediaStore.Audio.Media.InterfaceConsts.Duration);
        var yearCol = cursor.GetColumnIndex(MediaStore.Audio.Media.InterfaceConsts.Year);
        var trackCol = cursor.GetColumnIndex(MediaStore.Audio.Media.InterfaceConsts.Track);

        while (cursor.MoveToNext())
        {
            var id = cursor.GetLong(idCol);
            var trackUri = ContentUris.WithAppendedId(contentUri, id);

            tracks.Add(new Track
            {
                Title = titleCol >= 0 ? cursor.GetString(titleCol) : null,
                Artists = artistCol >= 0 ? cursor.GetString(artistCol) : null,
                Album = albumCol >= 0 ? cursor.GetString(albumCol) : null,
                Year = yearCol >= 0 && cursor.GetInt(yearCol) > 0 ? cursor.GetInt(yearCol).ToString() : null,
                TrackNumber = trackCol >= 0 ? (uint)Math.Max(0, cursor.GetInt(trackCol)) : 0,
                Duration = durationCol >= 0 ? TimeSpan.FromMilliseconds(cursor.GetLong(durationCol)) : TimeSpan.Zero,
                Path = trackUri.ToString()
            });
        }

        return tracks;
    }
}
