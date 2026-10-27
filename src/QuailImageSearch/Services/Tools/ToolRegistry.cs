using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using QuailImageSearch.Services.Models;

namespace QuailImageSearch.Services;

/// <summary>
/// The search tools available to tabs: one tool per downloaded model, and the command-line tools
/// defined in preferences. To add a built-in adapter, implement
/// <see cref="ISearchTool"/> and register it here.
/// </summary>
public sealed class ToolRegistry : IDisposable
{
    private readonly DebugLog _log;
    private readonly ModelManager _models;
    private decimal _defaultMinScore;
    private string _gpuMode;
    private int _gpuBatch;

    public ToolRegistry(DebugLog log, Preferences prefs, ModelManager models, ScoreCalibrator calibrator)
    {
        _log = log;
        _models = models;
        Calibrator = calibrator;
        _gpuMode = prefs.UseGpu;
        _gpuBatch = prefs.GpuBatchSize;
        SyncModelTools();
        SyncCustomTools(prefs);
        DefaultToolId = prefs.DefaultToolId;
        _defaultMinScore = prefs.DefaultMinScore;
        _models.Uninstalling += info => RemoveTool(EmbeddingModelTool.IdPrefix + info.Id);
        _models.ReleaseIndex = modelId =>
            Find(EmbeddingModelTool.IdPrefix + modelId) is not EmbeddingModelTool tool || tool.TryCloseIndex();
        _models.InstalledChanged += () =>
        {
            SyncModelTools();
            ToolsChanged?.Invoke();
        };
    }

    /// <summary>Bound to the tool dropdowns. Items are updated in place when preferences change.</summary>
    public ObservableCollection<ISearchTool> Tools { get; } = new();

    /// <summary>Per-tool score calibration (raw score -> 0–100).</summary>
    public ScoreCalibrator Calibrator { get; }

    public string DefaultToolId { get; private set; }

    /// <summary>Raised (UI thread) after tools were added or removed outside of <see cref="Apply"/>.</summary>
    public event Action? ToolsChanged;

    public ISearchTool? Find(string? id) => Tools.FirstOrDefault(t => t.Id == id);

    /// <summary>The tool for <paramref name="id"/>, else the default tool, else the first tool; null if there are none.</summary>
    public ISearchTool? Resolve(string? id) =>
        Find(id) ?? Find(DefaultToolId) ?? Find(EmbeddingModelTool.IdPrefix + ModelCatalog.RecommendedId) ?? Tools.FirstOrDefault();

    /// <summary>
    /// Min score a tab starts with when it uses <paramref name="tool"/>: the shared 0–100 default for
    /// calibrated (or auto-calibrated) tools, 0 for tools whose raw scores are shown as-is.
    /// </summary>
    public decimal DefaultMinScoreFor(ISearchTool? tool) =>
        tool is null || tool.AutoCalibrate || Calibrator.IsCalibrated(tool) ? _defaultMinScore : 0;

    /// <summary>The raw-to-0–100 scale for the tool with this id, or null if it has none (raw scores shown).</summary>
    public ScoreScale? ScaleFor(string? toolId) => Find(toolId) is { } tool ? Calibrator.ScaleFor(tool) : null;

    public void Apply(Preferences prefs)
    {
        SyncCustomTools(prefs);
        _gpuMode = prefs.UseGpu;
        _gpuBatch = prefs.GpuBatchSize;
        foreach (var model in Tools.OfType<EmbeddingModelTool>())
            model.SetGpuSettings(_gpuMode, _gpuBatch); // reloads on the next search if they changed
        DefaultToolId = Resolve(prefs.DefaultToolId)?.Id ?? prefs.DefaultToolId;
        _defaultMinScore = prefs.DefaultMinScore;
    }

    private void SyncModelTools()
    {
        var installed = _models.Installed.Select(e => e.Info).ToList();
        foreach (var stale in Tools.OfType<EmbeddingModelTool>().Where(t => installed.All(m => m.Id != t.Info.Id)).ToList())
            RemoveTool(stale.Id);
        var insertAt = 0; // models first, then command-line tools
        foreach (var info in installed)
        {
            if (Find(EmbeddingModelTool.IdPrefix + info.Id) is null)
                Tools.Insert(insertAt, new EmbeddingModelTool(info, _log, _gpuMode, _gpuBatch));
            insertAt++;
        }
    }

    private void RemoveTool(string id)
    {
        if (Find(id) is not { } tool) return;
        Tools.Remove(tool);
        tool.Dispose();
    }

    private void SyncCustomTools(Preferences prefs)
    {
        // While command-line tools are switched off (Features), saved ones aren't offered as search tools;
        // they stay in preferences.json for when the feature comes back.
        var defs = !Features.CommandLineTools ? [] : prefs.CustomTools
            .Where(d => !string.IsNullOrWhiteSpace(d.Id) && !d.Id.StartsWith(EmbeddingModelTool.IdPrefix))
            .GroupBy(d => d.Id).Select(g => g.First()).ToList();

        foreach (var stale in Tools.OfType<CommandLineTool>().Where(t => defs.All(d => d.Id != t.Id)).ToList())
        {
            Tools.Remove(stale);
            stale.Dispose();
        }
        foreach (var def in defs)
        {
            try
            {
                if (Tools.OfType<CommandLineTool>().FirstOrDefault(t => t.Id == def.Id) is { } existing)
                    existing.Update(def);
                else
                    Tools.Add(new CommandLineTool(_log, def));
            }
            catch (ArgumentException e)
            {
                _log.Write($"tool \"{def.Name}\" skipped: invalid output pattern ({e.Message})");
            }
        }
    }

    /// <summary>Removes deleted files from every installed model's index, in the background.</summary>
    public void ForgetFiles(IReadOnlyList<string> paths)
    {
        foreach (var model in Tools.OfType<EmbeddingModelTool>())
            _ = model.ForgetFilesAsync(paths);
    }

    public void Dispose()
    {
        foreach (var t in Tools) t.Dispose();
    }
}
