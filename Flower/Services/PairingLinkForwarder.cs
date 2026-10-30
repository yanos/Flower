using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Flower.Services;

// A pairing link opened while Flower is already running, on Windows and Linux.
// There the platform hands an opened flower:// URL to a fresh process, as a
// command-line argument (see UrlSchemeRegistration in Flower.Desktop), and a
// second window - second library, second player - is the wrong answer to it.
// So the new process offers the link to the one already running and exits,
// and the running one delivers it to PairingLinkInbox as though it had been
// opened there. macOS and the phones never need this: their platforms deliver
// the URL to the running app themselves.
//
// A named pipe, which .NET carries as a Unix-domain socket off Windows, opened
// CurrentUserOnly at both ends: another account on the machine can neither
// offer this user's instance a link nor answer as it. Even a link offered by
// this user's own processes only ever reaches the pairing confirmation, which
// a person still has to accept.
public static class PairingLinkForwarder
{
    // A pairing link is a few hundred characters; anything longer is not one.
    private const int MaxLinkLength = 4096;

    // Per user, without the user name in the clear on a shared /tmp.
    internal static string PipeName { get; } =
        "flower-links-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..16].ToLowerInvariant();

    // True when an instance already running took the link, and this process
    // should exit. False when there is none - this process then becomes the
    // instance, and handles the link itself.
    public static bool TryForward(string link, TimeSpan? timeout = null, string? pipeName = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)(timeout ?? TimeSpan.FromMilliseconds(500)).TotalMilliseconds);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.WriteLine(link);
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Takes forwarded links for the life of the process, one connection at a
    // time. A second instance started without a link (a plain double-click
    // while one runs) finds the pipe taken and simply does not listen.
    public static Task ListenAsync(
        PairingLinkInbox inbox, ILogger? logger = null, string? pipeName = null, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream server;
                try
                {
                    server = new NamedPipeServerStream(
                        pipeName ?? PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                }
                catch (IOException ex)
                {
                    logger?.LogInformation("Not taking pairing links from other launches: {Reason}", ex.Message);
                    return;
                }

                await using (server)
                {
                    try
                    {
                        await server.WaitForConnectionAsync(ct);
                        var link = await ReadLinkAsync(server, ct);
                        if (!inbox.Deliver(link))
                            logger?.LogInformation("Another launch passed something that is not a pairing link; ignored");
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (IOException ex)
                    {
                        // One sender hanging up early is that sender's problem;
                        // the next one still gets a fresh pipe.
                        logger?.LogDebug(ex, "A forwarded pairing link could not be read");
                    }
                }
            }
        }, ct);

    private static async Task<string> ReadLinkAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var buffer = new char[MaxLinkLength];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0)
                break;
            length += read;
        }
        return new string(buffer, 0, length).Trim();
    }
}
