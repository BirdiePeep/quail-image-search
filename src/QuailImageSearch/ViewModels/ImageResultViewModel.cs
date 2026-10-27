using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace QuailImageSearch.ViewModels;

public partial class ImageResultViewModel : ObservableObject
{
    // Limits concurrent decodes so scrolling a huge result list doesn't swamp the thread pool.
    private static readonly SemaphoreSlim DecodeLimiter = new(Math.Max(2, Environment.ProcessorCount / 2));

    private int _requestedWidth; // widest thumbnail decoded or being decoded so far

    public ImageResultViewModel(string path, double rawScore, QuailImageSearch.Services.ScoreScale? scale)
    {
        Path = path;
        RawScore = rawScore;
        ApplyScale(scale);
    }

    /// <summary>Position among the currently shown (score-filtered) results.</summary>
    public int Index { get; set; }
    public string Path { get; }
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>The tool's own score.</summary>
    public double RawScore { get; }

    /// <summary>Calibrated 0–100 score, or the raw score if the tool isn't calibrated.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScoreText), nameof(ToolTip))]
    public partial double Score { get; private set; }

    private bool _isScaled;

    public string ScoreText => _isScaled ? Score.ToString("0") : Score.ToString("0.000");

    public string ToolTip => _isScaled
        ? $"{Path}\nScore {Score:0} (raw {RawScore:0.0000})"
        : $"{Path}\nRaw score {RawScore:0.0000} (tool not calibrated)";

    public void ApplyScale(QuailImageSearch.Services.ScoreScale? scale)
    {
        _isScaled = scale is not null;
        Score = scale?.ToDisplay(RawScore) ?? RawScore;
        OnPropertyChanged(nameof(ScoreText));
        OnPropertyChanged(nameof(ToolTip));
    }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    /// <summary>True once a thumbnail was requested (the tile was shown at some point).</summary>
    public bool HasThumbnailRequest => _requestedWidth > 0;

    /// <summary>When the thumbnail was last requested (larger is more recent), for <see cref="ThumbnailCache"/>.</summary>
    public long LastUsed { get; private set; }

    /// <summary>
    /// Called when a tile for this item is shown, and when tiles get bigger: decodes off the UI
    /// thread, only if no thumbnail at least <paramref name="width"/> pixels wide exists yet.
    /// </summary>
    public void EnsureThumbnail(int width)
    {
        LastUsed = ThumbnailCache.Touch();
        if (width <= _requestedWidth) return;
        _requestedWidth = width;
        _ = LoadThumbnailAsync(width);
    }

    /// <summary>Decodes the thumbnail again (e.g. after the file was rotated), at the size already shown.</summary>
    public void ReloadThumbnail()
    {
        if (_requestedWidth == 0) return;
        SetThumbnail(null);
        _ = LoadThumbnailAsync(_requestedWidth);
    }

    /// <summary>Drops the thumbnail to free memory; it's decoded again when the tile is shown.</summary>
    internal void UnloadThumbnail()
    {
        _requestedWidth = 0;
        SetThumbnail(null);
    }

    /// <summary>Replaces the thumbnail, freeing the old one (the renderer keeps its own reference while it needs it).</summary>
    private void SetThumbnail(Bitmap? bitmap)
    {
        var old = Thumbnail;
        if (ReferenceEquals(old, bitmap)) return;
        Thumbnail = bitmap;
        old?.Dispose();
        ThumbnailCache.Resize(this, bitmap is null ? 0 : (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4);
    }

    private async Task LoadThumbnailAsync(int width)
    {
        await DecodeLimiter.WaitAsync();
        try
        {
            var bmp = await Task.Run(() =>
            {
                try
                {
                    // Tiles are square, so the thumbnail never needs to be taller than it is wide.
                    return QuailImageSearch.Services.ImageLoader.Load(Path, width, width); // upright per EXIF
                }
                catch (Exception)
                {
                    return null; // unsupported format (e.g. RAW) or unreadable file: tile shows the name only
                }
            });
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (bmp is null) return;
                // Unloaded meanwhile, or a larger decode already arrived: this one isn't needed.
                if (_requestedWidth == 0 || Thumbnail is not null && bmp.PixelSize.Width < Thumbnail.PixelSize.Width)
                    bmp.Dispose();
                else
                    SetThumbnail(bmp);
            });
        }
        finally
        {
            DecodeLimiter.Release();
        }
    }
}

/// <summary>
/// Caps the memory used by thumbnails across all tabs. A search can return up to 100,000 results,
/// and without a cap every tile ever scrolled past would keep its bitmap. When the total goes over
/// <see cref="Budget"/>, the least recently shown thumbnails are dropped (tiles on screen were shown
/// most recently, so they stay). UI thread only.
/// </summary>
public static class ThumbnailCache
{
    private const long Budget = 256L * 1024 * 1024;

    private static readonly Dictionary<ImageResultViewModel, long> Sizes = new();
    private static long _total;
    private static long _clock;

    /// <summary>Marks the item as just shown; returns its new recency stamp.</summary>
    internal static long Touch() => ++_clock;

    internal static void Resize(ImageResultViewModel item, long bytes)
    {
        _total -= Sizes.GetValueOrDefault(item);
        if (bytes > 0) Sizes[item] = bytes;
        else Sizes.Remove(item);
        _total += bytes;
        if (_total <= Budget) return;
        foreach (var old in Sizes.Keys.Where(k => !ReferenceEquals(k, item)).OrderBy(k => k.LastUsed).ToList())
        {
            if (_total <= Budget * 3 / 4) break; // free a margin, so this doesn't run on every load
            old.UnloadThumbnail();
        }
    }
}
