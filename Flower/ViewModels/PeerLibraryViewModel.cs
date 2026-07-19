using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;

namespace Flower.ViewModels;

// Live, non-persisted browse state for whichever DiscoveredDevice is
// currently selected in the sidebar (see MainViewModel.SelectedDevice) -
// deliberately separate from LibrarySyncService's bulk-merge path (see
// SyncRolePolicy): looking at a peer's catalog here never touches
// Library/LibraryStore, and unlike bulk sync this works for ANY trusted peer
// regardless of Client/Server role or pairing (SYNC-PLAN.md's browsing
// feature). Re-fetched fresh every time a Device sidebar item is selected -
// nothing here is cached across selections.
public sealed class PeerLibraryViewModel : ViewModelBase
{
    private readonly DeviceIdentity _deviceIdentity;
    private readonly AppSettings _appSettings;
    private readonly PlaylistControlViewModel _playlistControlViewModel;

    private OpenSubsonicClient? _client;
    private DiscoveredDevice? _peer;
    private int _requestId; // guards against a stale LoadAsync/SelectAlbumAsync winning a race

    public PeerLibraryViewModel(DeviceIdentity deviceIdentity, AppSettings appSettings, PlaylistControlViewModel playlistControlViewModel)
    {
        _deviceIdentity = deviceIdentity;
        _appSettings = appSettings;
        _playlistControlViewModel = playlistControlViewModel;
    }

    public ObservableCollection<AlbumID3> Albums { get; } = new();
    public ObservableCollection<Child> AlbumSongs { get; } = new();

    private AlbumID3? _selectedAlbum;
    public AlbumID3? SelectedAlbum
    {
        get => _selectedAlbum;
        private set => SetProperty(ref _selectedAlbum, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public async Task LoadAsync(DiscoveredDevice peer)
    {
        var requestId = ++_requestId;
        _peer = peer;
        SelectedAlbum = null;
        AlbumSongs.Clear();
        Albums.Clear();
        ErrorMessage = null;
        IsLoading = true;
        try
        {
            _client = PeerOpenSubsonicClientFactory.Create(peer, _deviceIdentity, _appSettings);
            var albums = await _client.GetAlbumList2Async();
            if (requestId != _requestId)
                return; // A newer selection already replaced this one.
            foreach (var album in albums)
                Albums.Add(album);
        }
        catch (Exception ex)
        {
            if (requestId == _requestId)
                ErrorMessage = $"Could not reach {peer.Alias}: {ex.Message}";
        }
        finally
        {
            if (requestId == _requestId)
                IsLoading = false;
        }
    }

    public async Task SelectAlbumAsync(AlbumID3 album)
    {
        if (_client == null)
            return;

        var requestId = ++_requestId;
        SelectedAlbum = album;
        AlbumSongs.Clear();
        ErrorMessage = null;
        IsLoading = true;
        try
        {
            var full = await _client.GetAlbumAsync(album.Id);
            if (requestId != _requestId)
                return;
            foreach (var song in full.Song ?? new List<Child>())
                AlbumSongs.Add(song);
        }
        catch (Exception ex)
        {
            if (requestId == _requestId)
                ErrorMessage = $"Could not load {album.Name}: {ex.Message}";
        }
        finally
        {
            if (requestId == _requestId)
                IsLoading = false;
        }
    }

    // Queues the rest of the currently-viewed album as a transient "Now
    // Playing" playlist (same pattern as MainViewModel.PlayAlbum), so Next/
    // Previous naturally walk through it - none of these Tracks are added to
    // Library/persisted anywhere; Path is a live http://peer/rest/stream URL,
    // which VlcAudioManager.Play already streams directly (it branches on any
    // Path containing "://", originally for Android's content:// URIs).
    public void PlaySong(Child song)
    {
        if (_client == null || SelectedAlbum == null)
            return;

        var songs = AlbumSongs.ToList();
        var tracks = songs.Select(ToTransientTrack).ToList();
        var index = songs.FindIndex(s => s.Id == song.Id);
        if (index < 0)
            return;

        _playlistControlViewModel.SetCurrentPlaylist(new Playlist($"{_peer?.Alias}: {SelectedAlbum.Name}", tracks));
        _playlistControlViewModel.Play(tracks[index]);
    }

    private Track ToTransientTrack(Child song) => new()
    {
        Title = song.Title,
        Artists = song.Artist,
        Album = song.Album,
        TrackNumber = (uint)(song.Track ?? 0),
        Year = song.Year?.ToString(),
        Genre = song.Genre,
        Duration = TimeSpan.FromSeconds(song.Duration ?? 0),
        Path = _client!.GetStreamUrl(song.Id),
    };
}
