using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;

using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests.Network;

// The paired server's icon as this device keeps it: fetched once per token
// its /info names, kept on disk for the next launch, and left alone for any
// other server.
public sealed class PairedServerIconTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("flower-server-icon-tests").FullName;
    private string IconPath => Path.Combine(_root, "paired-server-icon");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class PictureServer(byte[]? picture) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            Assert.Equal(ServerIcon.ImagePath, request.RequestUri!.AbsolutePath);
            return Task.FromResult(picture == null
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(picture) });
        }
    }

    private PairedServerIcon Make(AppSettings settings, HttpMessageHandler handler) =>
        new(settings, store: null, new StaticPeerCredentials("X-Test", "1"), new HttpClient(handler),
            NullLogger<PairedServerIcon>.Instance, IconPath);

    private static DiscoveredDevice Server(string fingerprint, string emoji = "", string token = "") => new()
    {
        InstanceName = fingerprint,
        BaseUri = new Uri("http://192.168.1.40:4533/"),
        Fingerprint = fingerprint,
        IconEmoji = emoji,
        IconToken = token,
    };

    [AvaloniaFact]
    public async Task A_new_picture_is_fetched_once_and_is_there_at_the_next_launch()
    {
        var settings = new AppSettings { PairedServerFingerprint = "paired" };
        var server = new PictureServer(SyntheticPng.Build(32, 32));
        var icon = Make(settings, server);

        await icon.ObserveAsync(Server("paired", token: "t1"));
        await icon.ObserveAsync(Server("paired", token: "t1"));

        Assert.NotNull(icon.Image);
        Assert.Equal("t1", settings.PairedServerIconToken);
        Assert.Equal(1, server.Requests);
        Assert.NotNull(Make(settings, new PictureServer(null)).Image);
    }

    [AvaloniaFact]
    public async Task Another_servers_icon_is_not_this_devices_to_keep()
    {
        var settings = new AppSettings { PairedServerFingerprint = "paired" };
        var server = new PictureServer(SyntheticPng.Build(32, 32));
        var icon = Make(settings, server);

        await icon.ObserveAsync(Server("someone-else", emoji: "🎧", token: "t1"));

        Assert.Equal("", icon.Emoji);
        Assert.Null(icon.Image);
        Assert.Equal(0, server.Requests);
    }

    // A server out of reach for a moment must not cost the picture it had:
    // the token is only taken once the picture it names is here.
    [AvaloniaFact]
    public async Task A_picture_that_could_not_be_fetched_leaves_the_old_one_and_is_asked_for_again()
    {
        var settings = new AppSettings { PairedServerFingerprint = "paired" };
        var icon = Make(settings, new PictureServer(null));

        await icon.ObserveAsync(Server("paired", token: "t1"));

        Assert.Null(icon.Image);
        Assert.Equal("", settings.PairedServerIconToken);
    }
}
