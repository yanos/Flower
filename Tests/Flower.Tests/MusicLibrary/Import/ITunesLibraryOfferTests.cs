using System;
using System.IO;
using System.Linq;

using Avalonia.Interactivity;

using Flower.Persistence;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.MusicLibrary.Import;

// Offer decisions and persistence, without opening the dialog.
[Collection("PlatformDataDirectory")]
public class ITunesLibraryOfferTests : PinnedDataDirectory
{
    private const string Folder = "/Users/test/Music/Music/Media.localized/";

    [Fact]
    public void Arming_turns_both_imports_off_until_there_is_an_answer()
    {
        // Both default to on, which is what makes this worth doing.
        var settings = new AppSettings();
        Assert.True(settings.SyncPlayCountFromITunes);
        Assert.True(settings.SyncDateAddedFromITunes);

        ITunesLibraryOffer.Arm(settings);

        Assert.True(settings.ITunesLibraryOfferPending);
        Assert.False(settings.SyncPlayCountFromITunes);
        Assert.False(settings.SyncDateAddedFromITunes);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Yes_takes_the_folder_and_sets_each_import_to_what_was_ticked(bool playCount, bool dateAdded)
    {
        var settings = new AppSettings();
        ITunesLibraryOffer.Arm(settings);

        ITunesLibraryOffer.Apply(settings, Folder, new ITunesLibraryOfferAnswer(playCount, dateAdded));

        Assert.False(settings.ITunesLibraryOfferPending);
        Assert.Equal([Folder], settings.LibraryPaths);
        Assert.Equal(playCount, settings.SyncPlayCountFromITunes);
        Assert.Equal(dateAdded, settings.SyncDateAddedFromITunes);
    }

    [Fact]
    public void No_takes_nothing_and_is_not_asked_again()
    {
        var settings = new AppSettings();
        ITunesLibraryOffer.Arm(settings);

        ITunesLibraryOffer.Apply(settings, Folder, answer: null);

        Assert.False(settings.ITunesLibraryOfferPending);
        Assert.False(settings.SyncPlayCountFromITunes);
        Assert.False(settings.SyncDateAddedFromITunes);
        Assert.Empty(settings.LibraryPaths);
    }

    [Fact]
    public void Yes_does_not_list_a_folder_twice()
    {
        var settings = new AppSettings { LibraryPaths = [Folder.ToUpperInvariant()] };
        ITunesLibraryOffer.Arm(settings);

        ITunesLibraryOffer.Apply(settings, Folder, new ITunesLibraryOfferAnswer(true, true));

        Assert.Single(settings.LibraryPaths);
    }

    // What decides whether a first run may still start from ~/Music: only when
    // that would not scan the library being offered. A sibling whose name
    // merely starts the same way is not inside it.
    [Theory]
    [InlineData("/Users/test/Music/Music/Media.localized/", "/Users/test/Music", true)]
    [InlineData("/Users/test/Music/Music/Media.localized", "/Users/test/Music/", true)]
    [InlineData("/Users/test/Music", "/Users/test/Music", true)]
    [InlineData("/Volumes/Drive/Media", "/Users/test/Music", false)]
    [InlineData("/Users/test/Music Archive/Media", "/Users/test/Music", false)]
    [InlineData(null, "/Users/test/Music", false)]
    public void A_seed_covers_an_offered_folder_only_when_the_folder_is_inside_it(string? offered, string seed, bool covered)
    {
        Assert.Equal(covered, ITunesLibraryOffer.IsCoveredBy(offered, seed));
    }

    // Through the real store, on whatever machine this runs on: one with a
    // Music.app library owes the question and has taken none of it, one without
    // owes nothing. The pinned directory is empty, so this is a first run.
    [Fact]
    public void A_first_run_never_takes_a_Music_app_library_before_being_told_to()
    {
        var found = Flower.Importer.Importer.TryResolveAppleMusicFolder();

        var settings = new AppSettingsStore().Load();

        Assert.Equal(found != null, settings.ITunesLibraryOfferPending);
        if (found == null)
            return;

        Assert.False(settings.SyncPlayCountFromITunes);
        Assert.False(settings.SyncDateAddedFromITunes);
        Assert.DoesNotContain(settings.LibraryPaths, path => ITunesLibraryOffer.IsCoveredBy(found, path));

        // And a relaunch with the question still unanswered is the same: it is
        // not a first run any more, but it has not been told yes either.
        var relaunched = new AppSettingsStore().Load();

        Assert.True(relaunched.ITunesLibraryOfferPending);
        Assert.DoesNotContain(relaunched.LibraryPaths, path => ITunesLibraryOffer.IsCoveredBy(found, path));
    }

    // The folder used to come back on every load for as long as a master switch
    // was on. There is no such switch in the app now, so a no - or a folder
    // removed by hand afterwards - has to stay that way across a relaunch.
    [Fact]
    public void A_declined_library_is_not_taken_on_a_later_launch()
    {
        var store = new AppSettingsStore();
        var settings = store.Load();
        var found = Flower.Importer.Importer.TryResolveAppleMusicFolder();
        if (found != null)
            ITunesLibraryOffer.Apply(settings, found, answer: null);
        settings.LibraryPaths = [];
        store.Save(settings);

        var relaunched = new AppSettingsStore().Load();

        Assert.False(relaunched.ITunesLibraryOfferPending);
        Assert.Empty(relaunched.LibraryPaths);
    }

    [Fact]
    public void The_question_survives_a_round_trip_through_the_store()
    {
        var store = new AppSettingsStore();
        var settings = store.Load();
        ITunesLibraryOffer.Arm(settings);
        store.Save(settings);

        Assert.True(new AppSettingsStore().Load().ITunesLibraryOfferPending);
    }
}
