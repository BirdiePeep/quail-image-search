using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace QuailImageSearch.Services;

/// <summary>OS-facing actions on result files: open, reveal, clipboard, drag-out.</summary>
public static class FileActions
{
    public static async Task OpenAsync(TopLevel topLevel, IEnumerable<string> paths)
    {
        foreach (var path in paths.Take(20)) // guard against opening hundreds of windows
            await topLevel.Launcher.LaunchFileInfoAsync(new FileInfo(path));
    }

    /// <summary>Opens the folder of each file with the files selected (one window per folder, at most 5).</summary>
    public static async Task ShowInFolderAsync(TopLevel topLevel, IReadOnlyList<string> paths)
    {
        foreach (var group in paths.Select(Path.GetFullPath).GroupBy(p => Path.GetDirectoryName(p) ?? "", StringComparer.OrdinalIgnoreCase).Take(5))
        {
            var files = group.ToList();
            if (OperatingSystem.IsWindows())
            {
                if (!SelectInExplorer(group.Key, files))
                {
                    // Fallback. The argument must be exactly /select,"path": passing it through ArgumentList
                    // quotes the whole thing when the path has spaces, and Explorer then opens Documents.
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{files[0]}\"") { UseShellExecute = false });
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                psi.ArgumentList.Add("-R");
                foreach (var f in files) psi.ArgumentList.Add(f);
                Process.Start(psi);
            }
            else if (group.Key.Length > 0)
            {
                // No portable "select file" on Linux desktops; open the containing folder instead.
                await topLevel.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(group.Key));
            }
        }
    }

    /// <summary>Opens <paramref name="folder"/> in Explorer with <paramref name="files"/> selected (shell API). False on failure.</summary>
    private static bool SelectInExplorer(string folder, List<string> files)
    {
        var folderId = IntPtr.Zero;
        var items = new List<IntPtr>();
        try
        {
            if (SHParseDisplayName(folder, IntPtr.Zero, out folderId, 0, out _) != 0) return false;
            foreach (var f in files)
                if (SHParseDisplayName(f, IntPtr.Zero, out var item, 0, out _) == 0)
                    items.Add(item);
            return items.Count > 0 && SHOpenFolderAndSelectItems(folderId, (uint)items.Count, items.ToArray(), 0) == 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            foreach (var item in items) Marshal.FreeCoTaskMem(item);
            if (folderId != IntPtr.Zero) Marshal.FreeCoTaskMem(folderId);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr[] items, uint flags);

    public static async Task<List<IStorageFile>> ToStorageFilesAsync(TopLevel topLevel, IEnumerable<string> paths)
    {
        var files = new List<IStorageFile>();
        foreach (var path in paths)
            if (await topLevel.StorageProvider.TryGetFileFromPathAsync(path) is { } file)
                files.Add(file);
        return files;
    }

    /// <summary>
    /// Puts the files on the clipboard in two forms: as files (paste into Explorer/Finder, or attach
    /// in chat apps) and as a picture (paste into Paint, Word, image editors, …). The clipboard holds
    /// only one picture, so with several files it's the first one's. The picture is the full image,
    /// upright per its EXIF orientation.
    /// </summary>
    public static async Task CopyToClipboardAsync(TopLevel topLevel, IEnumerable<string> paths)
    {
        if (topLevel.Clipboard is not { } clipboard) return;
        var list = paths.ToList();
        var files = await ToStorageFilesAsync(topLevel, list);
        if (files.Count == 0) return;
        var picture = await Task.Run(() =>
        {
            try { return ImageLoader.Load(list[0], MaxClipboardSize, MaxClipboardSize); }
            catch (Exception) { return null; } // not an image Skia can read: copy the file only
        });

        var data = new DataTransfer();
        for (var i = 0; i < files.Count; i++)
        {
            var item = new DataTransferItem();
            item.SetFile(files[i]);
            if (i == 0 && picture is not null)
            {
                item.SetBitmap(picture);
                // PNGs also go on as "PNG" (the file itself), which browsers, Discord and Office read to keep transparency.
                if (string.Equals(Path.GetExtension(list[0]), ".png", StringComparison.OrdinalIgnoreCase))
                    item.Set(PngFormat, File.ReadAllBytes(list[0]));
            }
            data.Add(item);
        }
        await clipboard.SetDataAsync(data);
        await clipboard.FlushAsync(); // keep it available after the app closes
    }

    private static readonly DataFormat<byte[]> PngFormat = DataFormat.CreateBytesPlatformFormat("PNG");

    /// <summary>Largest side of the copied picture (full size for practically any photo).</summary>
    private const int MaxClipboardSize = 16384;

    /// <summary>What <see cref="MoveToTrash"/> did: the files moved, and the ones that failed with why.</summary>
    public sealed record TrashResult(List<string> Moved, List<(string Path, string Error)> Failed);

    /// <summary>Where deleted files go on this OS, for messages.</summary>
    public static string TrashName => OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash";

    /// <summary>
    /// Moves files to the Recycle Bin (Windows) or Trash (macOS, Linux), one at a time so one failure
    /// (file in use, read-only folder, …) doesn't stop the rest. Files that can't go to the trash
    /// (e.g. on a network drive, which has no Recycle Bin) are left alone and reported as failed:
    /// nothing is ever deleted permanently.
    /// </summary>
    public static TrashResult MoveToTrash(IEnumerable<string> paths)
    {
        var result = new TrashResult(new(), new());
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(path)) throw new FileNotFoundException("the file no longer exists");
                if (OperatingSystem.IsWindows()) RecycleWindows(path);
                else if (OperatingSystem.IsMacOS()) MoveInto(path, Path.Combine(Home, ".Trash"));
                else TrashFreedesktop(path);
                if (File.Exists(path)) throw new IOException("the file is still there");
                result.Moved.Add(path);
            }
            catch (Exception e)
            {
                result.Failed.Add((path, e.Message));
            }
        }
        return result;
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static void RecycleWindows(string path)
    {
        // SHFileOperation deletes permanently when the file can't be recycled: drives without a
        // Recycle Bin (network shares, USB sticks and memory cards), a bin that's turned off, or a
        // file too big for it. Refuse the drives that never have one; for the rest,
        // FOF_WANTNUKEWARNING makes Windows ask before deleting permanently (overriding
        // FOF_NOCONFIRMATION), and answering No leaves the file alone.
        if (path.StartsWith(@"\\") || new DriveInfo(Path.GetPathRoot(path)!).DriveType is not DriveType.Fixed)
            throw new IOException("this drive has no Recycle Bin, so the file was not deleted");
        var op = new ShFileOp
        {
            Func = FoDelete,
            From = path + "\0\0", // double-null-terminated list
            Flags = FofAllowUndo | FofNoConfirmation | FofWantNukeWarning | FofSilent | FofNoErrorUi,
        };
        var code = SHFileOperation(ref op);
        if (code != 0) throw new IOException($"Windows couldn't move it to the Recycle Bin (error 0x{code:X}); it may be open in another program");
        if (op.AnyOperationsAborted != 0) throw new IOException("moving it to the Recycle Bin was cancelled");
    }

    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x4, FofNoConfirmation = 0x10, FofAllowUndo = 0x40, FofNoErrorUi = 0x400, FofWantNukeWarning = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOp
    {
        public IntPtr Hwnd;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref ShFileOp op);

    /// <summary>The freedesktop.org trash (~/.local/share/Trash): the file plus a .trashinfo with its original path.</summary>
    private static void TrashFreedesktop(string path)
    {
        var trash = Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? data : Path.Combine(Home, ".local", "share"), "Trash");
        Directory.CreateDirectory(Path.Combine(trash, "info"));
        var target = MoveInto(path, Path.Combine(trash, "files"));
        File.WriteAllText(Path.Combine(trash, "info", Path.GetFileName(target) + ".trashinfo"),
            $"[Trash Info]\nPath={Uri.EscapeDataString(path).Replace("%2F", "/")}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
    }

    /// <summary>Moves a file into a folder, adding " (2)", " (3)", … if the name is taken. Returns the new path.</summary>
    private static string MoveInto(string path, string folder)
    {
        Directory.CreateDirectory(folder);
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var target = Path.Combine(folder, name + ext);
        for (var n = 2; File.Exists(target) || Directory.Exists(target); n++)
            target = Path.Combine(folder, $"{name} ({n}){ext}");
        File.Move(path, target);
        return target;
    }

    public static async Task DragOutAsync(TopLevel topLevel, PointerPressedEventArgs trigger, IEnumerable<string> paths)
    {
        var files = await ToStorageFilesAsync(topLevel, paths);
        if (files.Count == 0) return;
        var data = new DataTransfer();
        foreach (var f in files)
            data.Add(DataTransferItem.CreateFile(f));
        await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Copy);
    }
}
