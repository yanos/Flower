using Microsoft.Extensions.Options;

using Flower.Server.Configuration;
using Flower.Services;

namespace Flower.Server.Services;

// The picture an owner chose to show this server as (ServerIcon), kept as one
// file in the data directory rather than as a setting: it is bytes, and
// flower-server.json is for an operator to read and edit by hand. Read once and
// then held, since /info asks for its token on every poll from every device.
public sealed class ServerIconStore(IOptions<FlowerServerOptions> options, ILogger<ServerIconStore> logger)
{
    public const string FileName = "server-icon";

    public sealed record Picture(byte[] Bytes, string MimeType, string Token);

    private readonly string _path = Path.Combine(options.Value.DataDirectory, FileName);
    private readonly object _lock = new();
    private Picture? _current;
    private bool _loaded;

    public Picture? Current
    {
        get
        {
            lock (_lock)
            {
                if (!_loaded)
                {
                    _current = Read();
                    _loaded = true;
                }
                return _current;
            }
        }
    }

    // Written beside the old one and moved over it, so a reader never finds
    // half a picture. bytes is an image already: the caller sniffed it.
    public async Task SetAsync(byte[] bytes, string mimeType, CancellationToken ct = default)
    {
        var staging = _path + ".new";
        await File.WriteAllBytesAsync(staging, bytes, ct);
        lock (_lock)
        {
            File.Move(staging, _path, overwrite: true);
            _current = new Picture(bytes, mimeType, ServerIcon.TokenOf(bytes));
            _loaded = true;
        }
    }

    public void Remove()
    {
        lock (_lock)
        {
            File.Delete(_path);
            _current = null;
            _loaded = true;
        }
    }

    private Picture? Read()
    {
        try
        {
            if (!File.Exists(_path))
                return null;

            var bytes = File.ReadAllBytes(_path);
            return LocalAlbumArtReader.MimeTypeForBytes(bytes) is { } mimeType
                ? new Picture(bytes, mimeType, ServerIcon.TokenOf(bytes))
                : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the server's icon at {Path}", _path);
            return null;
        }
    }
}
