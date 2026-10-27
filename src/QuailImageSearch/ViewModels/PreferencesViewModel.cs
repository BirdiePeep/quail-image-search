using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuailImageSearch.Services;
using QuailImageSearch.Services.Models;

namespace QuailImageSearch.ViewModels;

public sealed record ToolOption(string Id, string Name);

public sealed record GpuModeOption(string Mode, string Label);

/// <summary>Editable copy of <see cref="Services.Preferences"/> for the preferences window.</summary>
public partial class PreferencesViewModel : ViewModelBase
{
    private readonly ModelManager _models;

    private readonly ToolRegistry _tools;
    private readonly Preferences _prefs;

    public PreferencesViewModel(Preferences prefs, ModelManager models, ToolRegistry tools)
    {
        _models = models;
        _tools = tools;
        _prefs = prefs;
        foreach (var e in models.Entries)
            e.Calibration = tools.Calibrator.StateFor(EmbeddingModelTool.IdPrefix + e.Info.Id);
        _models.InstalledChanged += RebuildToolOptions;
        _models.InstalledChanged += RebuildModelGroups;
        RebuildModelGroups();
        RefreshIndexSizes(); // sizes change as searches index folders, so measure when the window opens
        DefaultMaxResults = prefs.DefaultMaxResults;
        DefaultMinScore = prefs.DefaultMinScore;
        GpuMode = GpuModes.All.Contains(prefs.UseGpu) ? prefs.UseGpu : GpuModes.Auto;
        GpuBatchSize = GpuBatch.Choices.Contains(prefs.GpuBatchSize) ? prefs.GpuBatchSize : GpuBatch.Default;
        Theme = Appearance.Themes.Any(o => o.Value == prefs.Theme) ? prefs.Theme : Appearance.Fluent;
        ThemeMode = Appearance.Modes.Any(o => o.Value == prefs.ThemeMode) ? prefs.ThemeMode : Appearance.System;
        ThemeDensity = Appearance.Densities.Any(o => o.Value == prefs.ThemeDensity) ? prefs.ThemeDensity : Appearance.Normal;
        foreach (var def in prefs.CustomTools)
            AddCustomTool(new CustomToolViewModel(def));
        SelectedCustomTool = CustomTools.FirstOrDefault();
        RebuildToolOptions();
        // The saved default if it's offered; otherwise RebuildToolOptions' fallback (the recommended model).
        if (ToolOptions.Any(o => o.Id == prefs.DefaultToolId)) DefaultToolId = prefs.DefaultToolId;
    }

    /// <summary>Call when the window closes.</summary>
    public void Detach()
    {
        _models.InstalledChanged -= RebuildToolOptions;
        _models.InstalledChanged -= RebuildModelGroups;
    }

    // ---- Models ----

    /// <summary>Installed models, shown first (in model-list order).</summary>
    public ObservableCollection<ModelEntry> InstalledModels { get; } = new();

    /// <summary>Models that aren't installed, shown below a divider.</summary>
    public ObservableCollection<ModelEntry> AvailableModels { get; } = new();

    public bool ShowModelDivider => InstalledModels.Count > 0 && AvailableModels.Count > 0;

    /// <summary>Re-sorts the model cards after a model is downloaded or deleted.</summary>
    private void RebuildModelGroups()
    {
        InstalledModels.Clear();
        AvailableModels.Clear();
        foreach (var e in _models.Entries)
            (e.IsInstalled ? InstalledModels : AvailableModels).Add(e);
        OnPropertyChanged(nameof(ShowModelDivider));
    }

    // ---- Score calibration ----

    [RelayCommand]
    private Task RecalibrateModel(ModelEntry entry) =>
        _tools.Find(EmbeddingModelTool.IdPrefix + entry.Info.Id) is { } tool ? _tools.Calibrator.CalibrateAsync(tool) : Task.CompletedTask;

    /// <summary>Calibrates the saved version of a command-line tool (unsaved edits aren't used).</summary>
    [RelayCommand]
    private async Task CalibrateCustomTool(CustomToolViewModel tool)
    {
        if (_tools.Find(tool.Id) is not CommandLineTool saved)
        {
            tool.Calibration.Error = "save the tool first (Save closes this window; reopen Preferences to calibrate)";
            return;
        }
        await _tools.Calibrator.CalibrateAsync(saved);
    }

    public string CatalogSource => $"{ModelCatalog.Source}, {ModelCatalog.All.Count} models";

    // ---- Search indexes ----

    /// <summary>Set by the window: asks the user to confirm (message) and returns their answer.</summary>
    public Func<string, Task<bool>>? Confirm { get; set; }

    /// <summary>
    /// Set by the window: asks to confirm (title, message, confirm button, checkbox text or null,
    /// checkbox default) and returns (confirmed, checkbox value).
    /// </summary>
    public Func<string, string, string, string?, bool, Task<(bool Confirmed, bool Option)>>? ConfirmWithOption { get; set; }

    /// <summary>Indexes whose model isn't installed (deleted, or no longer in the model list).</summary>
    public ObservableCollection<DanglingIndex> DanglingIndexes { get; } = new();

    public bool HasDanglingIndexes => DanglingIndexes.Count > 0;

    public string DanglingIndexesText =>
        $"{DanglingIndexes.Count} index(es) for models that aren't installed, using {ModelManager.FormatSize(DanglingIndexes.Sum(d => d.Size))}. " +
        "They can't be used until the model is downloaded again.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteAllIndexesCommand))]
    public partial long TotalIndexSize { get; set; }

    public string TotalIndexText => TotalIndexSize == 0
        ? "No search indexes yet."
        : $"Search indexes use {ModelManager.FormatSize(TotalIndexSize)} in total, across {_models.Entries.Count(e => e.HasIndex)} model(s).";

    [ObservableProperty]
    public partial string IndexMessage { get; set; } = "";

    partial void OnTotalIndexSizeChanged(long value) => OnPropertyChanged(nameof(TotalIndexText));

    /// <summary>Re-measures index sizes and the dangling list.</summary>
    public void RefreshIndexes() => RefreshIndexSizes();

    private void RefreshIndexSizes()
    {
        _models.RefreshIndexSizes();
        TotalIndexSize = ModelManager.TotalIndexSize();
        DanglingIndexes.Clear();
        foreach (var d in _models.DanglingIndexes()) DanglingIndexes.Add(d);
        OnPropertyChanged(nameof(HasDanglingIndexes));
        OnPropertyChanged(nameof(DanglingIndexesText));
        OnPropertyChanged(nameof(TotalIndexText));
    }

    [RelayCommand]
    private async Task DeleteDanglingIndex(DanglingIndex index)
    {
        if (Confirm is not null && !await Confirm($"Delete the search index for {index.Name} ({index.SizeText})?"))
            return;
        var error = _models.DeleteIndex(index.ModelId);
        RefreshIndexSizes();
        IndexMessage = error is null ? $"Deleted the index for {index.Name}." : $"Couldn't delete the index for {index.Name}: {error}";
    }

    [RelayCommand]
    private async Task DeleteAllDanglingIndexes()
    {
        var total = ModelManager.FormatSize(DanglingIndexes.Sum(d => d.Size));
        if (Confirm is not null && !await Confirm(
                $"Delete the {DanglingIndexes.Count} index(es) for models that aren't installed ({total})?"))
            return;
        var errors = _models.DeleteIndexes(DanglingIndexes.Select(d => d.ModelId));
        RefreshIndexSizes();
        IndexMessage = errors.Count == 0 ? "Deleted the indexes for models that aren't installed." : "Some indexes couldn't be deleted: " + string.Join("; ", errors);
    }

    [RelayCommand]
    private async Task DeleteModelIndex(ModelEntry entry)
    {
        if (Confirm is not null && !await Confirm(
                $"Delete the search index for {entry.Info.Name} ({entry.IndexSizeText})?\n\n" +
                "Folders will be re-indexed the next time you search them with this model."))
            return;
        var error = _models.DeleteIndex(entry.Info.Id);
        RefreshIndexSizes();
        IndexMessage = error is null ? $"Deleted the index for {entry.Info.Name}." : $"Couldn't delete the index for {entry.Info.Name}: {error}";
    }

    private bool HasAnyIndex() => TotalIndexSize > 0;

    [RelayCommand(CanExecute = nameof(HasAnyIndex))]
    private async Task DeleteAllIndexes()
    {
        if (Confirm is not null && !await Confirm(
                $"Delete all search indexes ({ModelManager.FormatSize(TotalIndexSize)})?\n\n" +
                "Downloaded models are kept. Folders will be re-indexed the next time you search them."))
            return;
        var errors = _models.DeleteAllIndexes();
        RefreshIndexSizes();
        IndexMessage = errors.Count == 0 ? "Deleted all search indexes." : "Some indexes couldn't be deleted: " + string.Join("; ", errors);
    }

    [RelayCommand]
    private Task DownloadModel(ModelEntry entry) => _models.DownloadAsync(entry);

    [RelayCommand]
    private void CancelModelDownload(ModelEntry entry) => _models.CancelDownload(entry);

    /// <summary>Deletes a downloaded model, asking whether to delete its search index too (default: yes).</summary>
    [RelayCommand]
    private async Task DeleteModel(ModelEntry entry)
    {
        var alsoIndex = entry.HasIndex;
        if (ConfirmWithOption is not null)
        {
            var (confirmed, option) = await ConfirmWithOption(
                "Delete model",
                $"Delete {entry.Info.Name} ({entry.Info.SizeText})? You can download it again later.",
                "Delete",
                entry.HasIndex ? $"Also delete its search index ({entry.IndexSizeText})" : null,
                true);
            if (!confirmed) return;
            alsoIndex = entry.HasIndex && option;
        }
        if (_models.Delete(entry) is { } modelError)
        {
            RefreshIndexSizes();
            IndexMessage = $"Couldn't fully delete {entry.Info.Name}: {modelError}";
            return;
        }
        var error = alsoIndex ? _models.DeleteIndex(entry.Info.Id) : null;
        RefreshIndexSizes();
        IndexMessage = (alsoIndex, error) switch
        {
            (false, _) when entry.HasIndex => $"Deleted {entry.Info.Name}; its search index was kept.",
            (false, _) => $"Deleted {entry.Info.Name}.",
            (true, null) => $"Deleted {entry.Info.Name} and its search index.",
            _ => $"Deleted {entry.Info.Name}, but couldn't delete its search index: {error}",
        };
    }

    // ---- General ----

    public ObservableCollection<ToolOption> ToolOptions { get; } = new();

    [ObservableProperty]
    public partial string? DefaultToolId { get; set; }

    [ObservableProperty]
    public partial decimal? DefaultMaxResults { get; set; }

    [ObservableProperty]
    public partial decimal? DefaultMinScore { get; set; }

    // ---- Appearance: light/dark and density preview live (restored if cancelled); the theme applies at the next start ----

    public static System.Collections.Generic.IReadOnlyList<AppearanceOption> ThemeOptions => Appearance.Themes;
    public static System.Collections.Generic.IReadOnlyList<AppearanceOption> ThemeModeOptions => Appearance.Modes;
    public static System.Collections.Generic.IReadOnlyList<AppearanceOption> ThemeDensityOptions => Appearance.Densities;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFluentTheme), nameof(ThemeNeedsRestart))]
    public partial string Theme { get; set; } = Appearance.Fluent;

    /// <summary>A different theme is chosen than the one running: it applies at the next start.</summary>
    public bool ThemeNeedsRestart => Theme != Appearance.RunningTheme;

    [ObservableProperty]
    public partial string ThemeMode { get; set; } = Appearance.System;

    [ObservableProperty]
    public partial string ThemeDensity { get; set; } = Appearance.Normal;

    /// <summary>Density only exists in the Fluent theme (and only shows while Fluent is running).</summary>
    public bool IsFluentTheme => Theme == Appearance.Fluent;

    partial void OnThemeModeChanged(string value) => PreviewAppearance();
    partial void OnThemeDensityChanged(string value) => PreviewAppearance();

    private void PreviewAppearance()
    {
        if (Avalonia.Application.Current is { } app && ThemeMode is not null && ThemeDensity is not null)
            Appearance.ApplyLive(app, ThemeMode, ThemeDensity);
    }

    // ---- GPU ----

    public static System.Collections.Generic.IReadOnlyList<GpuModeOption> GpuModeOptions { get; } =
    [
        new(GpuModes.Auto, "Auto: use the GPU when it can run the model (recommended)"),
        new(GpuModes.On, "On: try the GPU even if the checks say no"),
        new(GpuModes.Off, "Off: always use the CPU"),
    ];

    [ObservableProperty]
    public partial string GpuMode { get; set; }

    public static System.Collections.Generic.IReadOnlyList<int> GpuBatchSizeOptions => GpuBatch.Choices;

    [ObservableProperty]
    public partial int GpuBatchSize { get; set; }

    /// <summary>The DirectML privacy note (see PRIVACY.md, rule 2); shown on Windows, where the GPU uses DirectML.</summary>
    public static string GpuPrivacyNote => PrivacyText.DirectMLSettingNote;

    public static bool HasGpuPrivacyNote => OperatingSystem.IsWindows();

    /// <summary>What this machine offers, e.g. "NVIDIA GeForce RTX 3080 (9.8 GB), via DirectML".</summary>
    public static string DetectedGpuText => GpuInfo.Available switch
    {
        GpuInfo.Backend.DirectML when GpuInfo.Best is { } gpu => $"Detected: {gpu}, via DirectML",
        GpuInfo.Backend.DirectML => "Detected: no DirectX 12 graphics card; models run on the CPU",
        GpuInfo.Backend.CoreML => "Detected: Apple GPU / Neural Engine, via Core ML",
        _ => "GPU acceleration isn't available on this operating system yet; models run on the CPU",
    };

    // ---- Custom tools ----

    public ObservableCollection<CustomToolViewModel> CustomTools { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedCustomTool))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCustomToolCommand))]
    public partial CustomToolViewModel? SelectedCustomTool { get; set; }

    public bool HasSelectedCustomTool => SelectedCustomTool is not null;

    [ObservableProperty]
    public partial string Error { get; set; } = "";

    [RelayCommand]
    private void NewCustomTool()
    {
        var tool = new CustomToolViewModel(new CommandLineToolDefinition
        {
            Id = "tool-" + Guid.NewGuid().ToString("N")[..8],
            Name = "New tool",
        });
        AddCustomTool(tool);
        SelectedCustomTool = tool;
        RebuildToolOptions();
    }


    private bool HasSelection() => SelectedCustomTool is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveCustomTool()
    {
        if (SelectedCustomTool is not { } tool) return;
        var index = CustomTools.IndexOf(tool);
        tool.PropertyChanged -= CustomTool_PropertyChanged;
        CustomTools.Remove(tool);
        SelectedCustomTool = CustomTools.Count == 0 ? null : CustomTools[Math.Min(index, CustomTools.Count - 1)];
        RebuildToolOptions();
    }

    private void AddCustomTool(CustomToolViewModel tool)
    {
        tool.Calibration = _tools.Calibrator.StateFor(tool.Id);
        tool.PropertyChanged += CustomTool_PropertyChanged;
        CustomTools.Add(tool);
    }

    private void CustomTool_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CustomToolViewModel.Name)) RebuildToolOptions();
    }

    private void RebuildToolOptions()
    {
        var selected = DefaultToolId;
        ToolOptions.Clear();
        foreach (var m in _models.Installed)
            ToolOptions.Add(new ToolOption(EmbeddingModelTool.IdPrefix + m.Info.Id, m.Info.Name));
        if (Features.CommandLineTools) // otherwise not offered (see Features)
            foreach (var t in CustomTools)
                ToolOptions.Add(new ToolOption(t.Id, string.IsNullOrWhiteSpace(t.Name) ? "(unnamed)" : t.Name));
        // Keep the choice if it's still offered; else the recommended model, else the first one.
        var recommended = EmbeddingModelTool.IdPrefix + ModelCatalog.RecommendedId;
        DefaultToolId = ToolOptions.Any(o => o.Id == selected) ? selected
            : ToolOptions.Any(o => o.Id == recommended) ? recommended
            : ToolOptions.FirstOrDefault()?.Id;
    }

    /// <summary>Validated preferences, or null (with <see cref="Error"/> set) if something's wrong.</summary>
    public Preferences? TryBuild()
    {
        // Saved command-line tools are kept as they are while the feature is off (their page is hidden,
        // so there'd be no way to fix one that doesn't validate).
        foreach (var t in Features.CommandLineTools ? CustomTools : [])
        {
            if (t.Validate() is { } problem)
            {
                SelectedCustomTool = t;
                Error = $"Custom tool \"{t.Name}\": {problem}";
                return null;
            }
        }
        Error = "";
        return new Preferences
        {
            DefaultToolId = DefaultToolId ?? "",
            // Written by this version, so nothing needs migrating when it's loaded again.
            ScoreScaleVersion = Preferences.CurrentScoreScaleVersion,
            ToolsVersion = Preferences.CurrentToolsVersion,
            DefaultMaxResults = Math.Clamp(DefaultMaxResults ?? 200, 1, 100_000),
            DefaultMinScore = Math.Clamp(DefaultMinScore ?? Preferences.StandardMinScore, 0, 100),
            UseGpu = GpuMode,
            Theme = Theme,
            ThemeMode = ThemeMode,
            ThemeDensity = ThemeDensity,
            PreviewWindow = _prefs.PreviewWindow, // not edited here; kept as is
            GpuBatchSize = GpuBatchSize,
            CustomTools = CustomTools.Select(t => t.ToDefinition()).ToList(),
        };
    }
}

/// <summary>One user-defined command-line tool in the preferences window.</summary>
public partial class CustomToolViewModel : ViewModelBase
{
    public CustomToolViewModel(CommandLineToolDefinition def)
    {
        Id = def.Id;
        Name = def.Name;
        Executable = def.Executable;
        Arguments = def.Arguments;
        OutputPattern = def.OutputPattern;
        DefaultScore = (decimal)def.DefaultScore;
        FallbackPaths = string.Join(Environment.NewLine, def.FallbackPaths);
        AutoCalibrate = def.AutoCalibrate;
    }

    public string Id { get; }

    /// <summary>Score calibration of the saved tool; without one, its raw scores are shown.</summary>
    public CalibrationState Calibration { get; set; } = new("");

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Executable { get; set; }

    [ObservableProperty]
    public partial string Arguments { get; set; }

    [ObservableProperty]
    public partial string OutputPattern { get; set; }

    [ObservableProperty]
    public partial decimal? DefaultScore { get; set; }

    /// <summary>Other places to look for the executable, one per line.</summary>
    [ObservableProperty]
    public partial string FallbackPaths { get; set; }

    [ObservableProperty]
    public partial bool AutoCalibrate { get; set; }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) return "give it a name.";
        if (string.IsNullOrWhiteSpace(Executable)) return "set the executable.";
        if (CommandLineTool.IsBatchFile(Executable) || FallbackPaths.Split('\n').Any(CommandLineTool.IsBatchFile))
            return "batch files (.bat, .cmd) can't be run safely; choose the program's .exe instead.";
        if (!Arguments.Contains("{query}")) return "the arguments must include {query}.";
        try
        {
            var regex = CommandLineTool.CompilePattern(OutputPattern);
            if (!regex.GetGroupNames().Contains("path"))
                return "the output pattern needs a named group (?<path>...).";
        }
        catch (ArgumentException e)
        {
            return "the output pattern isn't a valid regex: " + e.Message;
        }
        return null;
    }

    public CommandLineToolDefinition ToDefinition() => new()
    {
        Id = Id,
        Name = Name.Trim(),
        Executable = Executable.Trim(),
        Arguments = Arguments.Trim(),
        OutputPattern = OutputPattern,
        DefaultScore = (double)(DefaultScore ?? 1),
        FallbackPaths = FallbackPaths.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList(),
        AutoCalibrate = AutoCalibrate,
    };
}
