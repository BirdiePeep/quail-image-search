using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuailImageSearch.Services;

namespace QuailImageSearch.ViewModels;

/// <summary>
/// The preview window: shows the focused image of the active tab (the one last clicked or moved to),
/// following the selection as it changes. Left/Right move the tab's selection, so the grid and the
/// preview stay in sync. Neighbouring images are decoded ahead so stepping through is quick.
/// </summary>
public partial class PreviewViewModel : ViewModelBase
{
    /// <summary>Largest side the preview decodes to (the window scales it down further to fit).</summary>
    private const int MaxDecodeSize = 2560;

    private readonly MainViewModel _main;
    private readonly Dictionary<string, Task<Bitmap?>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _rotateGate = new(1, 1);
    private SearchTabViewModel? _tab;

    public PreviewViewModel(MainViewModel main)
    {
        _main = main;
        _main.PropertyChanged += Main_PropertyChanged;
        AttachTab(_main.SelectedTab);
    }

    /// <summary>Call when the window closes.</summary>
    public void Detach()
    {
        _main.PropertyChanged -= Main_PropertyChanged;
        AttachTab(null);
        Image = null;
        foreach (var t in _cache.Values)
            if (t.IsCompletedSuccessfully) t.Result?.Dispose();
        _cache.Clear();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(CanRotate), nameof(RotateTip), nameof(HasItem))]
    public partial ImageResultViewModel? Item { get; private set; }

    [ObservableProperty]
    public partial Bitmap? Image { get; private set; }

    /// <summary>"12 / 240 · score 73 · folder", or a hint when nothing is selected.</summary>
    [ObservableProperty]
    public partial string Info { get; private set; } = "";

    /// <summary>Errors and confirmations (e.g. "Rotated and saved").</summary>
    [ObservableProperty]
    public partial string Message { get; set; } = "";

    /// <summary>The tab whose images are shown.</summary>
    public SearchTabViewModel? Tab => _tab;

    public bool HasItem => Item is not null;

    public string Title => Item is null ? "Preview" : $"{Item.FileName} – Preview";

    public bool CanRotate => Item is not null && ImageRotation.CannotRotateReason(Item.Path) is null;

    public string RotateTip => Item is null ? "" : ImageRotation.CannotRotateReason(Item.Path) ?? "Rotates the image and saves it to the file (JPEG: lossless, only the orientation tag changes)";

    private void Main_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedTab)) AttachTab(_main.SelectedTab);
    }

    private void AttachTab(SearchTabViewModel? tab)
    {
        if (_tab is not null) _tab.FocusedItemChanged -= Refresh;
        _tab = tab;
        if (_tab is not null) _tab.FocusedItemChanged += Refresh;
        Refresh();
    }

    /// <summary>Shows the active tab's focused image (called whenever it or the results change).</summary>
    private void Refresh()
    {
        var item = _tab?.FocusedItem;
        Info = item is null
            ? "Select an image in the results to preview it."
            : $"{item.Index + 1} / {_tab!.ShownCount} · score {item.ScoreText} · {System.IO.Path.GetDirectoryName(item.Path)}";
        if (ReferenceEquals(item, Item) && Image is not null) return;
        Item = item;
        Message = "";
        _ = ShowAsync(item);
    }

    private async Task ShowAsync(ImageResultViewModel? item)
    {
        if (item is null)
        {
            Image = null;
            return;
        }
        var load = Load(item.Path);
        // Decode the neighbours meanwhile, and forget the rest.
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { item.Path };
        foreach (var d in new[] { 1, -1 })
            if (_tab?.ShownAt(item.Index + d) is { } neighbour)
            {
                keep.Add(neighbour.Path);
                _ = Load(neighbour.Path);
            }
        foreach (var stale in _cache.Keys.Where(k => !keep.Contains(k)).ToList())
            Evict(stale);

        var bitmap = await load;
        if (!ReferenceEquals(Item, item)) return; // moved on meanwhile
        Image = bitmap;
        if (bitmap is null) Message = "This file can't be displayed.";
    }

    private Task<Bitmap?> Load(string path)
    {
        if (!_cache.TryGetValue(path, out var task))
        {
            task = Task.Run(() =>
            {
                try { return ImageLoader.Load(path, MaxDecodeSize, MaxDecodeSize); }
                catch (Exception) { return null; }
            });
            _cache[path] = task;
        }
        return task;
    }

    private void Evict(string path)
    {
        if (!_cache.Remove(path, out var task)) return;
        _ = task.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result is { } b && !ReferenceEquals(b, Image))
                Avalonia.Threading.Dispatcher.UIThread.Post(b.Dispose);
        }, TaskScheduler.Default);
    }

    [RelayCommand]
    private void Previous() => _tab?.Step(-1);

    [RelayCommand]
    private void Next() => _tab?.Step(1);

    [RelayCommand]
    private Task RotateLeft() => RotateAsync(clockwise: false);

    [RelayCommand]
    private Task RotateRight() => RotateAsync(clockwise: true);

    /// <summary>Rotates the file on disk, then shows it again and refreshes its thumbnail.</summary>
    private async Task RotateAsync(bool clockwise)
    {
        if (Item is not { } item || !CanRotate) return;
        await _rotateGate.WaitAsync(); // one rotation at a time, so quick clicks add up correctly
        try
        {
            await Task.Run(() => ImageRotation.Rotate(item.Path, clockwise));
            var old = Image;
            _cache.Remove(item.Path);
            var bitmap = await Load(item.Path);
            if (ReferenceEquals(Item, item))
            {
                Image = bitmap;
                Message = $"Rotated {(clockwise ? "right" : "left")} and saved. The image is re-indexed on the next search.";
            }
            if (old is not null && !ReferenceEquals(old, Image)) old.Dispose();
            item.ReloadThumbnail();
        }
        catch (Exception e)
        {
            Message = "Couldn't rotate: " + e.Message;
        }
        finally
        {
            _rotateGate.Release();
        }
    }
}
