using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace QuailImageSearch.Services;

public sealed record SearchRequest(string Query, string Folder, int MaxResults);

/// <summary>One result: a file and the tool's raw score.</summary>
public sealed record SearchResult(string Path, double Score);

/// <summary>
/// Adapter between the app and one search utility: translates a <see cref="SearchRequest"/> into
/// whatever the tool expects and its output back into scored file paths. Adapters write what they
/// ran and the tool's raw output to the <see cref="DebugLog"/>.
/// Scores are the tool's raw scores; the app rescales them to 0–100 using the tool's calibration
/// (see <see cref="ScoreCalibrator"/>).
/// </summary>
public interface ISearchTool : IDisposable
{
    /// <summary>Stable id stored in session.json / preferences.json.</summary>
    string Id { get; }

    /// <summary>Name shown in the tool dropdowns.</summary>
    string Name { get; }

    /// <summary>Identifies how the tool scores; a stored calibration is only used while this matches.</summary>
    string ScoreVersion => "1";

    /// <summary>True if the app calibrates this tool's scores automatically (built-in tools).</summary>
    bool AutoCalibrate => false;

    /// <summary>Where the tool computes, for status text (e.g. "GPU (NVIDIA …)"); null if not applicable.</summary>
    string? DeviceSummary => null;

    /// <param name="progress">Optional short status updates (e.g. indexing progress) for the tab.</param>
    Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, IProgress<string>? progress, CancellationToken ct);
}
