using System;
using System.Reflection;

namespace Flower.Services;

// The one place that answers "which build is this?".
//
// There are two version attributes and they disagree on purpose. MinVer pins
// AssemblyVersion to <major>.0.0.0 so that assembly binding stays stable
// across a patch release - which means it reads 0.0.0.0 for the whole of 0.x
// and is useless for identifying a build. The real, git-derived version lives
// in AssemblyInformationalVersion. Reading the wrong one is an easy mistake to
// make twice, which is why this is a helper rather than three call sites.
public static class AppVersion
{
    // Every assembly in this repo carries the same MinVer version (see
    // Directory.Build.props), so reading the one this code is compiled into
    // is both correct and never null - unlike Assembly.GetEntryAssembly(),
    // which can return null depending on how the process was started.
    private static readonly Assembly Source = typeof(AppVersion).Assembly;

    // The full version including MinVer's "+<commit-sha>" build metadata, for
    // a log line or a bug report - the sha is the part that pins down exactly
    // which commit a pre-release build came from.
    public static string Full { get; } =
        Source.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Source.GetName().Version?.ToString()
        ?? "unknown";

    // The same version without the build metadata, for anywhere a person
    // reads it: an about box, a settings screen. A 40-character sha is noise
    // to someone who just wants to know they are on 0.1.0.
    public static string Display { get; } = StripBuildMetadata(Full);

    // When this build happened, as the build stamped it into the executable
    // that was started (see _StampFlowerBuildDate in Directory.Build.targets),
    // or null when nothing loaded carries a stamp - a library loaded by a test
    // host, say.
    //
    // Found among the loaded assemblies rather than through
    // Assembly.GetEntryAssembly(), for the reason Source gives: that can be
    // null depending on how the process was started, and a phone or a browser
    // is exactly where it tends to be.
    public static DateTimeOffset? BuildDate => Stamp?.Date;

    // Which executable carries that stamp - Flower.MacOS, Flower.iOS,
    // Flower.Server - i.e. which entry point this process is.
    public static string EntryPoint => Stamp?.Assembly ?? "unknown";

    private static readonly (string Assembly, DateTimeOffset Date)? Stamp = FindStamp();

    private static (string, DateTimeOffset)? FindStamp()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var metadata in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (metadata.Key == "BuildDate"
                    && DateTimeOffset.TryParse(metadata.Value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date))
                {
                    return (assembly.GetName().Name ?? "unknown", date);
                }
            }
        }

        return null;
    }

    // The one startup line every entry point logs: which executable, which
    // build, when it was built, on what.
    public static string StartupDescription =>
        $"{EntryPoint} {Full}, built {(BuildDate is { } date ? date.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", System.Globalization.CultureInfo.InvariantCulture) : "at an unknown time")}, "
        + $"on {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";

    // Also for another assembly's version - the decoder's, when it comes from
    // the FFAudio.NET package (see DecoderVersion).
    public static string StripBuildMetadata(string version)
    {
        var plusIndex = version.IndexOf('+');
        return plusIndex >= 0 ? version[..plusIndex] : version;
    }
}
