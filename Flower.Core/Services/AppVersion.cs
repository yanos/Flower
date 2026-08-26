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

    private static string StripBuildMetadata(string version)
    {
        var plusIndex = version.IndexOf('+');
        return plusIndex >= 0 ? version[..plusIndex] : version;
    }
}
