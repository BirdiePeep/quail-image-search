using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuailImageSearch.Services;

namespace QuailImageSearch.ViewModels;

/// <summary>One tab: a query, a folder, and the results grid with its selection.</summary>
public partial class SearchTabViewModel : ViewModelBase
{
    public const decimal DefaultMaxResults = 200;

    /// <summary>
    /// Min score for image searches. Their 0–100 scale runs from unrelated photos to the same image;
    /// on the bundled calibration photos this hides about 93% of unrelated pairs.
    /// </summary>
    public const decimal ImageMinScore = 25;

    // Thumbnail size slider: percent of the base tile size, in 10% steps.
    public const int MinThumbnailScale = 50;
    public const int MaxThumbnailScale = 200;
    public const int DefaultThumbnailScale = 100;
    private const double BaseImageSize = 224; // image area at 100%, in pixels

    private readonly ToolRegistry _tools;
    private string _toolId;
    private string _resultsToolId;                               // tool that produced the current results
    private List<SearchResult> _rawResults = new();              // everything the tool returned, raw scores (persisted)
    private ScoreScale? _imageScale;                             // scale of the results if they came from an image search
    private List<ImageResultViewModel> _allResults = new();      // view models for all of them
    private List<ImageResultViewModel> _results = new();         // the ones passing MinScore (shown)
    private int _columns = 4;
    private int _anchorIndex = -1; // where Shift-selection ranges start
    private int _focusIndex = -1;  // where arrow-key movement starts
    private CancellationTokenSource? _searchCts;

    /// <summary>A new, empty tab using the given tool and default settings.</summary>
    public SearchTabViewModel(ToolRegistry tools, string toolId, string? folder, decimal maxResults, decimal minScore,
                              int thumbnailScale = DefaultThumbnailScale)
    {
        ThumbnailScale = thumbnailScale;
        _tools = tools;
        _toolId = tools.Resolve(toolId)?.Id ?? toolId;
        _resultsToolId = _toolId;
        Folder = folder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        MaxResults = maxResults;
        MinScore = minScore;
    }

    /// <summary>A tab restored from session.json; a tab saved without a tool uses the default tool.</summary>
    public SearchTabViewModel(ToolRegistry tools, TabState state)
        : this(tools, state.ToolId ?? "", state.Folder,
               state.MaxResults ?? DefaultMaxResults, state.MinScore ?? Preferences.StandardMinScore)
    {
        // Tabs saved before scores were rescaled to 0–100 hold raw-scale Min scores like 0.22.
        if (state.ScoreScaleVersion < Preferences.CurrentScoreScaleVersion)
            MinScore = tools.DefaultMinScoreFor(tools.Resolve(_toolId));
        ThumbnailScale = state.ThumbnailScale ?? DefaultThumbnailScale;
        Query = state.Query;
        var minScore = MinScore;
        QueryImage = state.QueryImage;
        MinScore = minScore; // as saved, not the image-search default
        LastQuery = state.LastQuery;
        HasSearched = state.Results.Count > 0 || !string.IsNullOrWhiteSpace(state.LastQuery);
        _resultsToolId = state.ToolId ?? "";
        _imageScale = state.ImageScoreScale;
        SetResults(state.Results);
        if (state.Results.Count > 0)
            Status = $"{state.Results.Count} results restored from last session.";
    }

    /// <summary>Raised when something worth saving changes (e.g. a search finished).</summary>
    public event Action? SessionChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    public partial string LastQuery { get; set; } = "";

    /// <summary>
    /// Image to search with ("find similar", or a file dropped on the search bar); while set, it
    /// replaces the text query.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQueryImage), nameof(QueryImageName))]
    public partial string? QueryImage { get; set; }

    public bool HasQueryImage => QueryImage is not null;

    public string QueryImageName => QueryImage is null ? "" : System.IO.Path.GetFileName(QueryImage);

    /// <summary>Small picture of <see cref="QueryImage"/> for the search bar.</summary>
    [ObservableProperty]
    public partial Avalonia.Media.Imaging.Bitmap? QueryImageThumbnail { get; private set; }

    partial void OnQueryImageChanged(string? oldValue, string? newValue)
    {
        // Image and text searches score on different scales, so switching between them resets the filter.
        if ((oldValue is null) != (newValue is null))
            MinScore = newValue is null ? _tools.DefaultMinScoreFor(_tools.Resolve(_toolId)) : ImageMinScore;
        var value = newValue;
        QueryImageThumbnail = null;
        if (value is null) return;
        Task.Run(() =>
        {
            Avalonia.Media.Imaging.Bitmap? bitmap = null;
            try { bitmap = ImageLoader.Load(value, 64, 64); }
            catch (Exception) { } // the search reports unreadable images
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (QueryImage == value) QueryImageThumbnail = bitmap;
            });
        });
    }

    /// <summary>Back to searching by text.</summary>
    [RelayCommand]
    private void ClearQueryImage() => QueryImage = null;

    /// <summary>Searches for images like <paramref name="path"/>.</summary>
    public void SearchByImage(string path)
    {
        QueryImage = path;
        if (SearchCommand.CanExecute(null)) SearchCommand.Execute(null);
    }

    /// <summary>Image file types that can be searched with.</summary>
    public static bool IsSearchableImage(string path) => EmbeddingModelTool.IsImageFile(path);

    [ObservableProperty]
    public partial string Folder { get; set; }

    [ObservableProperty]
    public partial decimal? MaxResults { get; set; } = DefaultMaxResults;

    /// <summary>
    /// Display-only filter on the 0–100 score (raw score for uncalibrated tools): the tool still
    /// returns everything, this just hides low scores.
    /// </summary>
    [ObservableProperty]
    public partial decimal? MinScore { get; set; } = Preferences.StandardMinScore;

    /// <summary>Thumbnail size in percent (50–200), snapped to 10% steps.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TileWidth), nameof(TileHeight), nameof(ImageSize), nameof(ThumbnailScaleText), nameof(IsThumbnailScaleDefault))]
    public partial int ThumbnailScale { get; set; }

    public string ThumbnailScaleText => $"{ThumbnailScale}%";

    public bool IsThumbnailScaleDefault => ThumbnailScale == DefaultThumbnailScale;

    [RelayCommand]
    private void ResetThumbnailScale() => ThumbnailScale = DefaultThumbnailScale;

    /// <summary>Side of the square image area in a tile.</summary>
    public double ImageSize => Math.Round(BaseImageSize * ThumbnailScale / 100.0);

    public double TileWidth => ImageSize + 12;       // + tile padding
    public double TileHeight => ImageSize + 12 + 38; // + padding + file name and score lines

    /// <summary>Tile width including its margin, for working out how many columns fit.</summary>
    public double TileOuterWidth => TileWidth + 8;

    /// <summary>Decode width for thumbnails at the current size (rounded up so small steps reuse them).</summary>
    public int ThumbnailDecodeWidth => (int)(Math.Ceiling(ImageSize / 64.0) * 64);

    /// <summary>Raised when the tile size changes, so the view can recompute the column count.</summary>
    public event Action? TileSizeChanged;

    partial void OnThumbnailScaleChanged(int value)
    {
        var snapped = Math.Clamp((int)Math.Round(value / 10.0) * 10, MinThumbnailScale, MaxThumbnailScale);
        if (snapped != value)
        {
            ThumbnailScale = snapped; // the slider snaps too; this also covers values from session.json
            return;
        }
        // Tiles that were already shown get a sharper thumbnail if they grew.
        // Oldest first, so tiles on screen (shown most recently) stay the most recent in the thumbnail cache.
        foreach (var r in _allResults.Where(r => r.HasThumbnailRequest).OrderBy(r => r.LastUsed).ToList())
            r.EnsureThumbnail(ThumbnailDecodeWidth);
        TileSizeChanged?.Invoke();
        SessionChanged?.Invoke();
    }

    [ObservableProperty]
    public partial string Status { get; set; } = "Pick a folder and type a description, e.g. \"a dog on a beach\".";

    [ObservableProperty]
    public partial string FilterSummary { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>True once a search has finished in this tab (or results were restored).</summary>
    [ObservableProperty]
    public partial bool HasSearched { get; set; }

    /// <summary>The placeholder logo in the middle of the results area, before the first search.</summary>
    public bool ShowLogo => HasTool && !HasSearched && !IsBusy && _allResults.Count == 0;

    /// <summary>
    /// True if the tab has a search tool it can use (an installed model or a command-line tool).
    /// Without one the search box is disabled and the results area asks to pick a model.
    /// </summary>
    public bool HasTool => _tools.Resolve(_toolId) is not null;

    /// <summary>"Download a model" prompt in the middle of the results area (no search tool at all).</summary>
    public bool NeedsTool => !HasTool;

    /// <summary>
    /// Shown in the middle of the results area when there's nothing to show after a search. Never
    /// while there's no usable tool: the "download a model" prompt takes the space then.
    /// </summary>
    public string EmptyMessage =>
        !HasTool || IsBusy || !HasSearched || _results.Count > 0 ? ""
        : _allResults.Count == 0 ? $"No results found for \u201c{LastQuery}\u201d."
        : $"All {_allResults.Count} results score below the Min score ({MinScore ?? 0:0.##}). Lower it to see them.";

    public bool HasEmptyMessage => EmptyMessage.Length > 0;

    partial void OnIsBusyChanged(bool value) => NotifyEmptyState();

    partial void OnHasSearchedChanged(bool value) => NotifyEmptyState();

    private void NotifyEmptyState()
    {
        OnPropertyChanged(nameof(HasTool));
        OnPropertyChanged(nameof(NeedsTool));
        SearchCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowLogo));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(HasEmptyMessage));
    }

    /// <summary>Tools for the dropdown.</summary>
    public ObservableCollection<ISearchTool> Tools => _tools.Tools;

    /// <summary>
    /// The tab's search tool. Nulls are ignored: the dropdown briefly reports no selection when a
    /// tool is removed or renamed in preferences, and the tab shouldn't forget its choice for that.
    /// </summary>
    public string? ToolId
    {
        get => _toolId;
        set
        {
            if (value is null || value == _toolId) return;
            _toolId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedTool));
            OnPropertyChanged(nameof(MinScoreStep));
            NotifyEmptyState();
            // Score scales differ between tools/models, so switching tools resets the filter.
            if (_tools.Find(value) is { } tool)
                MinScore = QueryImage is null ? _tools.DefaultMinScoreFor(tool) : ImageMinScore;
        }
    }

    /// <summary>
    /// The tab's tool object, for the dropdown. Bound by object rather than by id: a tool that
    /// comes back under the same id (e.g. a model that finished downloading) is then a new value,
    /// so the dropdown re-selects it instead of staying blank. Nulls are ignored, as for <see cref="ToolId"/>.
    /// </summary>
    public ISearchTool? SelectedTool
    {
        get => _tools.Find(_toolId);
        set
        {
            if (value is not null) ToolId = value.Id;
        }
    }

    /// <summary>
    /// Re-selects the tab's tool in the dropdown after the tool list changed. A tab whose tool is
    /// missing switches to the default (or first) tool, so installing a model or adding a tool
    /// makes every tab usable at once.
    /// </summary>
    public void RefreshTool()
    {
        if (_tools.Find(_toolId) is null && _tools.Resolve(null) is { } fallback) _toolId = fallback.Id;
        OnPropertyChanged(nameof(ToolId));
        OnPropertyChanged(nameof(SelectedTool));
        OnPropertyChanged(nameof(MinScoreStep));
        NotifyEmptyState(); // a model may have been installed or removed
    }

    /// <summary>
    /// Min score arrow step: 5 on the 0–100 scale; 0.01 while the tab's tool shows raw scores
    /// (uncalibrated command-line tools, whose scores are usually between 0 and 1).
    /// </summary>
    public decimal MinScoreStep =>
        _tools.Find(_toolId) is { } tool && !tool.AutoCalibrate && !_tools.Calibrator.IsCalibrated(tool) ? 0.01m : 5;

    public string Header => string.IsNullOrWhiteSpace(LastQuery) ? "New search" : LastQuery;

    public ObservableCollection<ResultRowViewModel> Rows { get; } = new();

    public IReadOnlyList<ImageResultViewModel> Results => _results;

    public IEnumerable<ImageResultViewModel> SelectedItems => _results.Where(r => r.IsSelected);

    public IReadOnlyList<string> SelectedPaths => SelectedItems.Select(r => r.Path).ToList();

    public TabState ToState() => new()
    {
        ToolId = _toolId,
        Query = Query,
        QueryImage = QueryImage,
        ImageScoreScale = _imageScale,
        LastQuery = LastQuery,
        Folder = Folder,
        MaxResults = MaxResults,
        MinScore = MinScore,
        ScoreScaleVersion = Preferences.CurrentScoreScaleVersion,
        ThumbnailScale = ThumbnailScale,
        Results = _rawResults,
    };

    partial void OnMinScoreChanged(decimal? value) => ApplyFilter();

    private bool CanSearch() => !IsBusy && HasTool;

    [RelayCommand]
    private void Cancel() => _searchCts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        var image = QueryImage;
        var query = image ?? Query.Trim();
        if (query.Length == 0) { Status = "Type what you're looking for."; return; }
        if (!Directory.Exists(Folder)) { Status = $"Folder not found: {Folder}"; return; }
        if (image is not null && !File.Exists(image)) { Status = $"Image not found: {image}"; return; }
        if (image is not null && _tools.Resolve(_toolId) is { } textOnly and not EmbeddingModelTool)
        {
            Status = $"{textOnly.Name} can't search by image. Pick a downloaded model in the tool list.";
            return;
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        IsBusy = true;
        LastQuery = image is null ? query : $"Like {System.IO.Path.GetFileName(image)}";
        Status = "Searching…";
        var started = DateTime.Now;
        try
        {
            var top = (int)Math.Clamp(MaxResults ?? DefaultMaxResults, 1, 100_000);
            if (_tools.Resolve(_toolId) is not { } tool)
            {
                Status = "No search tool available: download a model or add a command-line tool in Preferences.";
                return;
            }
            if (tool.AutoCalibrate && !_tools.Calibrator.IsCalibrated(tool))
            {
                // One-time: needed so scores can be shown on the common 0–100 scale.
                Status = $"Calibrating {tool.Name} scores on the built-in test images (one time)…";
                await _tools.Calibrator.EnsureCalibratedAsync(tool, _searchCts.Token);
            }
            if (tool is EmbeddingModelTool)
                Status = "Searching… (the first search in a new folder indexes it, which can take a while)";
            var progress = new Progress<string>(s => { if (IsBusy) Status = s; }); // posts to the UI thread
            IReadOnlyList<SearchResult> results;
            ScoreScale? imageScale = null;
            if (image is not null)
                (results, imageScale) = await ((EmbeddingModelTool)tool).SearchByImageAsync(
                    new SearchRequest(image, Folder, top), image, progress, _searchCts.Token);
            else
                results = await tool.SearchAsync(new SearchRequest(query, Folder, top), progress, _searchCts.Token);
            _resultsToolId = tool.Id;
            _imageScale = imageScale;
            SetResults(results);
            HasSearched = true;
            ScrollToTopRequested?.Invoke(); // new results start at the top
            Status = $"{results.Count} results from {tool.Name} in {(DateTime.Now - started).TotalSeconds:0.0}s"
                     + (tool.DeviceSummary is { } device ? $" · {device}" : "");
            SessionChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled. (Images indexed so far are kept, so the next search continues from there.)";
        }
        catch (Exception e)
        {
            Status = "Search failed: " + e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetResults(IReadOnlyList<SearchResult> results)
    {
        _rawResults = results.ToList();
        var scale = _imageScale ?? _tools.ScaleFor(_resultsToolId);
        _allResults = results.Select(r => new ImageResultViewModel(r.Path, r.Score, scale)).ToList();
        ApplyFilter();
    }

    /// <summary>Raised after files were moved to the trash from this tab, so every tab and index can drop them.</summary>
    public event Action<IReadOnlyList<string>>? FilesDeleted;

    public void OnFilesDeleted(IReadOnlyList<string> paths)
    {
        if (paths.Count > 0) FilesDeleted?.Invoke(paths);
    }

    /// <summary>Removes results for files that no longer exist (e.g. deleted from any tab).</summary>
    public void RemoveResults(ISet<string> paths)
    {
        if (!_rawResults.Any(r => paths.Contains(r.Path))) return;
        _rawResults.RemoveAll(r => paths.Contains(r.Path));
        _allResults.RemoveAll(r => paths.Contains(r.Path));
        ApplyFilter();
        SessionChanged?.Invoke();
    }

    /// <summary>Re-applies the results' tool calibration (e.g. after it was calibrated or recalibrated).</summary>
    public void Rescale(string toolId)
    {
        OnPropertyChanged(nameof(MinScoreStep));
        if (toolId != _resultsToolId || _allResults.Count == 0 || _imageScale is not null) return;
        var scale = _tools.ScaleFor(_resultsToolId);
        foreach (var r in _allResults) r.ApplyScale(scale);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var min = (double)(MinScore ?? 0);
        _results = _allResults.Where(r => r.Score >= min).ToList();
        for (var i = 0; i < _results.Count; i++)
            _results[i].Index = i;
        foreach (var hidden in _allResults.Where(r => r.Score < min))
            hidden.IsSelected = false; // never copy/drag something you can't see
        var focused = _focusItem;
        _anchorIndex = _focusIndex = -1;
        // Keep the focused image (the one the preview shows) if it's still shown.
        if (focused is not null && _results.Contains(focused)) _focusIndex = focused.Index;
        SetFocusItem(_focusIndex >= 0 ? focused : null);
        NotifyEmptyState();
        FilterSummary = _allResults.Count == 0
            ? ""
            : $"Showing {_results.Count} of {_allResults.Count} (score ≥ {min:0.##})";
        RebuildRows();
    }

    /// <summary>Called by the view when its width changes.</summary>
    public void SetColumns(int columns)
    {
        columns = Math.Max(1, columns);
        if (columns == _columns) return;
        _columns = columns;
        RebuildRows();
    }

    private void RebuildRows()
    {
        Rows.Clear();
        for (var i = 0; i < _results.Count; i += _columns)
            Rows.Add(new ResultRowViewModel(_results.GetRange(i, Math.Min(_columns, _results.Count - i))));
    }

    /// <summary>Explorer-style click selection: plain click, Ctrl toggles, Shift extends from the anchor.</summary>
    public void Select(ImageResultViewModel item, bool ctrl, bool shift)
    {
        _focusIndex = item.Index;
        SetFocusItem(item);
        if (shift && _anchorIndex >= 0)
        {
            var (lo, hi) = (Math.Min(_anchorIndex, item.Index), Math.Max(_anchorIndex, item.Index));
            foreach (var r in _results)
                r.IsSelected = (ctrl && r.IsSelected) || (r.Index >= lo && r.Index <= hi);
            return;
        }
        if (ctrl)
        {
            item.IsSelected = !item.IsSelected;
        }
        else
        {
            foreach (var r in _results)
                r.IsSelected = ReferenceEquals(r, item);
        }
        _anchorIndex = item.Index;
    }

    public void SelectAll()
    {
        foreach (var r in _results) r.IsSelected = true;
    }

    /// <summary>Moves the focus with arrow keys (Shift extends the selection); returns the newly focused item.</summary>
    public ImageResultViewModel? MoveSelection(int dx, int dy, bool shift)
    {
        if (_results.Count == 0) return null;
        var next = _focusIndex < 0 ? 0 : Math.Clamp(_focusIndex + dx + dy * _columns, 0, _results.Count - 1);
        var item = _results[next];
        Select(item, ctrl: false, shift: shift);
        return item;
    }

    public int RowOf(ImageResultViewModel item) => item.Index / _columns;

    private ImageResultViewModel? _focusItem;

    /// <summary>
    /// The image last clicked or moved to with the arrow keys, while it's shown; the preview window
    /// shows this one.
    /// </summary>
    public ImageResultViewModel? FocusedItem => _focusItem;

    /// <summary>How many results are shown (after the Min score filter).</summary>
    public int ShownCount => _results.Count;

    /// <summary>Raised when <see cref="FocusedItem"/> changes, or the shown results change.</summary>
    public event Action? FocusedItemChanged;

    private void SetFocusItem(ImageResultViewModel? item)
    {
        _focusItem = item;
        FocusedItemChanged?.Invoke();
    }

    /// <summary>Asks the view to scroll the results back to the top (after a search).</summary>
    public event Action? ScrollToTopRequested;

    /// <summary>Asks the view to scroll <see cref="FocusedItem"/> into view (e.g. after the preview moved it).</summary>
    public event Action? ScrollToFocusedRequested;

    /// <summary>Moves the focus and selection to the previous/next image (preview window arrows).</summary>
    public void Step(int delta)
    {
        if (_results.Count == 0) return;
        var next = _focusItem is null ? 0 : Math.Clamp(_focusItem.Index + delta, 0, _results.Count - 1);
        Select(_results[next], ctrl: false, shift: false);
        ScrollToFocusedRequested?.Invoke();
    }

    /// <summary>Focuses and selects the shown image at <paramref name="index"/> (clamped), e.g. the next one after a delete.</summary>
    public void FocusAt(int index)
    {
        if (_results.Count == 0) return;
        Select(_results[Math.Clamp(index, 0, _results.Count - 1)], ctrl: false, shift: false);
        ScrollToFocusedRequested?.Invoke();
    }

    /// <summary>The shown image at <paramref name="index"/>, or null (for preloading neighbours).</summary>
    public ImageResultViewModel? ShownAt(int index) => index >= 0 && index < _results.Count ? _results[index] : null;
}
