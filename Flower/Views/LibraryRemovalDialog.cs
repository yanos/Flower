using System.Collections.Generic;
using System.Threading.Tasks;

using Avalonia.Controls;

using CommunityToolkit.Mvvm.DependencyInjection;

using Flower.Models;
using Flower.Services;

namespace Flower.Views;

// "Remove from Library" for the desktop's song and album menus (ActionMenus,
// DesktopActionMenu) - the track list, an album tile, and a song row inside
// an expanded album - kept in one place
// for the same reason LocalFileDeletionDialog is: so no one menu can word the
// warning differently, or skip it.
//
// Absent entirely on a head with no LibraryRemovalService registered (the
// browser, which has no peer stack), rather than shown and failing.
internal static class LibraryRemovalDialog
{
    // Also what the desktop's menus count removable songs with
    // (MainViewModel.MenuFactsFor), so an entry is offered exactly when this
    // has something to remove.
    public static LibraryRemovalService? Service => Ioc.Default.GetService<LibraryRemovalService>();

    public static async Task RemoveAsync(TopLevel? topLevel, IReadOnlyList<Track> tracks)
    {
        if (Service is not { } service || topLevel is not Window owner)
            return;
        if (LibraryRemovalPrompt.For(service, tracks) is not { } prompt)
            return;

        bool deleteFiles;
        if (prompt.DeleteFilesLabel is { } label)
        {
            if (await ConfirmDialogWindow.ShowWithOptionAsync(owner, prompt.Title, prompt.Message, "Remove", label) is not { } ticked)
                return;
            deleteFiles = ticked;
        }
        else
        {
            if (!await ConfirmDialogWindow.ShowAsync(owner, prompt.Title, prompt.Message, "Remove"))
                return;
            deleteFiles = false;
        }

        var outcome = await service.RemoveAsync(prompt.Tracks, deleteFiles);
        if (outcome.Error is { } error)
            await ConfirmDialogWindow.ShowMessageAsync(owner, outcome.Removed > 0 ? "Removed, with a problem" : "Couldn't remove", error);
    }
}
