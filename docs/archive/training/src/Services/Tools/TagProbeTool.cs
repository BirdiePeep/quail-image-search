using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuailImageSearch.Services.Models;
using QuailImageSearch.Services.Probes;

namespace QuailImageSearch.Services;

/// <summary>
/// Searches by tags with a model's trained <see cref="LinearProbe"/>: the query is a list of tags
/// (e.g. <c>species:wolf black_fur -rating:explicit</c>; a leading '-' excludes a tag), and an image's
/// score is the lowest of its per-term probabilities (p for a tag, 1 − p for an excluded one), so
/// every term has to match. Scores are probabilities in percent, so they need no calibration.
/// Indexing and the embedding store are shared with the model's <see cref="EmbeddingModelTool"/>.
/// </summary>
public sealed class TagProbeTool : ISearchTool
{
    public const string IdPrefix = "probe:";

    private readonly EmbeddingModelTool _model;
    private readonly ProbeStore _store;
    private readonly DebugLog _log;

    public TagProbeTool(EmbeddingModelTool model, ProbeStore store, DebugLog log)
    {
        _model = model;
        _store = store;
        _log = log;
    }

    public ModelInfo Info => _model.Info;
    public string Id => IdPrefix + _model.Info.Id;
    public string Name => $"{_model.Info.Name} · tags";
    public string ScoreVersion => _store.HeaderFor(_model.Info.Id)?.TrainedAt.ToString("O") ?? "none";
    public ScoreScale? FixedScale { get; } = new(0, 100);
    public string? DeviceSummary => _model.DeviceSummary;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        var probe = await Task.Run(() => _store.Get(_model.Info.Id), ct);
        if (probe.Header.ModelVersion != EmbeddingModelTool.EmbeddingVersion(_model.Info))
            throw new InvalidOperationException($"the tag probe was trained with an older version of {_model.Info.Name}; train it again in Preferences > Training");
        var terms = Parse(probe, request.Query);
        _log.Write($"tag query: {string.Join(" AND ", terms.Select(t => (t.Exclude ? "NOT " : "") + $"{probe.Tags[t.Tag].Tag} (AP {probe.Tags[t.Tag].AveragePrecision:0.00})"))}");

        return await _model.SearchAsync(request, Name, _ => (probe.Dim, embedding =>
        {
            Span<float> x = stackalloc float[probe.Dim];
            probe.Standardize(embedding, x);
            var score = 1f;
            foreach (var (tag, exclude) in terms)
            {
                var p = probe.Probability(x, tag);
                score = Math.Min(score, exclude ? 1 - p : p);
            }
            return score * 100;
        }), progress, ct);
    }

    /// <summary>
    /// Splits the query into tags (separated by spaces or commas) and finds each in the probe's
    /// vocabulary: exactly, ignoring case, or without its category prefix ("wolf" finds "species:wolf").
    /// </summary>
    public static List<(int Tag, bool Exclude)> Parse(LinearProbe probe, string query)
    {
        var terms = new List<(int, bool)>();
        foreach (var raw in query.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var exclude = raw.StartsWith('-') && raw.Length > 1;
            var token = exclude ? raw[1..] : raw;
            var index = Find(probe, token);
            if (index < 0)
            {
                var similar = probe.Tags.Where(t => t.Tag.Contains(token, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(t => t.TrainPositives).Take(8).Select(t => t.Tag).ToList();
                throw new InvalidOperationException($"\"{token}\" isn't one of the {probe.TagCount:N0} trained tags" +
                    (similar.Count > 0 ? $"; did you mean: {string.Join(", ", similar)}?" : ". Tags use underscores, e.g. black_fur."));
            }
            terms.Add((index, exclude));
        }
        if (terms.Count == 0) throw new InvalidOperationException("type one or more tags, e.g. \"species:wolf black_fur\"");
        return terms;
    }

    private static int Find(LinearProbe probe, string token)
    {
        var exact = probe.IndexOf(token);
        if (exact >= 0) return exact;
        var matches = probe.Tags.Select((t, i) => (t.Tag, i))
            .Where(t => string.Equals(t.Tag, token, StringComparison.OrdinalIgnoreCase)
                        || t.Tag.EndsWith(":" + token, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0].i : -1;
    }

    public void Dispose() { } // the model tool owns the model and index
}
