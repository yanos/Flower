using System.Diagnostics;

namespace Flower.Tests.Playback;

// A dbus-daemon of the test's own, on a socket in a temp folder, so an MPRIS
// test never reaches the desktop's session bus (and its media widget). Null
// where there is no dbus-daemon to start: Linux has one wherever D-Bus is
// installed, a Mac only with MacPorts' or Homebrew's.
public sealed class PrivateSessionBus : IDisposable
{
    private readonly Process _daemon;
    private readonly string _directory;

    public string Address { get; }

    private PrivateSessionBus(Process daemon, string directory, string address)
    {
        _daemon = daemon;
        _directory = directory;
        Address = address;
    }

    public static string? FindDaemon()
    {
        if (OperatingSystem.IsIOS() || OperatingSystem.IsAndroid() || OperatingSystem.IsWindows())
            return null;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator)
            .Concat(["/usr/bin", "/opt/local/bin", "/opt/homebrew/bin", "/usr/local/bin"])
            .Select(dir => Path.Combine(dir, "dbus-daemon"))
            .FirstOrDefault(File.Exists);
    }

    public static PrivateSessionBus Start(string daemonPath)
    {
        var directory = Directory.CreateTempSubdirectory("flower-dbus").FullName;
        var socket = Path.Combine(directory, "bus");
        var config = Path.Combine(directory, "bus.conf");
        File.WriteAllText(config, $"""
            <!DOCTYPE busconfig PUBLIC "-//freedesktop//DTD D-Bus Bus Configuration 1.0//EN"
              "http://www.freedesktop.org/standards/dbus/1.0/busconfig.dtd">
            <busconfig>
              <type>session</type>
              <listen>unix:path={socket}</listen>
              <auth>EXTERNAL</auth>
              <policy context="default">
                <allow send_destination="*" eavesdrop="true"/>
                <allow eavesdrop="true"/>
                <allow own="*"/>
              </policy>
            </busconfig>
            """);

        var daemon = Process.Start(new ProcessStartInfo(daemonPath)
        {
            ArgumentList = { $"--config-file={config}", "--nofork", "--print-address=1" },
            RedirectStandardOutput = true,
            UseShellExecute = false
        })!;

        // The daemon prints its address once it is listening.
        var address = daemon.StandardOutput.ReadLine();
        if (string.IsNullOrEmpty(address))
        {
            daemon.Kill();
            throw new InvalidOperationException("dbus-daemon exited without printing an address");
        }
        return new PrivateSessionBus(daemon, directory, address.Trim());
    }

    public void Dispose()
    {
        try
        {
            _daemon.Kill();
            _daemon.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        _daemon.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
