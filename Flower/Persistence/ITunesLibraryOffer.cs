using System;
using System.IO;
using System.Linq;

namespace Flower.Persistence;

// What the person said to the first-run question. Declining is a null answer
// rather than a third field: there is nothing to remember about a "no" beyond
// that it was given.
public sealed record ITunesLibraryOfferAnswer(bool SyncPlayCount, bool SyncDateAdded);

// The first launch on a Mac with a Music.app library asks before taking it,
// instead of adopting the folder and switching both imports on unannounced.
// These are the rules of that question, as functions of the settings alone, so
// neither end of it - AppSettingsStore.Load, which decides a question is owed,
// and ITunesLibraryOfferWindow's caller in App.axaml.cs, which records the
// answer - has to restate them.
//
// The question is owed for as long as AppSettings.ITunesLibraryOfferPending is
// set, which is persisted rather than inferred from "there is no settings.json
// yet": quitting with the window still up writes that file (the main window's
// geometry is saved on close), and a question that was never answered has to
// survive that to be asked again.
public static class ITunesLibraryOffer
{
    // Called once, on a first run that found a Music.app media folder. Turns
    // both imports off until there is an answer - they default to on, and an
    // import is an AppleScript export out of Music.app, which is exactly the
    // thing being asked about.
    public static void Arm(AppSettings settings)
    {
        settings.SyncPlayCountFromITunes = false;
        settings.SyncDateAddedFromITunes = false;
        settings.ITunesLibraryOfferPending = true;
    }

    // Whether seeding a first run with `seed` (the platform's music folder)
    // would scan the offered folder anyway. It does on a default Mac -
    // Music.app keeps its media under ~/Music - and a seed that already
    // contains what is being offered makes the question decorative: the songs
    // are in the library whatever the answer is.
    public static bool IsCoveredBy(string? offeredFolder, string seed)
    {
        if (offeredFolder is null)
            return false;

        var parent = WithTrailingSeparator(seed);
        return WithTrailingSeparator(offeredFolder).StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    // Records the answer. Yes takes the folder and sets the two imports to
    // what was ticked; no takes nothing and leaves both imports off, where Arm
    // put them. Either way the question is not asked again, and nothing about
    // the answer is final: Settings > Library has the same two switches, usable
    // whenever a Music.app library exists, and Add Folder for the folder.
    //
    // Only the settings change here. The caller holds the startup rescan until
    // this has run, so the folder is scanned and the imports applied by the
    // same pass every later launch uses, rather than by a second one racing it.
    public static void Apply(AppSettings settings, string folder, ITunesLibraryOfferAnswer? answer)
    {
        settings.ITunesLibraryOfferPending = false;
        if (answer is null)
            return;

        settings.SyncPlayCountFromITunes = answer.SyncPlayCount;
        settings.SyncDateAddedFromITunes = answer.SyncDateAdded;
        if (!settings.LibraryPaths.Contains(folder, StringComparer.OrdinalIgnoreCase))
            settings.LibraryPaths.Add(folder);
    }

    private static string WithTrailingSeparator(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
}
