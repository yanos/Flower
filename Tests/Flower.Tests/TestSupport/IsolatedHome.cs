using System;
using System.IO;

namespace Flower.Tests.TestSupport;

// PinnedDataDirectory, with HOME redirected into the same temp directory as
// well - for what resolves off the user's home rather than AppDataDirectory,
// such as ITunesPlayCountImporter.ResolveLibraryXmlPath's Music-folder lookup.
//
// HOME alone used to be the only guard the store tests had, and it was not
// enough on GitHub's Ubuntu runners: AppDataDirectory's non-macOS branch
// resolves via SpecialFolder.LocalApplicationData, which reads XDG_DATA_HOME
// before falling back to $HOME/.local/share, and that image pins XDG_DATA_HOME
// to a fixed path - so every test silently shared one real directory. Pinning
// PlatformDataDirectory is what closes that; HOME is only for the lookups that
// go around it.
//
// Same collection rule as its base: [Collection("PlatformDataDirectory")].
public abstract class IsolatedHome : PinnedDataDirectory
{
    private readonly string? _originalHome;

    protected string Home => DataDirectory;

    protected IsolatedHome()
    {
        _originalHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", DataDirectory);
    }

    public override void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _originalHome);
        base.Dispose();
    }
}
