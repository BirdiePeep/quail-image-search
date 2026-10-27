using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using QuailImageSearch.Services;

namespace QuailImageSearch.Views;

/// <summary>User-facing file commands shared by the results grid and the preview window.</summary>
public static class FileCommands
{
    /// <summary>
    /// Asks once, then moves the files to the Recycle Bin / Trash. Returns what happened and a status
    /// message, or null if the user cancelled.
    /// </summary>
    public static async Task<(FileActions.TrashResult Result, string Message)?> DeleteAsync(Window owner, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return null;
        var what = paths.Count == 1 ? $"\"{Path.GetFileName(paths[0])}\"" : $"these {paths.Count} images";
        if (!await ConfirmDialog.ShowAsync(owner, "Delete", $"Move {what} to the {FileActions.TrashName}?", "Delete"))
            return null;

        var result = await Task.Run(() => FileActions.MoveToTrash(paths));
        var message = result.Failed.Count == 0
            ? $"Moved {result.Moved.Count} file(s) to the {FileActions.TrashName}."
            : $"Moved {result.Moved.Count} file(s) to the {FileActions.TrashName}; {result.Failed.Count} couldn't be deleted: " +
              string.Join("; ", result.Failed.Take(3).Select(f => $"{Path.GetFileName(f.Path)} ({f.Error})")) +
              (result.Failed.Count > 3 ? $"; and {result.Failed.Count - 3} more" : "");
        return (result, message);
    }

    public static async Task<string> CopyAsync(TopLevel topLevel, IReadOnlyList<string> paths)
    {
        await FileActions.CopyToClipboardAsync(topLevel, paths);
        return $"Copied {paths.Count} file(s) to the clipboard.";
    }
}
