using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using QuailImageSearch.Services;
using QuailImageSearch.ViewModels;

namespace QuailImageSearch.Views;

public partial class SearchTabView : UserControl
{
    private const double DragThreshold = 6;
    private SearchTabViewModel? _subscribed;

    // Pointer state for click-vs-drag on tiles.
    private PointerPressedEventArgs? _pressArgs;
    private Point _pressPoint;
    private ImageResultViewModel? _deferredSelect;

    public SearchTabView()
    {
        InitializeComponent();
        // Tunnel so we see arrow keys before ScrollViewer consumes them for scrolling.
        ResultsScroller.AddHandler(KeyDownEvent, Results_KeyDown, RoutingStrategies.Tunnel);
        SearchBar.AddHandler(DragDrop.DragOverEvent, SearchBar_DragOver);
        SearchBar.AddHandler(DragDrop.DropEvent, SearchBar_Drop);
    }

    private SearchTabViewModel? Vm => DataContext as SearchTabViewModel;

    private TopLevel? Top => TopLevel.GetTopLevel(this);

    /// <summary>Ctrl on Windows/Linux, Cmd on macOS.</summary>
    private KeyModifiers CommandModifier =>
        Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_subscribed is not null)
        {
            _subscribed.TileSizeChanged -= UpdateColumns;
            _subscribed.ScrollToFocusedRequested -= ScrollToFocused;
            _subscribed.ScrollToTopRequested -= ScrollToTop;
        }
        _subscribed = Vm;
        if (_subscribed is not null)
        {
            _subscribed.TileSizeChanged += UpdateColumns;
            _subscribed.ScrollToFocusedRequested += ScrollToFocused;
            _subscribed.ScrollToTopRequested += ScrollToTop;
        }
        // The TabControl reuses this view when switching tabs, so re-sync layout-dependent state.
        UpdateColumns();
        ResultsScroller.Offset = default;
    }

    private void UpdateColumns()
    {
        var width = ResultsScroller.Bounds.Width - 8 - 20; // list margin + room for the scrollbar
        if (width > 0 && Vm is { } vm)
            vm.SetColumns((int)(width / vm.TileOuterWidth));
    }

    private void ScrollToTop() => ResultsScroller.Offset = default;

    /// <summary>Keeps the focused image in view when the preview window moves through the results.</summary>
    private void ScrollToFocused()
    {
        if (Vm is { FocusedItem: { } item } vm)
            ResultsList.ScrollIntoView(vm.RowOf(item));
    }

    private void ResultsScroller_SizeChanged(object? sender, SizeChangedEventArgs e) => UpdateColumns();

    private void QueryBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Vm is not { } vm) return;
        e.Handled = true;
        if (vm.SearchCommand.CanExecute(null))
            vm.SearchCommand.Execute(null);
    }

    private async void OpenModelPreferences_Click(object? sender, RoutedEventArgs e)
    {
        if (Top is MainWindow window) await window.ShowModelPreferencesAsync();
    }

    private async void BrowseFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (Top is not { } top || Vm is not { } vm) return;
        var start = string.IsNullOrEmpty(vm.Folder) ? null : await top.StorageProvider.TryGetFolderFromPathAsync(vm.Folder);
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder of images",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            vm.Folder = path;
    }

    // ---- Search by image ----

    /// <summary>The one image file being dragged, or null (folders, several files, other data).</summary>
    private static string? DraggedImage(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles() is { Length: 1 } files && files[0].TryGetLocalPath() is { } path
        && SearchTabViewModel.IsSearchableImage(path) ? path : null;

    private void SearchBar_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = Vm is { HasTool: true } && DraggedImage(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void SearchBar_Drop(object? sender, DragEventArgs e)
    {
        if (Vm is not { HasTool: true } vm || DraggedImage(e) is not { } path) return;
        e.Handled = true;
        vm.SearchByImage(path);
    }

    private async void PickImage_Click(object? sender, RoutedEventArgs e)
    {
        if (Top is not { } top || Vm is not { } vm) return;
        var start = string.IsNullOrEmpty(vm.Folder) ? null : await top.StorageProvider.TryGetFolderFromPathAsync(vm.Folder);
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Find images like…",
            AllowMultiple = false,
            SuggestedStartLocation = start,
            FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.jpg", "*.jpeg", "*.jpe", "*.png", "*.webp", "*.bmp", "*.gif"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
            vm.SearchByImage(path);
    }

    // ---- Tiles ----

    private void Tile_DataContextChanged(object? sender, EventArgs e)
    {
        // Tiles only exist for rows the virtualizing panel has realized, so this lazily loads thumbnails.
        if (sender is Control { DataContext: ImageResultViewModel item } && Vm is { } vm)
            item.EnsureThumbnail(vm.ThumbnailDecodeWidth);
    }

    private void Tile_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ImageResultViewModel item } tile || Vm is not { } vm) return;
        ResultsScroller.Focus();
        var point = e.GetCurrentPoint(tile);
        var ctrl = e.KeyModifiers.HasFlag(CommandModifier);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (point.Properties.IsRightButtonPressed)
        {
            if (!item.IsSelected) vm.Select(item, false, false);
            return; // context menu opens on its own
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        if (item.IsSelected && !ctrl && !shift)
        {
            // Might be the start of dragging the whole selection; collapse it only on a plain click.
            _deferredSelect = item;
        }
        else
        {
            vm.Select(item, ctrl, shift);
            _deferredSelect = null;
        }
        _pressArgs = e;
        _pressPoint = e.GetPosition(this);
    }

    private async void Tile_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressArgs is null || Top is not { } top || Vm is not { } vm) return;
        var delta = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;

        var trigger = _pressArgs;
        _pressArgs = null;
        _deferredSelect = null;
        var paths = vm.SelectedPaths;
        if (paths.Count == 0) return;
        try
        {
            await FileActions.DragOutAsync(top, trigger, paths);
        }
        catch (Exception ex)
        {
            vm.Status = "Drag failed: " + ex.Message;
        }
    }

    private void Tile_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_deferredSelect is { } item && sender is Control { DataContext: ImageResultViewModel released }
            && ReferenceEquals(item, released))
            Vm?.Select(item, false, false);
        _deferredSelect = null;
        _pressArgs = null;
    }

    private void Tile_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // Opens the preview window, which shows the focused image (this one) and follows the selection.
        if (sender is not Control { DataContext: ImageResultViewModel item } || Vm is not { } vm) return;
        vm.Select(item, false, false);
        (Top as MainWindow)?.OpenPreview();
    }

    // ---- Context menu ----

    private async void Open_Click(object? sender, RoutedEventArgs e)
    {
        if (Top is { } top && Vm is { } vm)
            await RunAsync(() => FileActions.OpenAsync(top, vm.SelectedPaths));
    }

    private async void ShowInFolder_Click(object? sender, RoutedEventArgs e)
    {
        // Right-clicking selects the tile if it wasn't selected, so this covers the clicked file too.
        if (Top is { } top && Vm is { SelectedPaths.Count: > 0 } vm)
            await RunAsync(() => FileActions.ShowInFolderAsync(top, vm.SelectedPaths));
    }

    private void FindSimilar_Click(object? sender, RoutedEventArgs e)
    {
        // Right-clicking selects the tile, so the focused image is the one clicked.
        if (Vm?.FocusedItem is { } item) (Top as MainWindow)?.FindSimilar(item.Path);
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e) => await CopySelectionAsync();

    private async void Delete_Click(object? sender, RoutedEventArgs e) => await DeleteSelectionAsync();

    /// <summary>
    /// Moves the selected files to the Recycle Bin / Trash after one confirmation, then drops them from
    /// every tab and index. Files that fail (in use, no Recycle Bin on that drive, …) are kept and listed.
    /// </summary>
    private async System.Threading.Tasks.Task DeleteSelectionAsync()
    {
        if (Top is not Window window || Vm is not { } vm) return;
        if (await FileCommands.DeleteAsync(window, vm.SelectedPaths) is not { } done) return;
        vm.OnFilesDeleted(done.Result.Moved);
        vm.Status = done.Message;
    }

    // ---- Keyboard ----

    private async void Results_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || Top is not { } top) return;
        var cmd = e.KeyModifiers.HasFlag(CommandModifier);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        switch (e.Key)
        {
            case Key.C when cmd:
                e.Handled = true;
                await CopySelectionAsync();
                break;
            case Key.A when cmd:
                e.Handled = true;
                vm.SelectAll();
                break;
            case Key.Delete:
                e.Handled = true;
                await DeleteSelectionAsync();
                break;
            case Key.Enter:
                e.Handled = true;
                await RunAsync(() => FileActions.OpenAsync(top, vm.SelectedPaths));
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                e.Handled = true;
                var (dx, dy) = e.Key switch
                {
                    Key.Left => (-1, 0),
                    Key.Right => (1, 0),
                    Key.Up => (0, -1),
                    _ => (0, 1),
                };
                if (vm.MoveSelection(dx, dy, shift) is { } focused)
                    ResultsList.ScrollIntoView(vm.RowOf(focused));
                break;
        }
    }

    private async System.Threading.Tasks.Task CopySelectionAsync()
    {
        if (Top is not { } top || Vm is not { } vm) return;
        var paths = vm.SelectedPaths;
        if (paths.Count == 0) return;
        await RunAsync(async () => vm.Status = await FileCommands.CopyAsync(top, paths));
    }

    private async System.Threading.Tasks.Task RunAsync(Func<System.Threading.Tasks.Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            if (Vm is { } vm) vm.Status = "Error: " + ex.Message;
        }
    }
}
