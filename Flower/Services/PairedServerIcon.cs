using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Media.Imaging;

using Microsoft.Extensions.Logging;

using Flower.Persistence;

namespace Flower.Services;

// The paired server's icon (ServerIcon) as this device holds it: the emoji
// it shows as, or the picture - fetched once per token it names on /info and
// kept as a file beside settings.json, so the sidebar row has it from launch
// and while the server cannot be reached, as it has the server's alias.
//
// Another server's row needs none of this: its emoji comes with its /info
// answer (DiscoveredDevice.IconEmoji), and its picture is not this device's to
// fetch.
public sealed class PairedServerIcon
{
    private readonly AppSettings _settings;
    private readonly AppSettingsStore? _store;
    private readonly IPeerCredentials _credentials;
    private readonly HttpClient _http;
    private readonly ILogger<PairedServerIcon> _logger;
    private readonly string _path;
    private readonly SemaphoreSlim _observing = new(1, 1);

    public PairedServerIcon(
        AppSettings settings, AppSettingsStore? store, IPeerCredentials credentials, HttpClient http,
        ILogger<PairedServerIcon> logger, string? path = null)
    {
        _settings = settings;
        _store = store;
        _credentials = credentials;
        _http = http;
        _logger = logger;
        _path = path ?? Path.Combine(AppDataDirectory.Path, "paired-server-icon");

        if (_settings.PairedServerIconToken.Length > 0)
            Image = Decode(ReadFile());
    }

    public string Emoji => _settings.PairedServerIconEmoji;
    public Bitmap? Image { get; private set; }

    // Raised off the UI thread, whenever Emoji or Image changed.
    public event EventHandler? Changed;

    // Called with every /info answer that changed something. Anything but
    // the paired server is not this class's business.
    public async Task ObserveAsync(DiscoveredDevice device)
    {
        if (string.IsNullOrEmpty(device.Fingerprint) || device.Fingerprint != _settings.PairedServerFingerprint)
            return;

        await _observing.WaitAsync();
        try
        {
            var changed = false;
            if (device.IconEmoji != _settings.PairedServerIconEmoji)
            {
                _settings.PairedServerIconEmoji = device.IconEmoji;
                changed = true;
            }

            if (device.IconToken != _settings.PairedServerIconToken)
            {
                var image = device.IconToken.Length > 0 ? await FetchAsync(device) : null;
                // Not fetched this time is tried again on the next answer that
                // changes anything; the old picture stays meanwhile.
                if (device.IconToken.Length == 0 || image != null)
                {
                    _settings.PairedServerIconToken = device.IconToken;
                    Image = image;
                    if (image == null)
                        DeleteFile();
                    changed = true;
                }
            }

            if (changed)
            {
                await (_store?.SaveAsync(_settings) ?? Task.CompletedTask);
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _observing.Release();
        }
    }

    // After the pairing changed. PeerSyncCoordinator clears the icon's
    // settings along with the rest of what it remembered about a server it
    // leaves, and the picture goes with them.
    public void FollowPairing()
    {
        if (_settings.PairedServerIconToken.Length == 0 && Image != null)
        {
            Image = null;
            DeleteFile();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<Bitmap?> FetchAsync(DiscoveredDevice device)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, device.Url(ServerIcon.ImagePath));
            await request.AddPeerCredentialsAsync(_credentials);
            using var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (Decode(bytes) is not { } image)
                return null;

            await File.WriteAllBytesAsync(_path, bytes);
            return image;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not fetch the icon of {Alias}", device.Alias);
            return null;
        }
    }

    private byte[]? ReadFile()
    {
        try
        {
            return File.Exists(_path) ? File.ReadAllBytes(_path) : null;
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not read the paired server's icon at {Path}", _path);
            return null;
        }
    }

    private void DeleteFile()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not delete the paired server's icon at {Path}", _path);
        }
    }

    private Bitmap? Decode(byte[]? bytes)
    {
        var image = ServerIconPicture.Decode(bytes);
        if (image == null && bytes is { Length: > 0 })
            _logger.LogDebug("The paired server's icon is not a picture this device can draw");
        return image;
    }
}
