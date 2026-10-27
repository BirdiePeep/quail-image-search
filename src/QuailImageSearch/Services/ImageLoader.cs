using System;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using QuailImageSearch.Services.Models;
using SkiaSharp;

namespace QuailImageSearch.Services;

/// <summary>
/// Decodes image files for display (thumbnails and the preview), turned upright per their EXIF
/// orientation. Avalonia's own decoder ignores EXIF orientation, so rotated photos (and images
/// rotated with the preview's rotate buttons) would otherwise show sideways.
/// </summary>
public static class ImageLoader
{
    /// <summary>
    /// Decodes <paramref name="path"/> to fit within <paramref name="maxWidth"/> x <paramref name="maxHeight"/>
    /// (never enlarged). Returns null if the file can't be decoded.
    /// </summary>
    public static Bitmap? Load(string path, int maxWidth, int maxHeight)
    {
        using var codec = SKCodec.Create(path);
        if (codec is null) return null;

        var origin = codec.EncodedOrigin;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var (w, h) = (codec.Info.Width, codec.Info.Height);
        var (uprightW, uprightH) = swap ? (h, w) : (w, h);
        var scale = Math.Min(1.0, Math.Min(maxWidth / (double)uprightW, maxHeight / (double)uprightH));

        // JPEG decodes at 1/2, 1/4 or 1/8 scale almost for free; the rest is resized below.
        var decodeSize = codec.GetScaledDimensions((float)scale);
        if (decodeSize.Width < Math.Round(w * scale) || decodeSize.Height < Math.Round(h * scale))
            decodeSize = codec.Info.Size; // the nearest cheap scale is too small: decode fully, resize below
        using var gate = DecodeLimits.Enter(decodeSize.Width, decodeSize.Height);
        if (gate is null) return null; // too big to decode safely
        using var decoded = new SKBitmap(new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;

        var upright = ClipModel.ApplyOrientation(decoded, origin);
        try
        {
            var targetW = Math.Max(1, (int)Math.Round(uprightW * scale));
            var targetH = Math.Max(1, (int)Math.Round(uprightH * scale));
            SKBitmap final = upright;
            if (upright.Width > targetW * 1.05 || upright.Height > targetH * 1.05)
                final = upright.Resize(upright.Info.WithSize(targetW, targetH), new SKSamplingOptions(SKCubicResampler.Mitchell)) ?? upright;
            try
            {
                return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, final.GetPixels(),
                    new PixelSize(final.Width, final.Height), new Vector(96, 96), final.RowBytes);
            }
            finally
            {
                if (!ReferenceEquals(final, upright)) final.Dispose();
            }
        }
        finally
        {
            if (!ReferenceEquals(upright, decoded)) upright.Dispose();
        }
    }
}

/// <summary>
/// Keeps huge images from exhausting memory. Only JPEG and WebP can be decoded at a reduced size, so
/// a large PNG, GIF or BMP is decoded in full (a 30,000 x 30,000 PNG needs 3.6 GB), and indexing and
/// thumbnails decode several images at once. Images above <see cref="MaxPixels"/> are skipped; large
/// ones are decoded two at a time.
/// </summary>
public static class DecodeLimits
{
    /// <summary>Largest decode allowed: 100 megapixels (400 MB).</summary>
    public const long MaxPixels = 100_000_000;

    /// <summary>Decodes above this size wait for <see cref="LargeGate"/>.</summary>
    private const long LargePixels = 16_000_000;

    private static readonly SemaphoreSlim LargeGate = new(2, 2);

    /// <summary>
    /// Call before allocating a <paramref name="width"/> x <paramref name="height"/> decode buffer and
    /// dispose the result afterwards. Returns null if the image is too big to decode.
    /// </summary>
    public static IDisposable? Enter(int width, int height)
    {
        var pixels = (long)width * height;
        if (pixels > MaxPixels) return null;
        if (pixels <= LargePixels) return Nothing.Instance;
        LargeGate.Wait();
        return new Release();
    }

    private sealed class Release : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) LargeGate.Release(); }
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }
}
