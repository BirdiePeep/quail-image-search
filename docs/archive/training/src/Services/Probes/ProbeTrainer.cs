using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using QuailImageSearch.Services.Models;
using Parquet;

namespace QuailImageSearch.Services.Probes;

/// <summary>Settings for one training run.</summary>
public sealed record ProbeTrainingOptions(ModelInfo Model, string DatasetFolder, int MinExamples, int Epochs, string GpuMode, int GpuBatch);

/// <summary>
/// Trains a <see cref="LinearProbe"/> for one model on a tagged Parquet dataset (<see cref="TagDataset"/>):
/// <list type="number">
/// <item>Embeds every dataset image with the model (on the GPU when available) and caches the
/// embeddings next to the dataset, in .imagesearch-features\{model id}, one file per Parquet file.
/// Finished files are kept, so a cancelled or failed run continues where it stopped.</item>
/// <item>Trains one logistic regression per tag (tags with at least MinExamples images) with Adam,
/// holding out 5% of the images.</item>
/// <item>Measures each tag's average precision on the held-out images and saves the probe.</item>
/// </list>
/// One run at a time; it keeps going when the Preferences window is closed.
/// </summary>
public sealed partial class ProbeTrainer : ObservableObject
{
    public const string CacheFolderName = ".imagesearch-features";
    private const int PrefetchDepth = 2; // batches decoded ahead of the one being embedded
    private const int TrainBatch = 1024;
    private const float LearningRate = 1e-3f;
    private const float WeightDecay = 1e-4f;
    private const int ValidationEvery = 20; // 1 in 20 images (5%) is held out

    private readonly ProbeStore _store;
    private readonly DebugLog _log;
    private CancellationTokenSource? _cts;

    public ProbeTrainer(ProbeStore store, DebugLog log)
    {
        _store = store;
        _log = log;
    }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    /// <summary>0–100 for the current stage.</summary>
    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    public void Cancel() => _cts?.Cancel();

    public async Task RunAsync(ProbeTrainingOptions options)
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsRunning = true;
        Progress = 0;
        StatusText = "Starting…";
        var sw = Stopwatch.StartNew();
        try
        {
            var probe = await Task.Run(() => TrainAsync(options, ct), ct);
            _store.Save(probe);
            var h = probe.Header;
            StatusText = $"Done in {Elapsed(sw.Elapsed)}: {h.Tags.Count:N0} tags, mean average precision {h.MeanAveragePrecision:0.000} " +
                         $"on {h.ValidationImages:N0} held-out images.";
            _log.Write($"tag probe for {options.Model.Name} saved to {ProbeStore.PathFor(options.Model.Id)}");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled. Images embedded so far are kept, so the next run continues from there.";
            _log.Write("tag probe training cancelled");
        }
        catch (Exception e)
        {
            StatusText = "Failed: " + e.Message;
            _log.Write($"tag probe training failed: {e}");
        }
        finally
        {
            Progress = 0;
            IsRunning = false;
            _cts = null;
        }
    }

    private void Report(string text, double progress) =>
        Dispatcher.UIThread.Post(() =>
        {
            StatusText = text;
            Progress = progress;
        });

    private async Task<LinearProbe> TrainAsync(ProbeTrainingOptions o, CancellationToken ct)
    {
        var folder = Path.GetFullPath(o.DatasetFolder);
        var cacheDir = Path.Combine(folder, CacheFolderName, o.Model.Id);
        var version = EmbeddingModelTool.EmbeddingVersion(o.Model);
        _log.Write($"tag probe: training for {o.Model.Name} on {folder} (min {o.MinExamples} examples per tag, {o.Epochs} epochs); cache {cacheDir}");

        var files = TagDataset.FindFiles(folder);
        if (files.Count == 0) throw new InvalidDataException("no .parquet files in the dataset folder");
        var dim = await ExtractFeaturesAsync(o, folder, files, cacheDir, version, ct);

        var data = LoadFeatures(files, folder, cacheDir, dim, ct);
        return Fit(o, folder, version, data, ct);
    }

    // ---- 1. Embedding the dataset ----

    private static string CacheKey(string folder, string file) =>
        Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '~').Replace(Path.AltDirectorySeparatorChar, '~');

    /// <summary>Embeds the Parquet files that aren't cached yet. Returns the embedding size.</summary>
    private async Task<int> ExtractFeaturesAsync(ProbeTrainingOptions o, string folder, List<string> files, string cacheDir, string version, CancellationToken ct)
    {
        Directory.CreateDirectory(cacheDir);
        var versionFile = Path.Combine(cacheDir, "version.txt");
        var cached = File.Exists(versionFile) ? File.ReadAllLines(versionFile) : [];
        if (cached.Length < 2 || cached[0] != version)
        {
            // Made by another model version (or not at all): start over.
            foreach (var f in Directory.EnumerateFiles(cacheDir)) File.Delete(f);
            cached = [];
        }

        var todo = files.Where(f => !File.Exists(Path.Combine(cacheDir, CacheKey(folder, f) + ".tags"))).ToList();
        if (todo.Count == 0) return int.Parse(cached[1]);

        Report($"Counting images in {todo.Count} Parquet file(s)…", 0);
        long total = 0;
        foreach (var f in todo)
        {
            await using var r = await ParquetReader.CreateAsync(f, cancellationToken: ct);
            total += r.RowGroups.Sum(g => g.RowCount);
        }

        Report($"Loading {o.Model.Name}…", 0);
        using var model = new ClipModel(ModelManager.ModelDir(o.Model), o.Model, DevicePlan.Choose(o.Model, o.GpuMode, o.GpuBatch), _log.Write);
        var device = model.OnGpu ? model.Device : $"CPU ({model.DeviceReason})";
        _log.Write($"tag probe: embedding {total:N0} images in {todo.Count} file(s) with {o.Model.Name} on {device}");
        var dim = model.EmbedImages([new float[3 * o.Model.Image.Size * o.Model.Image.Size]])[0].Length;
        File.WriteAllLines(versionFile, [version, dim.ToString()]);

        var sw = Stopwatch.StartNew();
        long done = 0, unreadable = 0;
        var lastReport = Stopwatch.StartNew();
        var recent = new RateMeter(TimeSpan.FromSeconds(3));   // the speed shown: over the last 3 seconds
        var steady = new RateMeter(TimeSpan.FromSeconds(30));  // for the time left: steadier, but still current
        TimeSpan readWait = default, decodeWait = default, embedTime = default;
        var stage = new Stopwatch();
        foreach (var file in todo)
        {
            var key = CacheKey(folder, file);
            var featuresPath = Path.Combine(cacheDir, key + ".f16");
            var tagsPath = Path.Combine(cacheDir, key + ".tags");
            try
            {
                await using (var reader = await ParquetReader.CreateAsync(file, cancellationToken: ct))
                using (var features = new FileStream(featuresPath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                using (var tags = new StreamWriter(tagsPath + ".tmp", false, new UTF8Encoding(false), 1 << 16))
                {
                    var columns = TagDataset.FindColumns(reader.Schema, file);
                    var groups = reader.RowGroupCount;
                    // Read the next row group from disk while this one is embedded.
                    var next = TagDataset.ReadRowGroupAsync(reader, columns, 0, ct);
                    for (var g = 0; g < groups; g++)
                    {
                        stage.Restart();
                        var group = await next;
                        readWait += stage.Elapsed;
                        next = g + 1 < groups ? TagDataset.ReadRowGroupAsync(reader, columns, g + 1, ct) : null!;

                        var batches = Enumerable.Range(0, group.Images.Length).Chunk(model.BatchSize).ToList();
                        // Decode the next batches while the current one is embedded.
                        foreach (var (batch, pixels, waited) in Prefetch.Run(batches, b => Decode(model, group, b, ct), PrefetchDepth, ct))
                        {
                            ct.ThrowIfCancellationRequested();
                            decodeWait += waited;
                            var rows = batch.Where((_, i) => pixels[i] is not null).ToList();
                            unreadable += batch.Length - rows.Count;
                            if (rows.Count > 0)
                            {
                                stage.Restart();
                                var embeddings = model.EmbedImages(pixels.Where(p => p is not null).ToList()!);
                                embedTime += stage.Elapsed;
                                var half = new Half[dim];
                                for (var i = 0; i < rows.Count; i++)
                                {
                                    for (var d = 0; d < dim; d++) half[d] = (Half)embeddings[i][d];
                                    features.Write(MemoryMarshal.AsBytes(half.AsSpan()));
                                    // One line per image; tags can't contain spaces.
                                    tags.Write(string.Join(' ', group.Tags[rows[i]].Select(t => t.Replace(' ', '_'))));
                                    tags.Write('\n');
                                }
                            }

                            done += batch.Length;
                            recent.Add(done);
                            steady.Add(done);
                            if (lastReport.ElapsedMilliseconds > 500)
                            {
                                lastReport.Restart();
                                var left = TimeSpan.FromSeconds((total - done) / Math.Max(0.001, steady.Rate));
                                Report($"Embedding dataset images with {o.Model.Name} on {(model.OnGpu ? "GPU" : "CPU")}: " +
                                       $"{done:N0} / {total:N0} ({recent.Rate:0}/s, about {Elapsed(left)} left)", done * 100.0 / Math.Max(1, total));
                            }
                        }
                    }
                }
                // Only complete files are kept; the .tags file marks a file as done.
                File.Move(featuresPath + ".tmp", featuresPath, overwrite: true);
                File.Move(tagsPath + ".tmp", tagsPath, overwrite: true);
            }
            finally
            {
                foreach (var tmp in new[] { featuresPath + ".tmp", tagsPath + ".tmp" })
                    if (File.Exists(tmp)) File.Delete(tmp);
            }
        }
        _log.Write($"tag probe: embedded {done:N0} images in {Elapsed(sw.Elapsed)} ({done / Math.Max(0.001, sw.Elapsed.TotalSeconds):0}/s, " +
                   $"batches of {model.BatchSize}{(model.FixedShape ? ", fixed shape" : "")}); {unreadable} unreadable; " +
                   $"waited {readWait.TotalSeconds:0.0} s for reading and {decodeWait.TotalSeconds:0.0} s for decoding, embedding {embedTime.TotalSeconds:0.0} s");
        return dim;
    }

    private static float[]?[] Decode(ClipModel model, TagDataset.RowGroup group, int[] rows, CancellationToken ct)
    {
        var pixels = new float[]?[rows.Length];
        Parallel.For(0, rows.Length, new ParallelOptions { CancellationToken = ct }, i =>
        {
            try { pixels[i] = group.Images[rows[i]] is { } bytes ? model.Preprocess(bytes) : null; }
            catch (Exception) { pixels[i] = null; }
        });
        return pixels;
    }

    // ---- 2. Training ----

    /// <summary>The cached dataset: embeddings (row-major, Half) and each image's tags as ids into <see cref="TagNames"/>.</summary>
    private sealed record Features(int Dim, int Count, Half[] Embeddings, int[][] Tags, List<string> TagNames);

    private Features LoadFeatures(List<string> files, string folder, string cacheDir, int dim, CancellationToken ct)
    {
        Report("Loading embedded images…", 0);
        long total = files.Sum(f => new FileInfo(Path.Combine(cacheDir, CacheKey(folder, f) + ".f16")).Length) / (2L * dim);
        if (total * dim > Array.MaxLength) throw new InvalidDataException($"too many images ({total:N0}) for one training run");
        var embeddings = new Half[total * dim];
        var tags = new List<int[]>((int)total);
        var names = new List<string>();
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var offset = 0L;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var key = CacheKey(folder, f);
            var bytes = MemoryMarshal.AsBytes(embeddings.AsSpan((int)offset));
            using (var s = File.OpenRead(Path.Combine(cacheDir, key + ".f16")))
            {
                var n = (int)s.Length;
                s.ReadExactly(bytes[..n]);
                offset += n / 2;
            }
            foreach (var line in File.ReadLines(Path.Combine(cacheDir, key + ".tags")))
            {
                tags.Add(line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t =>
                {
                    if (!ids.TryGetValue(t, out var id))
                    {
                        ids[t] = id = names.Count;
                        names.Add(t);
                    }
                    return id;
                }).Distinct().ToArray());
            }
        }
        if (tags.Count != total) throw new InvalidDataException($"the embedding cache is inconsistent ({tags.Count} tag lines, {total} embeddings); delete {cacheDir} and train again");
        return new Features(dim, (int)total, embeddings, tags.ToArray(), names);
    }

    private LinearProbe Fit(ProbeTrainingOptions o, string folder, string version, Features data, CancellationToken ct)
    {
        var (dim, n) = (data.Dim, data.Count);
        var isValidation = Enumerable.Range(0, n).Select(i => (uint)i * 2654435761u % ValidationEvery == 0).ToArray();
        var train = Enumerable.Range(0, n).Where(i => !isValidation[i]).ToArray();
        var validation = Enumerable.Range(0, n).Where(i => isValidation[i]).ToArray();

        // Tags with enough training images, most frequent first.
        var trainCounts = new int[data.TagNames.Count];
        foreach (var i in train)
            foreach (var t in data.Tags[i])
                trainCounts[t]++;
        var selected = Enumerable.Range(0, trainCounts.Length).Where(t => trainCounts[t] >= o.MinExamples)
            .OrderByDescending(t => trainCounts[t]).ThenBy(t => data.TagNames[t], StringComparer.Ordinal).ToArray();
        if (selected.Length == 0) throw new InvalidDataException($"no tag has {o.MinExamples} or more images; lower 'Min images per tag'");
        var tags = selected.Select(t => data.TagNames[t]).ToArray();
        var counts = selected.Select(t => trainCounts[t]).ToArray();
        var local = Enumerable.Repeat(-1, trainCounts.Length).ToArray(); // dataset tag id -> probe tag index
        for (var k = 0; k < selected.Length; k++) local[selected[k]] = k;
        var labels = data.Tags.Select(ts => ts.Select(t => local[t]).Where(t => t >= 0).ToArray()).ToArray();
        var tagCount = tags.Length;
        _log.Write($"tag probe: {n:N0} images ({train.Length:N0} training, {validation.Length:N0} held out), {trainCounts.Count(c => c > 0):N0} distinct tags, " +
                   $"{tagCount:N0} with at least {o.MinExamples} images");

        // Per-dimension standardization from the training images.
        var mean = new double[dim];
        var sq = new double[dim];
        foreach (var i in train)
        {
            var row = data.Embeddings.AsSpan(i * dim, dim);
            for (var d = 0; d < dim; d++)
            {
                var v = (double)row[d];
                mean[d] += v;
                sq[d] += v * v;
            }
        }
        var meanF = new float[dim];
        var invStd = new float[dim];
        for (var d = 0; d < dim; d++)
        {
            var m = mean[d] / train.Length;
            meanF[d] = (float)m;
            invStd[d] = (float)(1 / Math.Sqrt(Math.Max(1e-12, sq[d] / train.Length - m * m)));
        }

        // Weights start at 0 and biases at each tag's base rate, so training starts from sensible probabilities.
        var w = new float[tagCount * dim];
        var bias = new float[tagCount];
        for (var t = 0; t < tagCount; t++)
        {
            var p = Math.Clamp(counts[t] / (double)train.Length, 1e-6, 1 - 1e-6);
            bias[t] = (float)Math.Log(p / (1 - p));
        }
        var mW = new float[w.Length];
        var vW = new float[w.Length];
        var mB = new float[tagCount];
        var vB = new float[tagCount];

        var x = new float[TrainBatch * dim];
        var y = new byte[tagCount * TrainBatch]; // tag-major: y[t * TrainBatch + i]
        var stepsPerEpoch = (train.Length + TrainBatch - 1) / TrainBatch;
        var totalSteps = stepsPerEpoch * o.Epochs;
        var step = 0;
        var rng = new Random(12345);
        var sw = Stopwatch.StartNew();
        var lastReport = Stopwatch.StartNew();
        const float beta1 = 0.9f, beta2 = 0.999f, eps = 1e-8f;
        // Tags are processed in parallel blocks; each tag's loss only depends on its own weights.
        var blocks = Partition(tagCount, Environment.ProcessorCount * 4);

        for (var epoch = 0; epoch < o.Epochs; epoch++)
        {
            rng.Shuffle(train);
            double epochLoss = 0;
            for (var s = 0; s < stepsPerEpoch; s++)
            {
                ct.ThrowIfCancellationRequested();
                var batch = train.AsSpan(s * TrainBatch, Math.Min(TrainBatch, train.Length - s * TrainBatch)).ToArray();
                var bs = batch.Length;
                Array.Clear(y);
                for (var i = 0; i < bs; i++)
                {
                    LoadStandardized(data, batch[i], meanF, invStd, x.AsSpan(i * dim, dim));
                    foreach (var t in labels[batch[i]]) y[t * TrainBatch + i] = 1;
                }

                step++;
                // Cosine learning-rate decay to 0; Adam with decoupled weight decay (AdamW).
                var lr = LearningRate * 0.5f * (1 + MathF.Cos(MathF.PI * (step - 1) / totalSteps));
                var c1 = 1 - MathF.Pow(beta1, step);
                var c2 = 1 - MathF.Pow(beta2, step);
                double batchLoss = 0;
                var gate = new object();
                Parallel.ForEach(blocks, new ParallelOptions { CancellationToken = ct }, block =>
                {
                    var grad = new float[dim];
                    double loss = 0;
                    for (var t = block.Start; t < block.End; t++)
                    {
                        var wt = w.AsSpan(t * dim, dim);
                        Array.Clear(grad);
                        float gb = 0;
                        for (var i = 0; i < bs; i++)
                        {
                            var xi = x.AsSpan(i * dim, dim);
                            var z = TensorPrimitives.Dot(wt, xi) + bias[t];
                            var p = LinearProbe.Sigmoid(z);
                            var label = y[t * TrainBatch + i];
                            // Binary cross-entropy, computed stably from the logit.
                            loss += Math.Max(z, 0) - z * label + Math.Log(1 + Math.Exp(-Math.Abs(z)));
                            var g = (p - label) / bs;
                            TensorPrimitives.MultiplyAdd(xi, g, grad, grad);
                            gb += g;
                        }
                        AdamStep(wt, grad, mW.AsSpan(t * dim, dim), vW.AsSpan(t * dim, dim), lr, c1, c2, WeightDecay);
                        AdamStep(bias.AsSpan(t, 1), [gb], mB.AsSpan(t, 1), vB.AsSpan(t, 1), lr, c1, c2, 0);
                    }
                    lock (gate) batchLoss += loss;
                });
                epochLoss += batchLoss / (bs * (double)tagCount);

                if (lastReport.ElapsedMilliseconds > 500 || s == stepsPerEpoch - 1)
                {
                    lastReport.Restart();
                    var left = TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds / step * (totalSteps - step));
                    Report($"Training {tagCount:N0} tag classifiers: epoch {epoch + 1}/{o.Epochs}, batch {s + 1}/{stepsPerEpoch}, " +
                           $"loss {epochLoss / (s + 1) * 1000:0.00}e-3 (about {Elapsed(left)} left)", step * 100.0 / totalSteps);
                }

                void AdamStep(Span<float> param, ReadOnlySpan<float> g, Span<float> m, Span<float> v, float rate, float bc1, float bc2, float decay)
                {
                    for (var k = 0; k < param.Length; k++)
                    {
                        m[k] = beta1 * m[k] + (1 - beta1) * g[k];
                        v[k] = beta2 * v[k] + (1 - beta2) * g[k] * g[k];
                        param[k] -= rate * (m[k] / bc1 / (MathF.Sqrt(v[k] / bc2) + eps) + decay * param[k]);
                    }
                }
            }
            _log.Write($"tag probe: epoch {epoch + 1}/{o.Epochs} mean loss {epochLoss / stepsPerEpoch:0.00000} ({Elapsed(sw.Elapsed)} so far)");
        }

        // ---- 3. Evaluation on the held-out images ----
        Report($"Measuring accuracy on {validation.Length:N0} held-out images…", 100);
        var stats = Evaluate(data, validation, labels, tags, counts, meanF, invStd, w, bias, ct);
        var scored = stats.Where(s => s.ValidationPositives > 0).ToList();
        var map = scored.Count == 0 ? 0 : scored.Average(s => s.AveragePrecision);
        var log = new StringBuilder($"tag probe: mean average precision {map:0.000} over {scored.Count:N0} tags with held-out examples; trained in {Elapsed(sw.Elapsed)}");
        foreach (var s in scored.OrderByDescending(s => s.TrainPositives).Take(15))
            log.Append($"\n  {s.Tag}: AP {s.AveragePrecision:0.000} ({s.TrainPositives:N0} training images)");
        _log.Write(log.ToString());

        var header = new ProbeHeader(o.Model.Id, version, dim, folder, train.Length, validation.Length, o.MinExamples, o.Epochs,
                                     map, DateTime.Now, stats);
        return new LinearProbe(header, meanF, invStd, w, bias);
    }

    private static void LoadStandardized(Features data, int row, float[] mean, float[] invStd, Span<float> destination)
    {
        var src = data.Embeddings.AsSpan(row * data.Dim, data.Dim);
        for (var d = 0; d < destination.Length; d++) destination[d] = ((float)src[d] - mean[d]) * invStd[d];
    }

    private static List<TagStats> Evaluate(Features data, int[] validation, int[][] labels, string[] tags, int[] trainCounts,
                                           float[] mean, float[] invStd, float[] w, float[] bias, CancellationToken ct)
    {
        var dim = data.Dim;
        var x = new float[validation.Length * dim];
        for (var i = 0; i < validation.Length; i++)
            LoadStandardized(data, validation[i], mean, invStd, x.AsSpan(i * dim, dim));
        var positives = new List<int>[tags.Length];
        for (var t = 0; t < tags.Length; t++) positives[t] = new();
        for (var i = 0; i < validation.Length; i++)
            foreach (var t in labels[validation[i]])
                positives[t].Add(i);

        var stats = new TagStats[tags.Length];
        Parallel.For(0, tags.Length, new ParallelOptions { CancellationToken = ct }, () => (new float[validation.Length], new int[validation.Length], new bool[validation.Length]),
            (t, _, buffers) =>
            {
                var (scores, order, isPositive) = buffers;
                var wt = w.AsSpan(t * dim, dim);
                for (var i = 0; i < validation.Length; i++)
                {
                    scores[i] = TensorPrimitives.Dot(wt, x.AsSpan(i * dim, dim)) + bias[t];
                    order[i] = i;
                    isPositive[i] = false;
                }
                foreach (var i in positives[t]) isPositive[i] = true;
                stats[t] = new TagStats(tags[t], trainCounts[t], positives[t].Count, AveragePrecision(scores, order, isPositive, positives[t].Count));
                return buffers;
            }, _ => { });
        return stats.ToList();
    }

    /// <summary>Average precision: the mean, over positives, of the precision at each positive's rank.</summary>
    private static double AveragePrecision(float[] scores, int[] order, bool[] isPositive, int positives)
    {
        if (positives == 0) return 0;
        Array.Sort(scores.ToArray(), order); // ascending by score
        double sum = 0;
        var hits = 0;
        for (var rank = 0; rank < order.Length; rank++)
        {
            if (!isPositive[order[order.Length - 1 - rank]]) continue;
            hits++;
            sum += hits / (double)(rank + 1);
            if (hits == positives) break;
        }
        return sum / positives;
    }

    private static List<(int Start, int End)> Partition(int count, int parts)
    {
        var size = Math.Max(1, (count + parts - 1) / parts);
        var result = new List<(int, int)>();
        for (var s = 0; s < count; s += size) result.Add((s, Math.Min(count, s + size)));
        return result;
    }

    private static string Elapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min" : $"{Math.Max(1, t.Seconds)} s";
}
