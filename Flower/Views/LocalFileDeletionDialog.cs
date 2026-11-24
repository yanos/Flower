using System.Collections.Generic;
using System.Threading.Tasks;

using Avalonia.Controls;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels;

namespace Flower.Views;

// The track list and both album-grid menus all delete the same kind of local
// file. Keeping the prompt here prevents one menu from silently treating an
// imported file as disposable cache while another warns about it.
internal static class LocalFileDeletionDialog
{
    public static async Task DeleteAsync(TopLevel? topLevel, MainViewModel viewModel, IReadOnlyList<Track> tracks)
    {
        var localFiles = LocalFileDeletion.LocalFiles(tracks);
        if (localFiles.Count == 0)
            return;

        // A download its server still has is disposable cache. Anything else
        // is potentially the user's only copy, so it takes an explicit choice.
        var paired = viewModel.PairedServerFingerprint;
        if (LocalFileDeletion.RequiresWarning(localFiles, paired))
        {
            if (topLevel is not Window owner)
                return;

            var title = localFiles.Count == 1
                ? $"Delete \"{localFiles[0].Title ?? "Local File"}\"?"
                : $"Delete {localFiles.Count} Local Files?";
            var message = LocalFileDeletion.WarningMessage(localFiles, paired, LocalFileDeletion.Trash);
            var confirmed = await ConfirmDialogWindow.ShowAsync(owner, title, message, "Delete");
            if (!confirmed)
                return;
        }

        var failed = viewModel.DeleteLocalFiles(localFiles);

        if (failed > 0 && topLevel is Window window)
        {
            await ConfirmDialogWindow.ShowMessageAsync(window, "Couldn't delete",
                failed == 1
                    ? "One file could not be deleted, so its song was left as it was. Check that the file isn't locked or read-only."
                    : $"{failed} files could not be deleted, so their songs were left as they were. Check that the files aren't locked or read-only.");
        }
    }
}
