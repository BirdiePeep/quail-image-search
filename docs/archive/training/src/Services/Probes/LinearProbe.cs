using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace QuailImageSearch.Services.Probes;

/// <summary>How well one tag's probe did on the held-out images.</summary>
public sealed record TagStats(string Tag, int TrainPositives, int ValidationPositives, double AveragePrecision);

/// <summary>Everything about a probe except its weights (stored as JSON at the start of the file).</summary>
public sealed record ProbeHeader(
    string ModelId,
    string ModelVersion,     // the model's embedding version; the probe only applies to embeddings from it
    int Dim,
    string Dataset,
    int TrainImages,
    int ValidationImages,
    int MinExamples,
    int Epochs,
    double MeanAveragePrecision,
    DateTime TrainedAt,
    List<TagStats> Tags);

/// <summary>
/// One logistic-regression classifier per tag on top of a model's frozen image embeddings
/// (a "linear probe"): p(tag | image) = sigmoid(w_tag · standardize(embedding) + b_tag).
/// Embeddings are standardized per dimension with the training set's mean and standard deviation.
/// </summary>
public sealed class LinearProbe
{
    private const string Magic = "ISPROBE1";

    public LinearProbe(ProbeHeader header, float[] mean, float[] invStd, float[] weights, float[] bias)
    {
        Header = header;
        Mean = mean;
        InvStd = invStd;
        Weights = weights;
        Bias = bias;
        _tagIndex = header.Tags.Select((t, i) => (t.Tag, i)).ToDictionary(x => x.Tag, x => x.i, StringComparer.Ordinal);
    }

    private readonly Dictionary<string, int> _tagIndex;

    public ProbeHeader Header { get; }
    public int Dim => Header.Dim;
    public int TagCount => Header.Tags.Count;

    /// <summary>Per-dimension mean and 1/std of the training embeddings.</summary>
    public float[] Mean { get; }
    public float[] InvStd { get; }

    /// <summary>Tag-major: row t (Dim values) is tag t's weight vector.</summary>
    public float[] Weights { get; }
    public float[] Bias { get; }

    public IReadOnlyList<TagStats> Tags => Header.Tags;

    public int IndexOf(string tag) => _tagIndex.GetValueOrDefault(tag, -1);

    /// <summary>Standardizes an embedding into <paramref name="destination"/> (Dim values).</summary>
    public void Standardize(ReadOnlySpan<float> embedding, Span<float> destination)
    {
        TensorPrimitives.Subtract(embedding, Mean, destination);
        TensorPrimitives.Multiply(destination, InvStd, destination);
    }

    /// <summary>Probability of tag <paramref name="tag"/> for an already standardized embedding.</summary>
    public float Probability(ReadOnlySpan<float> standardized, int tag) =>
        Sigmoid(TensorPrimitives.Dot(standardized, Weights.AsSpan(tag * Dim, Dim)) + Bias[tag]);

    public static float Sigmoid(float z) => 1f / (1f + MathF.Exp(-z));

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        using (var f = new BinaryWriter(File.Create(tmp)))
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(Header);
            f.Write(Encoding.ASCII.GetBytes(Magic));
            f.Write(json.Length);
            f.Write(json);
            foreach (var a in new[] { Mean, InvStd, Weights, Bias })
                f.Write(MemoryMarshal.AsBytes(a.AsSpan()));
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static ProbeHeader ReadHeader(string path)
    {
        using var f = new BinaryReader(File.OpenRead(path));
        return ReadHeader(f);
    }

    private static ProbeHeader ReadHeader(BinaryReader f)
    {
        if (Encoding.ASCII.GetString(f.ReadBytes(Magic.Length)) != Magic) throw new InvalidDataException("not a tag probe file");
        var json = f.ReadBytes(f.ReadInt32());
        return JsonSerializer.Deserialize<ProbeHeader>(json) ?? throw new InvalidDataException("empty probe header");
    }

    public static LinearProbe Load(string path)
    {
        using var f = new BinaryReader(File.OpenRead(path));
        var header = ReadHeader(f);
        float[] Read(int n)
        {
            var a = new float[n];
            f.BaseStream.ReadExactly(MemoryMarshal.AsBytes(a.AsSpan()));
            return a;
        }
        var d = header.Dim;
        var t = header.Tags.Count;
        return new LinearProbe(header, Read(d), Read(d), Read(t * d), Read(t));
    }
}
