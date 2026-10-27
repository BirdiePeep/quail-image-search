using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuailImageSearch.Services;
using QuailImageSearch.Services.Models;

namespace QuailImageSearch.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ToolRegistry _tools;

    public MainViewModel(ToolRegistry tools, ModelManager models, Preferences prefs, DebugLog log, SessionState? session = null)
    {
        _tools = tools;
        Models = models;
        Preferences = prefs;
        Log = log;
        // A model was installed or removed: re-select each tab's tool (removed ones fall back to the default).
        _tools.ToolsChanged += () =>
        {
            foreach (var tab in Tabs) tab.RefreshTool();
            _ = CalibrateInBackgroundAsync(); // e.g. a model was just downloaded
        };
        // Calibration changed: show results on the new 0–100 scale.
        _tools.Calibrator.Changed += toolId => { foreach (var tab in Tabs) tab.Rescale(toolId); };

        if (session is { Tabs.Count: > 0 })
        {
            foreach (var t in session.Tabs)
                AddTab(new SearchTabViewModel(_tools, t));
            SelectedTab = Tabs[Math.Clamp(session.SelectedTab, 0, Tabs.Count - 1)];
            IsLogVisible = session.IsLogVisible;
            WindowPlacement = session.Window;
        }
        else
        {
            NewTab();
        }
        _ = CalibrateInBackgroundAsync();
    }

    /// <summary>
    /// Calibrates auto-calibrated tools that aren't calibrated yet, one at a time: all installed
    /// models (a few seconds each), and command-line tools if a tab or the default tool
    /// uses them (they run once per test image, which can take a minute or two, so otherwise they're
    /// calibrated on their first search).
    /// </summary>
    private async Task CalibrateInBackgroundAsync()
    {
        var used = Tabs.Select(t => t.ToolId).Append(_tools.DefaultToolId).ToHashSet();
        var pending = _tools.Tools
            .Where(t => t.AutoCalibrate && !_tools.Calibrator.IsCalibrated(t))
            .Where(t => t is EmbeddingModelTool || used.Contains(t.Id))
            .ToList();
        foreach (var tool in pending)
        {
            var state = _tools.Calibrator.StateFor(tool.Id);
            void Show(object? _, System.ComponentModel.PropertyChangedEventArgs e) =>
                BackgroundStatus = state.IsRunning ? $"Calibrating {tool.Name} scores… {state.Progress}" : "";
            state.PropertyChanged += Show;
            try
            {
                await _tools.Calibrator.EnsureCalibratedAsync(tool);
            }
            finally
            {
                state.PropertyChanged -= Show;
                BackgroundStatus = "";
            }
        }
    }

    // Designer-only constructor.
    public MainViewModel() : this(Design()) { }

    private MainViewModel((ToolRegistry Tools, ModelManager Models) d) : this(d.Tools, d.Models, new Preferences(), new DebugLog()) { }

    private static (ToolRegistry, ModelManager) Design()
    {
        var log = new DebugLog();
        var models = new ModelManager(log);
        return (new ToolRegistry(log, new Preferences(), models, new ScoreCalibrator(log)), models);
    }

    public ObservableCollection<SearchTabViewModel> Tabs { get; } = new();

    public ToolRegistry ToolRegistry => _tools;

    public ModelManager Models { get; }

    public Preferences Preferences { get; private set; }

    public DebugLog Log { get; }

    /// <summary>Window position/size, restored by the view on startup and updated by it on close.</summary>
    public WindowPlacement? WindowPlacement { get; set; }

    /// <summary>Set by the view: opens the preferences dialog.</summary>
    public Func<Task>? ShowPreferences { get; set; }

    [ObservableProperty]
    public partial SearchTabViewModel? SelectedTab { get; set; }

    /// <summary>Background work shown in the status bar (e.g. score calibration).</summary>
    [ObservableProperty]
    public partial string BackgroundStatus { get; set; } = "";

    [ObservableProperty]
    public partial bool IsLogVisible { get; set; }

    [RelayCommand]
    private void ToggleLog() => IsLogVisible = !IsLogVisible;

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    [RelayCommand]
    private async Task OpenPreferences()
    {
        if (ShowPreferences is not null) await ShowPreferences();
    }

    [RelayCommand]
    private void NewTab()
    {
        // New tabs use the default tool and settings, and start in the current tab's folder.
        var tool = _tools.Resolve(_tools.DefaultToolId);
        var tab = new SearchTabViewModel(_tools, tool?.Id ?? _tools.DefaultToolId, SelectedTab?.Folder,
            Preferences.DefaultMaxResults, _tools.DefaultMinScoreFor(tool),
            SelectedTab?.ThumbnailScale ?? SearchTabViewModel.DefaultThumbnailScale); // keep the current tab's thumbnail size
        AddTab(tab);
        SelectedTab = tab;
    }

    /// <summary>
    /// "Find similar": opens a tab next to the current one that searches the same folder for images
    /// like <paramref name="imagePath"/>, keeping the current results where they are. Uses the current
    /// tab's model, or the default (or first) model if its tool can't search by image.
    /// </summary>
    public void FindSimilar(string imagePath)
    {
        var current = SelectedTab;
        var tool = new[] { _tools.Find(current?.ToolId), _tools.Resolve(_tools.DefaultToolId) }
                       .OfType<EmbeddingModelTool>().FirstOrDefault()
                   ?? _tools.Tools.OfType<EmbeddingModelTool>().FirstOrDefault()
                   ?? _tools.Resolve(current?.ToolId);
        var tab = new SearchTabViewModel(_tools, tool?.Id ?? _tools.DefaultToolId, current?.Folder,
            current?.MaxResults ?? Preferences.DefaultMaxResults, _tools.DefaultMinScoreFor(tool),
            current?.ThumbnailScale ?? SearchTabViewModel.DefaultThumbnailScale);
        tab.SessionChanged += SaveSession;
        tab.FilesDeleted += OnFilesDeleted;
        Tabs.Insert(current is null ? Tabs.Count : Tabs.IndexOf(current) + 1, tab);
        SelectedTab = tab;
        tab.SearchByImage(imagePath);
    }

    private void AddTab(SearchTabViewModel tab)
    {
        tab.SessionChanged += SaveSession;
        tab.FilesDeleted += OnFilesDeleted;
        Tabs.Add(tab);
    }

    /// <summary>Files were moved to the trash from a tab: drop them from every tab's results and from the indexes.</summary>
    private void OnFilesDeleted(System.Collections.Generic.IReadOnlyList<string> paths)
    {
        var set = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in Tabs) tab.RemoveResults(set);
        _tools.ForgetFiles(paths);
    }

    /// <summary>Moves a tab to another position (drag and drop in the tab strip) and keeps it selected.</summary>
    public void MoveTab(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= Tabs.Count || to >= Tabs.Count) return;
        var tab = Tabs[from];
        Tabs.Move(from, to);
        SelectedTab = tab;
    }

    [RelayCommand]
    private void CloseTab(SearchTabViewModel? tab)
    {
        tab ??= SelectedTab;
        if (tab is null) return;
        var index = Tabs.IndexOf(tab);
        tab.SessionChanged -= SaveSession;
        tab.FilesDeleted -= OnFilesDeleted;
        Tabs.Remove(tab);
        if (Tabs.Count == 0)
            NewTab();
        else
            SelectedTab = Tabs[Math.Clamp(index, 0, Tabs.Count - 1)];
        SaveSession();
    }

    /// <summary>Remembers the preview window's size and position (saved to preferences.json).</summary>
    public void SavePreviewPlacement(WindowPlacement placement)
    {
        Preferences.PreviewWindow = placement;
        try
        {
            AppFiles.Preferences.Save(Preferences);
        }
        catch (Exception e)
        {
            Log.Write($"could not save preferences to {AppFiles.Preferences.FilePath}: {e.Message}");
        }
    }

    /// <summary>
    /// Downloads a model (from the first-run prompt) with progress in the status bar, then makes it
    /// the default search tool, so new tabs and tabs without a tool use it.
    /// </summary>
    public async Task InstallModelAsync(ModelEntry entry)
    {
        void Show(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (entry.IsDownloading) BackgroundStatus = $"Downloading {entry.Info.Name}: {entry.StatusText}";
        }
        BackgroundStatus = $"Downloading {entry.Info.Name}…";
        entry.PropertyChanged += Show;
        try
        {
            await Models.DownloadAsync(entry);
        }
        finally
        {
            entry.PropertyChanged -= Show;
        }
        if (!entry.IsInstalled)
        {
            BackgroundStatus = $"{entry.StatusText} You can try again in File > Preferences > Models.";
            return;
        }
        Preferences.DefaultToolId = EmbeddingModelTool.IdPrefix + entry.Info.Id;
        ApplyPreferences(Preferences); // saves, and switches tabs that had no tool to it
        BackgroundStatus = $"{entry.Info.Name} is installed. Pick a folder and describe what you're looking for.";
    }

    /// <summary>Saves new preferences and applies them (tool list, defaults).</summary>
    public void ApplyPreferences(Preferences prefs)
    {
        Preferences = prefs;
        try
        {
            AppFiles.Preferences.Save(prefs);
        }
        catch (Exception e)
        {
            Log.Write($"could not save preferences to {AppFiles.Preferences.FilePath}: {e.Message}");
        }
        _tools.Apply(prefs);
        foreach (var tab in Tabs) tab.RefreshTool();
        Log.Write($"preferences saved; default tool: {_tools.Resolve(null)?.Name ?? "(none)"},{_tools.Tools.Count} tool(s) available");
    }

    public SessionState ToSessionState() => new()
    {
        Tabs = Tabs.Select(t => t.ToState()).ToList(),
        SelectedTab = SelectedTab is null ? 0 : Tabs.IndexOf(SelectedTab),
        IsLogVisible = IsLogVisible,
        Window = WindowPlacement,
    };

    /// <summary>Saved on close, and also after each search and tab close so a crash loses little.</summary>
    public void SaveSession()
    {
        try
        {
            AppFiles.Session.Save(ToSessionState());
        }
        catch (Exception e)
        {
            Log.Write($"could not save session to {AppFiles.Session.FilePath}: {e.Message}");
        }
    }
}
