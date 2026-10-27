using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QuailImageSearch.Services;

/// <summary>
/// Linear map from a tool's raw scores to 0–100: <see cref="Low"/> (a typical unrelated
/// image/caption pair) becomes 0 and <see cref="High"/> (a strong match) becomes 100, clamped.
/// </summary>
public sealed record ScoreScale(double Low, double High)
{
    public double ToDisplay(double raw) =>
        High > Low ? Math.Clamp((raw - Low) / (High - Low) * 100, 0, 100) : raw;
}

/// <summary>A tool's stored calibration.</summary>
public sealed record CalibrationRecord(double Low, double High, string Version, int Images, DateTime CalibratedAt);

/// <summary>Calibration status of one tool, bound to Preferences.</summary>
public sealed partial class CalibrationState(string toolId) : ObservableObject
{
    public string ToolId { get; } = toolId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial CalibrationRecord? Record { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string Progress { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? Error { get; set; }

    public string StatusText =>
        IsRunning ? $"Calibrating scores… {Progress}"
        : Error is not null ? $"Calibration failed: {Error}"
        : Record is { } r ? $"Scores calibrated: raw {r.Low:0.000} → 0, raw {r.High:0.000} → 100 ({r.Images} images, {r.CalibratedAt:yyyy-MM-dd})"
        : "Scores not calibrated yet (shown raw)";
}

/// <summary>
/// Calibrates tools' scores on the calibration set shipped with the app (Calibration/images plus
/// manifest.json: public-domain/CC0 photos with captions). Each caption is searched through the
/// tool like a normal search, so it works for any tool: built-in models and command-line
/// tools. 0 = median score of images against other images' captions, 100 = 90th percentile of
/// images against their own caption. Results are stored in calibration.json in app data.
/// </summary>
public sealed class ScoreCalibrator
{
    internal const double LowPercentileUnmatched = 50;
    private const double HighPercentileMatched = 90;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly DebugLog _log;
    private readonly SemaphoreSlim _queue = new(1, 1); // one calibration at a time
    private readonly Dictionary<string, CalibrationRecord> _records;
    private readonly Dictionary<string, CalibrationState> _states = new();

    public ScoreCalibrator(DebugLog log)
    {
        _log = log;
        _records = Load();
    }

    public static string CalibrationDir => Path.Combine(AppContext.BaseDirectory, "Calibration");
    public static string ImagesDir => Path.Combine(CalibrationDir, "images");

    public static string StorePath { get; } = AppPaths.File("calibration.json");

    /// <summary>Raised on the UI thread when a tool's calibration changes.</summary>
    public event Action<string>? Changed;

    public CalibrationState StateFor(string toolId)
    {
        if (!_states.TryGetValue(toolId, out var state))
        {
            state = new CalibrationState(toolId) { Record = _records.GetValueOrDefault(toolId) };
            _states[toolId] = state;
        }
        return state;
    }

    /// <summary>The tool's current scale, or null if it isn't calibrated (for its current version).</summary>
    public ScoreScale? ScaleFor(ISearchTool tool) =>
        _records.TryGetValue(tool.Id, out var r) && r.Version == tool.ScoreVersion ? new ScoreScale(r.Low, r.High) : null;

    public bool IsCalibrated(ISearchTool tool) => ScaleFor(tool) is not null;

    /// <summary>Calibrates the tool if it has no calibration for its current version.</summary>
    public Task EnsureCalibratedAsync(ISearchTool tool, CancellationToken ct = default) =>
        IsCalibrated(tool) ? Task.CompletedTask : CalibrateAsync(tool, ct);

    public async Task CalibrateAsync(ISearchTool tool, CancellationToken ct = default)
    {
        var state = StateFor(tool.Id);
        await _queue.WaitAsync(ct);
        try
        {
            state.IsRunning = true;
            state.Error = null;
            state.Progress = "starting";
            var record = await Task.Run(() => RunAsync(tool, state, ct), ct);
            _records[tool.Id] = record;
            Save();
            state.Record = record;
            _log.Write($"calibrated {tool.Name}: raw {record.Low:0.0000} -> 0, raw {record.High:0.0000} -> 100 ({record.Images} images)");
            Changed?.Invoke(tool.Id);
        }
        catch (OperationCanceledException)
        {
            state.Error = "cancelled";
        }
        catch (Exception e)
        {
            state.Error = e.Message;
            _log.Write($"calibration of {tool.Name} failed: {e.Message}");
        }
        finally
        {
            state.IsRunning = false;
            _queue.Release();
        }
    }

    private async Task<CalibrationRecord> RunAsync(ISearchTool tool, CalibrationState state, CancellationToken ct)
    {
        var manifest = LoadManifest();
        var images = manifest.Select(m => Path.GetFullPath(Path.Combine(ImagesDir, m.File))).ToList();
        var index = images.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i, StringComparer.OrdinalIgnoreCase);
        var matched = new List<double>();
        var unmatched = new List<double>();
        var quiet = new Progress<string>(_ => { });

        for (var q = 0; q < manifest.Count; q++)
        {
            ct.ThrowIfCancellationRequested();
            Report(state, $"{q + 1}/{manifest.Count}");
            var results = await tool.SearchAsync(new SearchRequest(manifest[q].Caption, ImagesDir, images.Count), quiet, ct);
            foreach (var r in results)
            {
                if (!index.TryGetValue(Path.GetFullPath(r.Path), out var i)) continue;
                (i == q ? matched : unmatched).Add(r.Score);
            }
        }
        if (matched.Count < manifest.Count / 2 || unmatched.Count < manifest.Count)
            throw new InvalidOperationException($"the tool returned too few calibration results ({matched.Count} matches)");

        var low = Percentile(unmatched, LowPercentileUnmatched);
        var high = Percentile(matched, HighPercentileMatched);
        if (high <= low) throw new InvalidOperationException($"scores don't separate matches from non-matches (low {low:0.000}, high {high:0.000})");
        return new CalibrationRecord(low, high, tool.ScoreVersion, manifest.Count, DateTime.Now);
    }

    private static void Report(CalibrationState state, string text) => Dispatcher.UIThread.Post(() => state.Progress = text);

    internal static double Percentile(List<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var rank = p / 100 * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    public sealed record ManifestImage(string File, string Caption);

    private sealed record Manifest(List<ManifestImage> Images);

    public static List<ManifestImage> LoadManifest()
    {
        var path = Path.Combine(CalibrationDir, "manifest.json");
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                       ?? throw new InvalidDataException("empty calibration manifest");
        return manifest.Images;
    }

    private static Dictionary<string, CalibrationRecord> Load()
    {
        try
        {
            return File.Exists(StorePath)
                ? JsonSerializer.Deserialize<Dictionary<string, CalibrationRecord>>(File.ReadAllText(StorePath), Json) ?? new()
                : new();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return new();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        var tmp = StorePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_records, Json));
        File.Move(tmp, StorePath, overwrite: true);
    }
}
