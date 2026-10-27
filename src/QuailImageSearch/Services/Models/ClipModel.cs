using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace QuailImageSearch.Services.Models;

/// <summary>"auto": use the GPU if the checks pass (default); "on": try the GPU even if they don't; "off": CPU only.</summary>
public static class GpuModes
{
    public const string Auto = "auto";
    public const string On = "on";
    public const string Off = "off";
    public static readonly string[] All = [Auto, On, Off];
}

/// <summary>
/// The "GPU batch size" preference: images per GPU call. Bigger batches are faster on cards with
/// plenty of video memory (RTX 3080: 64 was about 1.8x faster than 16 for SigLIP B/16); 32 is the
/// default because it fits cards with about 4 GB and is close to the peak on bigger ones. Models
/// with inputs above 256 px use half the setting. If the GPU fails at a batch size (e.g. out of
/// memory), <see cref="ClipModel"/> halves it and retries.
/// </summary>
public static class GpuBatch
{
    public const int Default = 32;
    public static readonly int[] Choices = [8, 16, 32, 64, 128];

    /// <summary>The batch size <paramref name="info"/> uses for the <paramref name="setting"/>.</summary>
    public static int For(ModelInfo info, int setting) =>
        Math.Max(1, info.Image.Size <= 256 ? setting : setting / 2);
}

/// <summary>Where a model will run, and why; <see cref="BatchSize"/> is the GPU batch size preference.</summary>
public sealed record DevicePlan(bool UseGpu, GpuAdapter? Adapter, string Reason, int BatchSize = GpuBatch.Default)
{
    /// <summary>
    /// Decides where <paramref name="info"/>'s image encoder runs (the text encoder always runs on the
    /// CPU: one short query is faster there). The GPU needs a backend on this OS and a hardware
    /// adapter; in auto mode also enough video memory. 8-bit models always use the CPU: DirectML
    /// can't run them (it crashed in testing). A model that crashed the app while loading on the GPU
    /// uses the CPU until GPU mode is set to "on". <see cref="ClipModel"/> still falls back to the CPU
    /// if the GPU fails to load or run the model.
    /// </summary>
    public static DevicePlan Choose(ModelInfo info, string mode, int batchSize = GpuBatch.Default) =>
        ChooseDevice(info, mode) with { BatchSize = batchSize };

    private static DevicePlan ChooseDevice(ModelInfo info, string mode)
    {
        if (mode == GpuModes.Off) return new(false, null, "GPU turned off in Preferences");
        if (IsQuantized(info)) return new(false, null, "8-bit models run on the CPU");
        switch (GpuInfo.Available)
        {
            case GpuInfo.Backend.None:
                return new(false, null, "no GPU support on this operating system");
            case GpuInfo.Backend.CoreML:
                return mode == GpuModes.Auto && GpuCrashGuard.CrashedBefore(info.Id)
                    ? new(false, null, "the GPU crashed the app while loading this model before (set Use GPU to \"On\" to retry)")
                    : new(true, null, "Core ML");
        }
        if (GpuInfo.Best is not { } gpu) return new(false, null, "no DirectX 12 graphics card found");
        if (mode == GpuModes.Auto)
        {
            if (GpuCrashGuard.CrashedBefore(info.Id))
                return new(false, gpu, "the GPU crashed the app while loading this model before (set Use GPU to \"On\" to retry)");
            var needed = RequiredGpuMemory(info);
            if (gpu.DedicatedMemory > 0 && gpu.DedicatedMemory < needed)
                return new(false, gpu, $"{gpu.Name} has {gpu.MemoryText}; this model needs about {needed / 1_073_741_824.0:0.#} GB");
        }
        return new(true, gpu, "DirectML");
    }

    public static bool IsQuantized(ModelInfo info) =>
        info.Files.Vision.Path.Contains("quantized", StringComparison.OrdinalIgnoreCase);

    /// <summary>Image encoder weights plus working memory for a batch of images (generous).</summary>
    public static long RequiredGpuMemory(ModelInfo info) =>
        (long)(info.Files.Vision.Size * 1.5) + 512L * 1024 * 1024;
}

/// <summary>
/// Remembers native crashes while loading a model onto the GPU (they can't be caught): a marker
/// file is written before loading and removed afterwards, so a marker left behind at the next start
/// means that model crashed the app.
/// </summary>
public static class GpuCrashGuard
{
    public static string Dir { get; } = AppPaths.File("gpu-probe");

    private static string Marker(string modelId) => Path.Combine(Dir, modelId + ".loading");

    public static bool CrashedBefore(string modelId) => File.Exists(Marker(modelId));

    public static void Begin(string modelId)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Marker(modelId), DateTime.Now.ToString("O"));
    }

    public static void End(string modelId)
    {
        try { File.Delete(Marker(modelId)); } catch (IOException) { }
    }
}

/// <summary>
/// A CLIP-style dual encoder (OpenAI CLIP, MobileCLIP, SigLIP) on ONNX Runtime: separate text and
/// vision encoders whose outputs are L2-normalized, so the dot product of a text and an image
/// embedding is their cosine similarity. Preprocessing follows each model's Hugging Face processor,
/// as described by its <see cref="ModelInfo"/>.
/// </summary>
public sealed class ClipModel : IDisposable
{
    /// <summary>
    /// ONNX Runtime's Windows builds emit usage telemetry (TraceLogging events that Windows' diagnostic
    /// data service can collect). The app collects nothing, so turn it off before any session exists.
    /// </summary>
    static ClipModel()
    {
        try { OrtEnv.Instance().DisableTelemetryEvents(); }
        catch (Exception) { /* no telemetry on this platform, or ONNX Runtime isn't loadable (reported when a model loads) */ }
    }

    /// <summary>Bump when preprocessing changes, so existing indexes are rebuilt.</summary>
    public const string PreprocessingVersion = "pil-3";

    /// <summary>Decode at least this many times the model's input size (see Preprocess).</summary>
    private const float DecodeOversample = 4f;

    private readonly ModelInfo _info;
    private readonly Action<string>? _log;
    private readonly string _visionPath;
    private DevicePlan _plan;
    private int _gpuBatch;
    private InferenceSession _vision;
    private readonly InferenceSession _text;
    private readonly ITextTokenizer _tokenizer;
    private string _visionInput = "";
    private string _visionOutput = "";
    private readonly string _textOutput;
    private readonly bool _textWantsMask;
    private readonly int? _textFixedLength;

    /// <param name="log">Receives notices such as the GPU batch size being reduced.</param>
    public ClipModel(string modelDir, ModelInfo info, DevicePlan? plan = null, Action<string>? log = null)
    {
        _info = info;
        _log = log;
        plan ??= new DevicePlan(false, null, "CPU requested");
        _plan = plan;
        var visionPath = _visionPath = Path.Combine(modelDir, info.Files.Vision.LocalName);
        var textPath = Path.Combine(modelDir, info.Files.Text.LocalName);
        var tokenizerPath = Path.Combine(modelDir, info.Files.Tokenizer.LocalName);
        _tokenizer = info.Family == ModelInfo.SiglipFamily
            ? new SiglipTokenizer(tokenizerPath, info.Text.MaxLength)
            : new ClipTokenizer(tokenizerPath, info.Text.MaxLength);

        // The text encoder runs on the CPU: a single short query is faster there than on a GPU.
        _text = CreateSession(textPath, gpu: false);
        _textOutput = PickOutput(_text, "text_embeds", "pooler_output");
        _textWantsMask = _text.InputMetadata.ContainsKey("attention_mask");
        // Some exports have a fixed sequence length (e.g. 77); pad to it.
        var dims = _text.InputMetadata["input_ids"].Dimensions;
        _textFixedLength = dims.Length == 2 && dims[1] > 0 ? dims[1] : null;

        DeviceReason = plan.Reason;
        try
        {
            _vision = (plan.UseGpu ? TryCreateGpuVision(visionPath, plan) : null) ?? CreateVision(visionPath, gpu: false, null);
        }
        catch
        {
            _text.Dispose(); // the caller never gets this object, so it can't dispose it
            throw;
        }

        Description = $"vision {_visionInput}{Shape(_vision.InputMetadata[_visionInput].Dimensions)} -> {_visionOutput} on {Device}; " +
                      $"text {string.Join(",", _text.InputMetadata.Keys)}{Shape(dims)} -> {_textOutput} on CPU";
    }

    /// <summary>True if the model runs on the GPU.</summary>
    public bool OnGpu { get; private set; }

    /// <summary>Short device label for status text, e.g. "GPU (NVIDIA GeForce RTX 3080)" or "CPU".</summary>
    public string Device { get; private set; } = "CPU";

    /// <summary>Why this device was chosen (or why the GPU wasn't used).</summary>
    public string DeviceReason { get; private set; }

    /// <summary>
    /// Images per <see cref="EmbedImages"/> call that callers should use. On the GPU it comes from the
    /// GPU batch size preference (<see cref="GpuBatch"/>), and the GPU session is compiled for exactly
    /// this batch (see <see cref="FixedShape"/>); it can shrink if the GPU runs out of memory. The CPU
    /// gains little from bigger batches, and small ones keep progress and cancelling responsive.
    /// </summary>
    public int BatchSize { get; private set; } = CpuBatchSize;

    private const int CpuBatchSize = 16;

    private int GpuBatchSize => _gpuBatch;

    /// <summary>
    /// True if the GPU session has a fixed input shape (BatchSize x 3 x Size x Size); smaller batches
    /// are then padded. With DirectML a fixed shape was up to 2.4x faster (CLIP ViT-B/32: 534 -> 1,283
    /// images/s); it doesn't change the results beyond float rounding.
    /// </summary>
    public bool FixedShape { get; private set; }

    // Names the exported encoders use for their dynamic input dimensions.
    private static readonly string[] BatchDimNames = ["batch_size", "batch"];
    private static readonly string[] ChannelDimNames = ["num_channels", "channels"];
    private static readonly string[] HeightDimNames = ["height"];
    private static readonly string[] WidthDimNames = ["width"];

    private static InferenceSession CreateSession(string path, bool gpu, GpuAdapter? adapter = null, (int Batch, int Size)? fixedShape = null)
    {
        var o = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR, // e.g. "some nodes were not assigned to the GPU"
        };
        if (fixedShape is { } shape)
        {
            // Names that don't occur in the model are ignored.
            foreach (var n in BatchDimNames) o.AddFreeDimensionOverrideByName(n, shape.Batch);
            foreach (var n in ChannelDimNames) o.AddFreeDimensionOverrideByName(n, 3);
            foreach (var n in HeightDimNames) o.AddFreeDimensionOverrideByName(n, shape.Size);
            foreach (var n in WidthDimNames) o.AddFreeDimensionOverrideByName(n, shape.Size);
        }
        if (gpu)
        {
            if (GpuInfo.Available == GpuInfo.Backend.CoreML)
            {
                o.AppendExecutionProvider_CoreML();
            }
            else
            {
                // DirectML requires these (see ONNX Runtime's DirectML execution provider docs).
                o.EnableMemoryPattern = false;
                o.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                o.AppendExecutionProvider_DML(adapter?.Index ?? 0);
            }
        }
        return new InferenceSession(path, o);
    }

    private InferenceSession CreateVision(string path, bool gpu, GpuAdapter? adapter)
    {
        var session = CreateSession(path, gpu, adapter, gpu ? (GpuBatchSize, _info.Image.Size) : null);
        _visionInput = session.InputMetadata.Keys.First();
        _visionOutput = PickOutput(session, "image_embeds", "pooler_output");
        BatchSize = gpu ? GpuBatchSize : CpuBatchSize;
        // The override applies if the batch dimension has one of the names above (the metadata may still report it as dynamic).
        var meta = session.InputMetadata[_visionInput];
        FixedShape = gpu && (meta.Dimensions is [var b, ..] && b == GpuBatchSize
                             || meta.SymbolicDimensions is [var batchName, ..] && BatchDimNames.Contains(batchName));
        return session;
    }

    /// <summary>
    /// Loads the image encoder on the GPU and runs a test image through it. Returns null (after
    /// recording why in <see cref="DeviceReason"/>) if that fails, so the caller uses the CPU.
    /// </summary>
    private InferenceSession? TryCreateGpuVision(string visionPath, DevicePlan plan)
    {
        _gpuBatch = GpuBatch.For(_info, plan.BatchSize);
        GpuCrashGuard.Begin(_info.Id);
        try
        {
            while (true)
            {
                InferenceSession? session = null;
                try
                {
                    session = CreateVision(visionPath, gpu: true, plan.Adapter);
                    _vision = session;
                    EmbedImages([new float[3 * _info.Image.Size * _info.Image.Size]]); // a full (padded) batch
                    OnGpu = true;
                    Device = plan.Adapter is { } a ? $"GPU ({a.Name})" : $"GPU ({plan.Reason})";
                    DeviceReason = plan.Adapter is { } adapter ? $"{adapter} via {plan.Reason}" : plan.Reason;
                    return session;
                }
                catch (Exception e) when (_gpuBatch > 1)
                {
                    // Often the batch doesn't fit in video memory: try half.
                    session?.Dispose();
                    _log?.Invoke($"{_info.Name}: the GPU couldn't run a batch of {_gpuBatch} ({FirstLine(e)}); trying {_gpuBatch / 2}");
                    _gpuBatch /= 2;
                }
                catch (Exception e)
                {
                    session?.Dispose();
                    DeviceReason = $"the GPU couldn't run this model ({e.GetType().Name}: {FirstLine(e)})";
                    return null;
                }
            }
        }
        finally
        {
            GpuCrashGuard.End(_info.Id);
        }
    }

    private static string FirstLine(Exception e) => e.Message.Split('\n')[0];

    /// <summary>Recreates the GPU session with half the batch size, after a batch failed.</summary>
    private void ReduceGpuBatch(Exception e)
    {
        var old = _gpuBatch;
        _gpuBatch = Math.Max(1, _gpuBatch / 2);
        _log?.Invoke($"{_info.Name}: the GPU failed on a batch of {old} ({FirstLine(e)}); continuing with batches of {_gpuBatch}");
        _vision.Dispose();
        _vision = CreateVision(_visionPath, gpu: true, _plan.Adapter);
    }

    /// <summary>Input/output names and shapes, for the debug log.</summary>
    public string Description { get; }

    private static string Shape(int[] dims) => "[" + string.Join("x", dims.Select(d => d < 0 ? "?" : d.ToString())) + "]";

    private static string PickOutput(InferenceSession s, params string[] preferred) =>
        preferred.FirstOrDefault(s.OutputMetadata.ContainsKey)
        ?? s.OutputMetadata.FirstOrDefault(o => o.Value.Dimensions.Length == 2).Key
        ?? s.OutputMetadata.Keys.First();

    public float[] EmbedText(string text)
    {
        var ids = _tokenizer.Encode(text).ToList();
        var padTo = _textFixedLength ?? _info.Text.PadTo;
        var realLength = ids.Count;
        if (padTo is { } p)
            while (ids.Count < p) ids.Add(_info.Text.PadId);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids.ToArray(), [1, ids.Count])),
        };
        if (_textWantsMask)
        {
            var mask = Enumerable.Range(0, ids.Count).Select(i => i < realLength ? 1L : 0L).ToArray();
            inputs.Add(NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, [1, ids.Count])));
        }
        using var results = _text.Run(inputs, [_textOutput]);
        return Normalize(results[0].AsEnumerable<float>().ToArray());
    }

    /// <summary>
    /// Embeds preprocessed images (see <see cref="Preprocess"/>), one row per image. Pass up to
    /// <see cref="BatchSize"/> images at a time; more are split into batches.
    /// </summary>
    public float[][] EmbedImages(IReadOnlyList<float[]> pixels)
    {
        if (pixels.Count > BatchSize)
            return pixels.Chunk(BatchSize).SelectMany(EmbedImages).ToArray();
        var size = _info.Image.Size;
        var per = 3 * size * size;
        var rows = FixedShape ? BatchSize : pixels.Count; // a fixed-shape session needs a full batch: pad with zeros
        var batch = new float[rows * per];
        for (var i = 0; i < pixels.Count; i++)
            Array.Copy(pixels[i], 0, batch, i * per, per);
        var input = NamedOnnxValue.CreateFromTensor(_visionInput, new DenseTensor<float>(batch, [rows, 3, size, size]));
        float[] flat;
        try
        {
            using var results = _vision!.Run([input], [_visionOutput]);
            flat = results[0].AsEnumerable<float>().ToArray();
        }
        catch (OnnxRuntimeException e) when (OnGpu && BatchSize > 1)
        {
            ReduceGpuBatch(e);
            return EmbedImages(pixels); // splits into the smaller batches
        }
        var dim = flat.Length / rows;
        return Enumerable.Range(0, pixels.Count).Select(i => Normalize(flat.AsSpan(i * dim, dim).ToArray())).ToArray();
    }

    /// <summary>
    /// The model's image preprocessing, as in its Hugging Face image processor:
    /// "shortest_edge" resizes the short side to Size and center-crops Size x Size (CLIP, MobileCLIP);
    /// "exact" resizes to Size x Size (SigLIP). Pillow-compatible filtering, then /255, normalize, CHW.
    /// Returns null for files Skia can't decode.
    /// </summary>
    public float[]? Preprocess(string path)
    {
        using var codec = SKCodec.Create(path);
        return codec is null ? null : Preprocess(codec);
    }

    private float[]? Preprocess(SKCodec codec)
    {
        var spec = _info.Image;
        var size = spec.Size;

        // JPEG can decode at 1/2, 1/4, 1/8 scale for free; staying at least DecodeOversample x the
        // target keeps the result close to decoding at full size.
        var info = codec.Info;
        var shortSide = Math.Min(info.Width, info.Height);
        var decodeSize = codec.GetScaledDimensions(Math.Min(1f, size * DecodeOversample / shortSide));
        using var gate = DecodeLimits.Enter(decodeSize.Width, decodeSize.Height);
        if (gate is null) return null; // too big to decode safely: counted as unreadable
        using var decoded = new SKBitmap(new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var r = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (r is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;
        var bitmap = ApplyOrientation(decoded, codec.EncodedOrigin);

        try
        {
            var (w, h) = (bitmap.Width, bitmap.Height);
            var (rw, rh) = spec.Resize == "exact" ? (size, size)
                : w <= h ? (size, Math.Max(size, (int)((long)size * h / w)))
                : (Math.Max(size, (int)((long)size * w / h)), size);

            var filter = spec.Resample == "bilinear" ? PillowResize.Filter.Bilinear : PillowResize.Filter.Bicubic;
            var resized = PillowResize.Resize(bitmap.Bytes, w, h, rw, rh, filter); // RGBA, unpremultiplied
            var left = (rw - size) / 2;
            var top = (rh - size) / 2;

            var result = new float[3 * size * size];
            var plane = size * size;
            var (m, s) = (spec.Mean, spec.Std);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var i = ((top + y) * rw + left + x) * 4;
                    var o = y * size + x;
                    result[o] = (resized[i] / 255f - m[0]) / s[0];
                    result[plane + o] = (resized[i + 1] / 255f - m[1]) / s[1];
                    result[2 * plane + o] = (resized[i + 2] / 255f - m[2]) / s[2];
                }
            }
            return result;
        }
        finally
        {
            if (!ReferenceEquals(bitmap, decoded)) bitmap.Dispose();
        }
    }

    /// <summary>
    /// Rotates/flips per EXIF so portrait photos aren't embedded (or shown) sideways. Returns <paramref name="bitmap"/> turned upright per the EXIF origin (a new bitmap, or the same one if it's already upright).
    /// </summary>
    public static SKBitmap ApplyOrientation(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft) return bitmap;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (w, h) = swap ? (bitmap.Height, bitmap.Width) : (bitmap.Width, bitmap.Height);
        var rotated = new SKBitmap(bitmap.Info.WithSize(w, h)); // same pixel format as the input
        using var canvas = new SKCanvas(rotated);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Scale(-1, 1, w / 2f, 0); break;
            case SKEncodedOrigin.BottomRight: canvas.RotateDegrees(180, w / 2f, h / 2f); break;
            case SKEncodedOrigin.BottomLeft: canvas.Scale(1, -1, 0, h / 2f); break;
            case SKEncodedOrigin.LeftTop: canvas.Translate(w, 0); canvas.RotateDegrees(90); canvas.Scale(1, -1, 0, bitmap.Height / 2f); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(w, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(0, h); canvas.RotateDegrees(-90); canvas.Scale(1, -1, 0, bitmap.Height / 2f); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, h); canvas.RotateDegrees(-90); break;
        }
        canvas.DrawBitmap(bitmap, 0, 0);
        return rotated;
    }

    private static float[] Normalize(float[] v)
    {
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 0)
            for (var i = 0; i < v.Length; i++) v[i] /= norm;
        return v;
    }

    public void Dispose()
    {
        _vision.Dispose();
        _text.Dispose();
    }
}
