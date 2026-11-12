using System.Linq;

using Flower.Logging;

using Sentry;
using Sentry.Protocol;

using Xunit;

namespace Flower.Tests.Logging;

// What leaves a device in a crash report: the exception's type and the
// methods on its stack, and nothing that names a person, a song, a folder or a
// server.
public class CrashScrubberTests
{
    private const string Home = "/Users/someone";

    private static SentryEvent Crash(string message, params string[] frames)
    {
        var stack = new SentryStackTrace();
        foreach (var frame in frames)
            stack.Frames.Add(new SentryStackFrame { Function = frame, FileName = $"{Home}/dev/Flower/Flower/Playback/Player.cs", AbsolutePath = $"{Home}/dev/Flower/Flower/Playback/Player.cs" });

        return new SentryEvent
        {
            SentryExceptions = [new SentryException { Type = "System.IO.IOException", Value = message, Stacktrace = stack }],
        };
    }

    [Fact]
    public void A_path_keeps_only_its_extension()
    {
        Assert.Equal("Could not open <path>.flac", CrashScrubber.Text("Could not open /Volumes/Music/Secret Artist/01 Song.flac", Home));
        Assert.Equal("Could not open <path>.mp3", CrashScrubber.Text(@"Could not open D:\Music\Secret Artist\01.mp3", null));
    }

    // A folder has no extension to stop at, so its path runs to the end of
    // the line: more hidden than needed rather than half a name shown.
    [Fact]
    public void A_folder_with_spaces_in_its_name_is_hidden_whole()
    {
        Assert.Equal("Folder <path>", CrashScrubber.Text("Folder /Volumes/Music/Secret Artist was not found", Home));
    }

    [Fact]
    public void A_path_in_the_home_folder_says_nothing_of_the_person()
    {
        var text = CrashScrubber.Text($"Access to {Home}/Music/Album/cover.jpg was denied", Home);

        Assert.DoesNotContain("someone", text);
        Assert.Equal("Access to <path>.jpg was denied", text);
    }

    [Fact]
    public void A_quoted_name_is_dropped()
    {
        Assert.Equal("No track named '…' on album '…'", CrashScrubber.Text("No track named 'Secret Song' on album \"Hidden\"", Home));
    }

    // A URL can carry a server's address and, in its query, a stream ticket or
    // the song an online lookup asked about.
    [Fact]
    public void A_url_is_dropped_whole()
    {
        Assert.Equal("GET <url> failed", CrashScrubber.Text("GET https://lrclib.net/api/get?track_name=Secret+Song failed", Home));
    }

    [Fact]
    public void A_message_without_names_is_left_as_it_is()
    {
        const string message = "Object reference not set to an instance of an object.";

        Assert.Equal(message, CrashScrubber.Text(message, Home));
    }

    [Fact]
    public void An_event_keeps_its_exception_type_and_methods_and_loses_names_and_identity()
    {
        var crash = Crash("Could not open /Volumes/Music/Secret/01.flac", "Flower.Playback.Player.Open", "Flower.Decoding.Decoder.Read");
        crash.User = new SentryUser { Id = "install-1234" };
        crash.ServerName = "someones-macbook";
        crash.Contexts.Device.Name = "Someone's MacBook";
        crash.Contexts.Device.BootTime = System.DateTimeOffset.UnixEpoch;

        CrashScrubber.Scrub(crash, Home);

        var exception = Assert.Single(crash.SentryExceptions!);
        Assert.Equal("System.IO.IOException", exception.Type);
        Assert.Equal("Could not open <path>.flac", exception.Value);
        Assert.Equal(["Flower.Playback.Player.Open", "Flower.Decoding.Decoder.Read"], exception.Stacktrace!.Frames.Select(frame => frame.Function));
        Assert.All(exception.Stacktrace.Frames, frame => Assert.Equal("Player.cs", frame.AbsolutePath));
        Assert.Null(crash.User.Id);
        Assert.Null(crash.ServerName);
        Assert.Null(crash.Contexts.Device.Name);
        Assert.Null(crash.Contexts.Device.BootTime);
        Assert.Equal("0.0.0.0", crash.User.IpAddress);
    }

    // The one identity a report carries is the install's random one, which
    // is how Sentry counts how many installs a problem reaches.
    [Fact]
    public void The_installs_random_identity_is_the_only_one_kept()
    {
        var crash = Crash("boom", "Flower.Playback.Player.Open");
        crash.User = new SentryUser { Id = "sdk-installation", Username = "someone", Email = "someone@example.com" };

        CrashScrubber.Scrub(crash, Home, installId: "3f2b9c");

        Assert.Equal("3f2b9c", crash.User.Id);
        Assert.Null(crash.User.Username);
        Assert.Null(crash.User.Email);
    }

    // A media route's stream ticket rides in the query string.
    [Fact]
    public void A_server_request_keeps_its_route_and_loses_its_query_and_headers()
    {
        var crash = Crash("boom", "Flower.Server.Endpoints.MediaEndpoints.Stream");
        crash.Request.Url = "https://192.168.1.20:4534/api/flower/v1/media/42?ticket=secret";
        crash.Request.QueryString = "ticket=secret";
        crash.Request.Headers["Authorization"] = "Flower signature";

        CrashScrubber.Scrub(crash, Home);

        Assert.Equal("/api/flower/v1/media/42", crash.Request.Url);
        Assert.Null(crash.Request.QueryString);
        Assert.Empty(crash.Request.Headers);
    }

    [Fact]
    public void A_native_crashs_frames_are_scrubbed_too()
    {
        var crash = new SentryEvent { Message = new SentryMessage { Formatted = "Native crash in libminiaudio.dylib!ma_device_read" } };
        crash.SetExtra("frames", $"  at {Home}/Applications/Flower.app/libflowerbridge.dylib!render");

        CrashScrubber.Scrub(crash, Home);

        Assert.Equal("  at <path>.dylib!render", crash.Extra["frames"]);
    }
}
