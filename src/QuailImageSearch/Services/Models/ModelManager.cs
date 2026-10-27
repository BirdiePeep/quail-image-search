using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QuailImageSearch.Services.Models;

/// <summary>Download state of one catalog model, bound to the Models page in Preferences.</summary>
public sealed partial class ModelEntry(ModelInfo info) : ObservableObject
{
    public ModelInfo Info { get; } = info;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    public partial bool IsInstalled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    public partial bool IsDownloading { get; set; }

    /// <summary>0–100.</summary>
    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    public bool CanDownload => !IsInstalled && !IsDownloading;

    /// <summary>Bytes used by this model's search index (kept even if the model itself is deleted).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndexSizeText), nameof(HasIndex))]
    public partial long IndexSize { get; set; }

    public string IndexSizeText => IndexSize == 0 ? "no index" : ModelManager.FormatSize(IndexSize);

    public bool HasIndex => IndexSize > 0;

    /// <summary>Score calibration status for this model's tool (set by the Preferences window).</summary>
    public CalibrationState? Calibration { get; set; }

    internal CancellationTokenSource? Cts { get; set; }
}

/// <summary>A search index whose model isn't installed.</summary>
public sealed record DanglingIndex(string ModelId, string Name, long Size)
{
    public string SizeText => ModelManager.FormatSize(Size);
}

/// <summary>
/// Downloads catalog models from Hugging Face into the app-data folder
/// (%APPDATA%\QuailImageSearch\models\{id}) and tracks which are installed.
/// </summary>
public sealed class ModelManager
{
    private static readonly HttpClient Http = CreateHttp();
    private readonly DebugLog _log;

    public ModelManager(DebugLog log)
    {
        _log = log;
        Entries = ModelCatalog.All.Select(m => new ModelEntry(m)).ToList();
        foreach (var e in Entries) RefreshState(e);
        RefreshIndexSizes();
    }

    public static string Root { get; } = AppPaths.File("models");

    public static string IndexRoot { get; } = AppPaths.File("indexes");

    public IReadOnlyList<ModelEntry> Entries { get; }

    public IEnumerable<ModelEntry> Installed => Entries.Where(e => e.IsInstalled);

    /// <summary>Raised on the UI thread after a model is installed or deleted.</summary>
    public event Action? InstalledChanged;

    /// <summary>Raised before a model's files are deleted, so its tool can release them.</summary>
    public event Action<ModelInfo>? Uninstalling;

    public static string ModelDir(ModelInfo m) => Path.Combine(Root, m.Id);

    // ---- Search indexes (one SQLite database per model; see EmbeddingIndex) ----

    public static string IndexPath(string modelId) => Path.Combine(IndexRoot, modelId + ".sqlite");

    /// <summary>
    /// Set by the tool registry: closes the model's open index so its files can be deleted.
    /// Returns false if the model is busy (a search is running).
    /// </summary>
    public Func<string, bool>? ReleaseIndex { get; set; }

    /// <summary>Total size of all index files, including indexes of models no longer in the list.</summary>
    public static long TotalIndexSize() =>
        Directory.Exists(IndexRoot) ? Directory.EnumerateFiles(IndexRoot).Sum(f => new FileInfo(f).Length) : 0;

    public void RefreshIndexSizes()
    {
        foreach (var e in Entries)
            e.IndexSize = IndexFiles(e.Info.Id).Where(File.Exists).Sum(f => new FileInfo(f).Length);
    }

    /// <summary>Deletes one model's index. Returns an error message, or null on success.</summary>
    public string? DeleteIndex(string modelId)
    {
        if (ReleaseIndex?.Invoke(modelId) == false)
            return "it's being used by a running search; try again when the search finishes";
        var failed = IndexFiles(modelId).Where(File.Exists).Where(f => !TryDelete(f)).ToList();
        RefreshIndexSizes();
        if (failed.Count > 0) return "some files are in use: " + string.Join(", ", failed.Select(Path.GetFileName));
        _log.Write($"search index for {modelId} deleted");
        return null;
    }

    /// <summary>Deletes every model's index. Returns error messages (empty on success).</summary>
    public List<string> DeleteAllIndexes() => DeleteIndexes(IndexIds());

    /// <summary>Deletes the given models' indexes. Returns error messages (empty on success).</summary>
    public List<string> DeleteIndexes(IEnumerable<string> modelIds)
    {
        var errors = new List<string>();
        foreach (var id in modelIds.ToList())
            if (DeleteIndex(id) is { } error)
                errors.Add($"{id}: {error}");
        return errors;
    }

    /// <summary>
    /// Indexes whose model isn't installed: the model was deleted, or it's no longer in the model
    /// list at all. They can't be searched until the model is downloaded again.
    /// </summary>
    public List<DanglingIndex> DanglingIndexes() =>
        IndexIds()
            .Where(id => Entries.FirstOrDefault(e => e.Info.Id == id) is not { IsInstalled: true })
            .Select(id => new DanglingIndex(id,
                Entries.FirstOrDefault(e => e.Info.Id == id)?.Info.Name ?? $"{id} (not in the model list)",
                IndexFiles(id).Where(File.Exists).Sum(f => new FileInfo(f).Length)))
            .OrderBy(d => d.Name)
            .ToList();

    /// <summary>Model ids that have index files on disk.</summary>
    private static List<string> IndexIds() =>
        !Directory.Exists(IndexRoot)
            ? []
            : Directory.EnumerateFiles(IndexRoot, "*.sqlite*")
                .Select(Path.GetFileName)
                .Select(n => n![..n!.IndexOf(".sqlite", StringComparison.Ordinal)])
                .Distinct()
                .ToList();

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1e9:0.0} GB",
        >= 1_000_000 => $"{bytes / 1e6:0.0} MB",
        _ => $"{Math.Max(1, bytes / 1000)} KB",
    };

    private static string[] IndexFiles(string modelId)
    {
        var db = IndexPath(modelId);
        return [db, db + "-wal", db + "-shm"];
    }

    private static void RefreshState(ModelEntry e)
    {
        e.IsInstalled = e.Info.AllFiles.All(f =>
        {
            var path = Path.Combine(ModelDir(e.Info), f.LocalName);
            return File.Exists(path) && (f.Size == 0 || new FileInfo(path).Length == f.Size);
        });
        e.StatusText = e.IsInstalled ? "Installed" : $"Not installed ({e.Info.SizeText} download)";
    }

    public async Task DownloadAsync(ModelEntry entry)
    {
        if (!entry.CanDownload) return;
        var info = entry.Info;
        var dir = ModelDir(info);
        Directory.CreateDirectory(dir);
        entry.Cts = new CancellationTokenSource();
        var ct = entry.Cts.Token;
        entry.IsDownloading = true;
        entry.Progress = 0;
        _log.Write($"downloading {info.Name} from {info.SourceUrl} (revision {info.Revision[..7]})");

        long done = 0;
        var total = info.TotalSize;
        try
        {
            foreach (var file in info.AllFiles)
            {
                var target = Path.Combine(dir, file.LocalName);
                if (File.Exists(target) && (file.Size == 0 || new FileInfo(target).Length == file.Size))
                {
                    done += file.Size;
                    continue; // already downloaded (e.g. shared with another variant or an earlier attempt)
                }
                var part = target + ".part";
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using (var response = await Http.GetAsync(info.FileUrl(file), HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    response.EnsureSuccessStatusCode();
                    await using var src = await response.Content.ReadAsStreamAsync(ct);
                    await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                    var buffer = new byte[1 << 16];
                    var lastUi = DateTime.MinValue;
                    int n;
                    while ((n = await ReadWithTimeoutAsync(src, buffer, ct)) > 0)
                    {
                        hash.AppendData(buffer, 0, n);
                        await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                        done += n;
                        if (DateTime.UtcNow - lastUi > TimeSpan.FromMilliseconds(200))
                        {
                            lastUi = DateTime.UtcNow;
                            var (d, pct) = (done, total > 0 ? Math.Min(100, done * 100.0 / total) : 0);
                            Dispatcher.UIThread.Post(() =>
                            {
                                entry.Progress = pct;
                                entry.StatusText = $"Downloading {file.LocalName}… {d / 1_000_000.0:0} / {total / 1_000_000.0:0} MB";
                            });
                        }
                    }
                }
                if (file.Size > 0 && new FileInfo(part).Length != file.Size)
                    throw new IOException($"{file.LocalName}: expected {file.Size} bytes, got {new FileInfo(part).Length}");
                // The pinned revision and HTTPS say where the file came from; the hash says it's the right file.
                if (file.Sha256 is { } expected && Convert.ToHexStringLower(hash.GetHashAndReset()) is var actual && actual != expected)
                    throw new IOException($"{file.LocalName} failed its integrity check (SHA-256 {actual[..12]}…, expected {expected[..12]}…), so it wasn't installed");
                File.Move(part, target, overwrite: true);
            }
            _log.Write($"{info.Name} installed in {dir}");
        }
        catch (OperationCanceledException)
        {
            _log.Write($"download of {info.Name} cancelled");
        }
        catch (Exception e)
        {
            _log.Write($"download of {info.Name} failed: {e.Message}");
            entry.StatusText = "Download failed: " + e.Message;
            entry.IsDownloading = false;
            return;
        }
        finally
        {
            foreach (var part in Directory.EnumerateFiles(dir, "*.part")) TryDelete(part);
            entry.Cts = null;
        }

        entry.IsDownloading = false;
        entry.Progress = 0;
        RefreshState(entry);
        if (entry.IsInstalled) InstalledChanged?.Invoke();
    }

    /// <summary>
    /// Reads the next chunk, giving up if the server sends nothing for <see cref="StallTimeout"/>
    /// (the client has no overall timeout, since a model download can take a long time).
    /// </summary>
    private static async Task<int> ReadWithTimeoutAsync(Stream src, byte[] buffer, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        try
        {
            return await src.ReadAsync(buffer, stall.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"the server sent nothing for {StallTimeout.TotalSeconds:0} seconds");
        }
    }

    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    public void CancelDownload(ModelEntry entry) => entry.Cts?.Cancel();

    /// <summary>Deletes a downloaded model's files. Returns an error message, or null on success.</summary>
    public string? Delete(ModelEntry entry)
    {
        Uninstalling?.Invoke(entry.Info);
        // Everything in the model's folder is ours, including files from older catalog versions.
        var failed = Directory.Exists(ModelDir(entry.Info))
            ? Directory.EnumerateFiles(ModelDir(entry.Info)).Where(f => !TryDelete(f)).ToList()
            : [];
        TryDeleteDir(ModelDir(entry.Info));
        RefreshState(entry);
        InstalledChanged?.Invoke();
        if (failed.Count > 0)
        {
            var error = "some files are in use (try again when no search is running): " + string.Join(", ", failed.Select(Path.GetFileName));
            _log.Write($"{entry.Info.Name}: couldn't delete everything: {error}");
            return error;
        }
        _log.Write($"{entry.Info.Name} deleted (its search index in {IndexRoot} is kept)");
        return null;
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
        catch (IOException) { }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"QuailImageSearch/{typeof(ModelManager).Assembly.GetName().Version?.ToString(3) ?? "1.0"}");
        return http;
    }
}
