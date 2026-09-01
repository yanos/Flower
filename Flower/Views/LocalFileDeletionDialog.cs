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
    public static void UpdateMenuItem(MenuItem item, IReadOnlyList<Track> tracks)
    {
        var localFiles = LocalFileDeletion.LocalFiles(tracks);
        item.IsVisible = localFiles.Count > 0;
        item.Header = localFiles.Count == 1 ? "Delete Local File" : $"Delete {localFiles.Count} Local Files";
    }

    public static async Task DeleteAsync(TopLevel? topLevel, MainViewModel viewModel, IReadOnlyList<Track> tracks)
    {
        var localFiles = LocalFileDeletion.LocalFiles(tracks);
        if (localFiles.Count == 0)
            return;

        // A downloaded file is disposable cache: it can be fetched from its
        // server again. A scanned/imported file is potentially the user's only
        // copy, so require an explicit choice before touching it.
        if (LocalFileDeletion.RequiresWarning(localFiles))
        {
            if (topLevel is not Window owner)
                return;

            var title = localFiles.Count == 1
                ? $"Delete \"{localFiles[0].Title ?? "Local File"}\"?"
                : $"Delete {localFiles.Count} Local Files?";
            var message = localFiles.Count == 1
                ? "This file was not downloaded from a server. Deleting it will permanently remove this local copy."
                : "One or more of these files were not downloaded from a server. Deleting them will permanently remove those local copies.";
            var confirmed = await ConfirmDialogWindow.ShowAsync(owner, title, message, "Delete");
            if (!confirmed)
                return;
        }

        foreach (var track in localFiles)
            await viewModel.DeleteDownloadedFileAsync(track);
    }
}
