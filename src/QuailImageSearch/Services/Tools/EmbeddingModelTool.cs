using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics.Tensors;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuailImageSearch.Services.Models;

namespace QuailImageSearch.Services;

/// <summary>
/// Adapter for a downloaded image-text embedding model (<see cref="ModelCatalog"/>), running
/// in-process on ONNX Runtime: indexes the folder (embedding only new or changed images), then
/// ranks the folder's images by cosine similarity to the query.
/// </summary>
public sealed class EmbeddingModelTool : ISearchTool
{
    public const string IdPrefix = "model:";
    private const int PrefetchDepth = 2; // batches decoded ahead of the one being embedded

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".jpe", ".png", ".webp", ".bmp", ".gif" };

    /// <summary>True for the file types that get indexed (and can be searched with).</summary>
    public static bool IsImageFile(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    // System and tool folders that never hold the user's pictures; dot-prefixed (hidden) folders are skipped too.
    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
        { "@eaDir", "node_modules", ".git", "System Volume Information", "$RECYCLE.BIN" };

    private readonly ModelInfo _info;
    private readonly DebugLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1); // one index/search at a time per model
    private ClipModel? _model;
    private EmbeddingIndex? _index;
    private volatile bool _disposed;

    private string _gpuMode;
    private int _gpuBatch;

    public EmbeddingModelTool(ModelInfo info, DebugLog log, string gpuMode = GpuModes.Auto, int gpuBatch = GpuBatch.Default)
    {
        _info = info;
        _log = log;
        _gpuMode = gpuMode;
        _gpuBatch = gpuBatch;
    }

    /// <summary>
    /// Where the image encoder runs, for status text: "GPU (name)" or "CPU (reason)"; null until the
    /// model has been loaded.
    /// </summary>
    public string? DeviceSummary { get; private set; }

    /// <summary>Changes the GPU preferences; the model is reloaded on the next search if they changed.</summary>
    public void SetGpuSettings(string mode, int batch)
    {
        if (mode == _gpuMode && batch == _gpuBatch) return;
        _gpuMode = mode;
        _gpuBatch = batch;
        if (!_gate.Wait(0))
        {
            _reloadModel = true; // a search is running; reload afterwards
            return;
        }
        try { UnloadModel(); }
        finally { _gate.Release(); }
    }

    private volatile bool _reloadModel;

    private void UnloadModel()
    {
        _model?.Dispose();
        _model = null;
        DeviceSummary = null;
    }

    public ModelInfo Info => _info;

    /// <summary>Identifies the model + preprocessing that produce this model's image embeddings.</summary>
    public static string EmbeddingVersion(ModelInfo info) =>
        $"{info.Id}@{info.Revision}/{ClipModel.PreprocessingVersion}/{info.Image.Size}-{info.Image.Resize}-{info.Image.Resample}";
    public string Id => IdPrefix + _info.Id;
    public string Name => _info.Name;
    public string ScoreVersion => $"{_info.Revision}/{ClipModel.PreprocessingVersion}";
    public bool AutoCalibrate => true;

    public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, IProgress<string>? progress, CancellationToken ct) =>
        SearchAsync(request, Name, model =>
        {
            var query = model.EmbedText(request.Query);
            return (query.Length, e => TensorPrimitives.Dot(query, e));
        }, progress, ct);

    /// <summary>
    /// Ranks the folder's images by similarity to the image at <paramref name="imagePath"/>, which
    /// may be inside or outside the folder. Raw scores are cosine similarities; the returned scale maps
    /// them to 0–100, where 0 is as alike as two unrelated photos and 100 is the same image.
    /// </summary>
    public async Task<(IReadOnlyList<SearchResult> Results, ScoreScale Scale)> SearchByImageAsync(
        SearchRequest request, string imagePath, IProgress<string>? progress, CancellationToken ct)
    {
        ScoreScale? scale = null;
        var results = await SearchAsync(request, $"{Name}, by image", model =>
        {
            var query = ImageEmbedding(model, imagePath);
            scale = new ScoreScale(UnrelatedImageSimilarity(query.Length, ct), 1);
            return (query.Length, e => TensorPrimitives.Dot(query, e));
        }, progress, ct);
        return (results, scale!);
    }

    /// <summary>The image's embedding: from the index if it's indexed and unchanged, else computed now.</summary>
    private float[] ImageEmbedding(ClipModel model, string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException($"Image not found: {path}");
        if (_index!.TryGet(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks) is { } stored) return stored;
        var pixels = model.Preprocess(file.FullName) ?? throw new InvalidDataException($"Couldn't read the image {file.Name}.");
        return model.EmbedImages([pixels])[0];
    }

    private double? _unrelatedSimilarity; // per loaded index; see UnrelatedImageSimilarity

    /// <summary>
    /// Median similarity between the bundled calibration photos (all of different subjects), i.e. how
    /// alike two unrelated photos typically look to this model. Indexes them first if needed.
    /// </summary>
    private double UnrelatedImageSimilarity(int dim, CancellationToken ct)
    {
        if (_unrelatedSimilarity is { } cached) return cached;
        var folder = PathUtil.CanonicalDirectory(ScoreCalibrator.ImagesDir);
        var embeddings = _index!.EmbeddingsUnder(folder, dim);
        if (embeddings.Count < 10)
        {
            UpdateIndex(folder, null, ct);
            embeddings = _index.EmbeddingsUnder(folder, dim);
            if (embeddings.Count < 2) throw new InvalidOperationException("Couldn't read the built-in calibration images.");
        }
        var pairs = new List<double>();
        for (var i = 0; i < embeddings.Count; i++)
            for (var j = i + 1; j < embeddings.Count; j++)
                pairs.Add(TensorPrimitives.Dot(embeddings[i], embeddings[j]));
        var median = ScoreCalibrator.Percentile(pairs, ScoreCalibrator.LowPercentileUnmatched);
        _log.Write($"{Name}: unrelated photos have median image similarity {median:0.000} ({embeddings.Count} calibration images)");
        _unrelatedSimilarity = median;
        return median;
    }

    /// <summary>
    /// Indexes the folder like a normal search, then ranks its images with the scorer that
    /// <paramref name="makeScorer"/> returns (given the loaded model): its embedding size and a
    /// function from an image embedding to a score. Text queries use it with the text embedding.
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, string label,
        Func<ClipModel, (int Dim, EmbeddingScorer Score)> makeScorer, IProgress<string>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() => Search(request, label, makeScorer, progress, ct), ct);
        }
        finally
        {
            if (_disposed) Unload();
            _gate.Release();
        }
    }

    private IReadOnlyList<SearchResult> Search(SearchRequest request, string label,
        Func<ClipModel, (int Dim, EmbeddingScorer Score)> makeScorer, IProgress<string>? progress, CancellationToken ct)
    {
        var folder = PathUtil.CanonicalDirectory(request.Folder);
        _log.Write($"search [{label}]: query=\"{request.Query}\" folder=\"{folder}\" top={request.MaxResults}");
        EnsureLoaded(progress);

        UpdateIndex(folder, progress, ct);

        progress?.Report("Searching…");
        var sw = Stopwatch.StartNew();
        var (dim, score) = makeScorer(_model!);
        var results = _index!.Search(folder, dim, score, request.MaxResults);
        var log = new StringBuilder($"similarity search took {sw.ElapsedMilliseconds} ms; {results.Count} result(s):");
        foreach (var r in results) log.Append($"\n  {r.Score:0.000}  {r.Path}");
        _log.Write(log.ToString());
        return results;
    }

    private void EnsureLoaded(IProgress<string>? progress)
    {
        if (_reloadModel)
        {
            _reloadModel = false;
            UnloadModel();
        }
        if (_model is null)
        {
            progress?.Report($"Loading {Name}…");
            var sw = Stopwatch.StartNew();
            var plan = DevicePlan.Choose(_info, _gpuMode, _gpuBatch);
            _model = new ClipModel(ModelManager.ModelDir(_info), _info, plan, _log.Write);
            DeviceSummary = _model.OnGpu ? _model.Device : $"CPU ({_model.DeviceReason})";
            _log.Write($"{Name} loaded in {sw.ElapsedMilliseconds} ms on {_model.Device}: {_model.DeviceReason}\n  {_model.Description}");
        }
        if (_index is null)
        {
            _index = new EmbeddingIndex(ModelManager.IndexPath(_info.Id), EmbeddingVersion(_info));
            if (_index.WasReset)
                _log.Write($"{Name}: index was built by an older version and will be rebuilt");
        }
    }

    /// <summary>
    /// Removes deleted files from this model's index (if it has one). Waits for a running search to
    /// finish first; errors are logged, since the next search in the folder would drop them anyway.
    /// </summary>
    public async Task ForgetFilesAsync(IReadOnlyList<string> paths)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed || (_index is null && !File.Exists(ModelManager.IndexPath(_info.Id)))) return;
            await Task.Run(() =>
            {
                _index ??= new EmbeddingIndex(ModelManager.IndexPath(_info.Id), EmbeddingVersion(_info));
                _index.Delete(paths);
            });
        }
        catch (Exception e)
        {
            _log.Write($"{Name}: couldn't remove deleted files from the index: {e.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Closes the index database so its files can be deleted (it reopens on the next search).
    /// Returns false if a search is running.
    /// </summary>
    public bool TryCloseIndex()
    {
        if (!_gate.Wait(0)) return false;
        try
        {
            _index?.Dispose();
            _index = null;
            _unrelatedSimilarity = null;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private float[]?[] PreprocessBatch(FileInfo[] batch, CancellationToken ct)
    {
        var pixels = new float[]?[batch.Length];
        Parallel.For(0, batch.Length, new ParallelOptions { CancellationToken = ct }, i =>
        {
            try { pixels[i] = _model!.Preprocess(batch[i].FullName); }
            catch (Exception) { pixels[i] = null; }
        });
        return pixels;
    }

    /// <summary>Embeds new/changed images under <paramref name="folder"/> and forgets deleted ones.</summary>
    private void UpdateIndex(string folder, IProgress<string>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        progress?.Report("Checking for new or changed images…");
        var indexed = _index!.ListUnder(folder);
        var files = EnumerateImages(folder, ct).ToList();
        var present = files.Select(f => f.FullName).ToHashSet(StringComparer.Ordinal);

        var removed = indexed.Keys.Where(p => !present.Contains(p)).ToList();
        if (removed.Count > 0) _index.Delete(removed);

        var todo = files.Where(f => !indexed.TryGetValue(f.FullName, out var e)
                                    || e.Size != f.Length || e.MTime != f.LastWriteTimeUtc.Ticks).ToList();
        var unreadable = new List<string>();
        var done = 0;
        var lastReport = Stopwatch.StartNew();
        var recent = new RateMeter(TimeSpan.FromSeconds(3)); // the speed shown: over the last 3 seconds
        var device = _model!.OnGpu ? "GPU" : "CPU";
        var batches = todo.Chunk(_model.BatchSize).ToList();
        // Decode the next batches while the current one is embedded, and time each stage for the log.
        TimeSpan decodeWait = default, embedTime = default, saveTime = default;
        var stage = new Stopwatch();
        foreach (var (batch, pixels, waited) in Prefetch.Run(batches, b => PreprocessBatch(b, ct), PrefetchDepth, ct))
        {
            ct.ThrowIfCancellationRequested(); // what's embedded so far stays in the index
            decodeWait += waited;
            stage.Restart();
            var ok = Enumerable.Range(0, batch.Length).Where(i => pixels[i] is not null).ToList();
            var embeddings = ok.Count == 0 ? [] : _model!.EmbedImages(ok.Select(i => pixels[i]!).ToList());
            embedTime += stage.Elapsed;
            stage.Restart();
            var rows = new List<(string, long, long, float[]?)>(batch.Length);
            for (var i = 0; i < batch.Length; i++)
            {
                var k = ok.IndexOf(i);
                if (k < 0) unreadable.Add(batch[i].FullName);
                rows.Add((batch[i].FullName, batch[i].Length, batch[i].LastWriteTimeUtc.Ticks, k < 0 ? null : embeddings[k]));
            }
            _index.Upsert(rows);
            saveTime += stage.Elapsed;

            done += batch.Length;
            recent.Add(done);
            if (lastReport.ElapsedMilliseconds > 300 || done == todo.Count)
            {
                lastReport.Restart();
                progress?.Report($"Indexing with {Name} on {device}: {done} / {todo.Count} new or changed images ({recent.Rate:0.0}/s)");
            }
        }

        var log = new StringBuilder($"index [{Name}]: {files.Count} image(s) in folder, {todo.Count - unreadable.Count} embedded, " +
                                    $"{unreadable.Count} unreadable, {removed.Count} removed, {files.Count - todo.Count} unchanged; took {sw.ElapsedMilliseconds} ms");
        if (todo.Count > 0)
            log.Append($"\n  {todo.Count / Math.Max(0.001, sw.Elapsed.TotalSeconds):0.0} images/s on {device}, batches of {_model.BatchSize}" +
                       $"{(_model.FixedShape ? " (fixed shape)" : "")}: waited {decodeWait.TotalSeconds:0.0} s for decoding, " +
                       $"embedding {embedTime.TotalSeconds:0.0} s, saving {saveTime.TotalSeconds:0.0} s");
        foreach (var u in unreadable.Take(10)) log.Append("\n  unreadable: ").Append(u);
        if (unreadable.Count > 10) log.Append($"\n  … and {unreadable.Count - 10} more");
        _log.Write(log.ToString());
    }

    private static IEnumerable<FileInfo> EnumerateImages(string folder, CancellationToken ct)
    {
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(folder));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            // Symbolic links and junctions are skipped: they can loop back to a parent folder, and they
            // lead outside the folder. (Cloud placeholders such as OneDrive's aren't links, so they're kept.)
            foreach (var sub in dir.EnumerateDirectories("*", options))
                if (!sub.Name.StartsWith('.') && !ExcludedDirs.Contains(sub.Name) && sub.LinkTarget is null)
                    stack.Push(sub);
            foreach (var file in dir.EnumerateFiles("*", options))
                if (!file.Name.StartsWith('.') && ImageExtensions.Contains(file.Extension))
                    yield return file;
        }
    }

    /// <summary>
    /// Releases the model and index files. If a search is running (indexing can take minutes), it
    /// happens when that search ends rather than blocking the caller.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        if (!_gate.Wait(0)) return;
        try { Unload(); }
        finally { _gate.Release(); }
    }

    private void Unload()
    {
        _model?.Dispose();
        _index?.Dispose();
        _model = null;
        _index = null;
        _unrelatedSimilarity = null;
    }
}

public static class PathUtil
{
    /// <summary>
    /// Full path of an existing directory, with the on-disk casing on Windows, so the same folder
    /// typed differently ("e:\pics" vs "E:\Pics") maps to the same index entries.
    /// </summary>
    public static string CanonicalDirectory(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!OperatingSystem.IsWindows()) return full;
        return Canonical(new DirectoryInfo(full));

        static string Canonical(DirectoryInfo dir)
        {
            if (dir.Parent is null) return dir.FullName.ToUpperInvariant(); // "C:\" or "\\SERVER\SHARE"
            var actual = dir.Parent.EnumerateDirectories(dir.Name).FirstOrDefault()?.Name ?? dir.Name;
            return Path.Combine(Canonical(dir.Parent), actual);
        }
    }
}
