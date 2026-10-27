using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace QuailImageSearch.Services;

/// <summary>
/// Rotates image files by 90° without losing quality:
/// <list type="bullet">
/// <item>JPEG: only the EXIF Orientation tag changes; the compressed image data is untouched. The
/// tag is updated in place, added to the existing EXIF block, or a minimal EXIF block is added.</item>
/// <item>PNG: the pixels are rotated and saved as PNG again, which is lossless for 8-bit PNGs (the
/// colour profile, gamma and text chunks are copied over). 16-bit PNGs are refused, since they'd lose
/// precision.</item>
/// </list>
/// Other formats can't be rotated without re-encoding (WebP, GIF) or aren't supported for writing
/// (BMP), so they're refused.
/// </summary>
public static class ImageRotation
{
    private static readonly HashSet<string> JpegExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".jpe", ".jfif" };

    /// <summary>Why the file can't be rotated, or null if it can.</summary>
    public static string? CannotRotateReason(string path)
    {
        var ext = Path.GetExtension(path);
        if (JpegExtensions.Contains(ext)) return null;
        if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase))
            return PngBitDepth(path) == 16 ? "Rotating 16-bit PNGs isn't supported (it would lose precision)" : null;
        return $"Rotating {ext.TrimStart('.').ToUpperInvariant()} files isn't supported (only JPEG and PNG can be rotated without losing quality)";
    }

    /// <summary>Rotates the file 90° clockwise or counter-clockwise, replacing it.</summary>
    public static void Rotate(string path, bool clockwise)
    {
        if (CannotRotateReason(path) is { } reason) throw new NotSupportedException(reason);
        var bytes = File.ReadAllBytes(path);
        var rotated = JpegExtensions.Contains(Path.GetExtension(path)) ? RotateJpeg(bytes, clockwise) : RotatePng(bytes, clockwise);
        var tmp = path + ".rotating";
        File.WriteAllBytes(tmp, rotated);
        File.Move(tmp, path, overwrite: true);
    }

    // ---- JPEG: EXIF Orientation tag ----

    private const ushort OrientationTag = 0x0112;

    /// <summary>
    /// The EXIF orientation after turning the displayed image 90° further. Values 1/6/3/8 are
    /// 0°/90°/180°/270° clockwise; 2/7/4/5 are the mirrored versions.
    /// </summary>
    public static int NextOrientation(int orientation, bool clockwise)
    {
        int[] plain = [1, 6, 3, 8], mirrored = [2, 7, 4, 5];
        var cycle = Array.IndexOf(mirrored, orientation) >= 0 ? mirrored : plain;
        var i = Math.Max(0, Array.IndexOf(cycle, orientation)); // unknown values count as 1 (normal)
        return cycle[(i + (clockwise ? 1 : 3)) % 4];
    }

    private static byte[] RotateJpeg(byte[] jpeg, bool clockwise)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) throw new InvalidDataException("not a JPEG file");

        // Find the EXIF block (APP1 starting with "Exif\0\0") among the segments before the image data.
        int pos = 2, insertAt = 2, exifSegment = -1;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF)
        {
            var marker = jpeg[pos + 1];
            if (marker == 0xDA || marker == 0xD9) break; // start of scan / end of image
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2));
            if (marker == 0xE1 && length >= 8 && jpeg.AsSpan(pos + 4, 6).SequenceEqual("Exif\0\0"u8))
            {
                exifSegment = pos;
                break;
            }
            if (marker == 0xE0 && pos == insertAt) insertAt = pos + 2 + length; // keep a leading JFIF block first
            pos += 2 + length;
        }

        if (exifSegment < 0)
        {
            // No EXIF: add a minimal one holding just the orientation.
            var app1 = MinimalExif(NextOrientation(1, clockwise));
            return [.. jpeg.AsSpan(0, insertAt), .. app1, .. jpeg.AsSpan(insertAt)];
        }

        var segLength = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(exifSegment + 2));
        var tiff = exifSegment + 10; // after marker, length and "Exif\0\0"
        var tiffEnd = exifSegment + 2 + segLength;
        var little = jpeg[tiff] == (byte)'I';
        ushort U16(int at) => little ? BinaryPrimitives.ReadUInt16LittleEndian(jpeg.AsSpan(at)) : BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(at));
        uint U32(int at) => little ? BinaryPrimitives.ReadUInt32LittleEndian(jpeg.AsSpan(at)) : BinaryPrimitives.ReadUInt32BigEndian(jpeg.AsSpan(at));

        var ifd0 = tiff + (int)U32(tiff + 4);
        if (ifd0 + 2 > tiffEnd) throw new InvalidDataException("damaged EXIF data");
        var count = U16(ifd0);
        if (ifd0 + 2 + count * 12 + 4 > tiffEnd) throw new InvalidDataException("damaged EXIF data");
        for (var i = 0; i < count; i++)
        {
            var entry = ifd0 + 2 + i * 12;
            if (U16(entry) != OrientationTag) continue;
            // SHORT, count 1: the value sits in the first two bytes of the value field. Change it in place.
            var copy = (byte[])jpeg.Clone();
            var value = (ushort)NextOrientation(U16(entry + 8), clockwise);
            if (little) BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(entry + 8), value);
            else BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(entry + 8), value);
            return copy;
        }

        // EXIF without an Orientation tag: write a copy of IFD0 with the tag added at the end of the
        // EXIF block and point the header at it. Nothing else moves, so every other offset stays valid.
        var entries = Enumerable.Range(0, count).Select(i => jpeg.AsSpan(ifd0 + 2 + i * 12, 12).ToArray()).ToList();
        var orientationEntry = new byte[12];
        void W16(byte[] b, int at, int v) { if (little) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), (ushort)v); else BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at), (ushort)v); }
        void W32(byte[] b, int at, uint v) { if (little) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v); else BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), v); }
        W16(orientationEntry, 0, OrientationTag);
        W16(orientationEntry, 2, 3); // SHORT
        W32(orientationEntry, 4, 1);
        W16(orientationEntry, 8, NextOrientation(1, clockwise));
        entries.Add(orientationEntry);
        entries = entries.OrderBy(e => little ? BinaryPrimitives.ReadUInt16LittleEndian(e) : BinaryPrimitives.ReadUInt16BigEndian(e)).ToList();

        var pad = (tiffEnd - tiff) % 2; // IFDs start on a word boundary
        var newIfdOffset = tiffEnd - tiff + pad;
        var ifd = new byte[pad + 2 + entries.Count * 12 + 4];
        W16(ifd, pad, entries.Count);
        for (var i = 0; i < entries.Count; i++) entries[i].CopyTo(ifd, pad + 2 + i * 12);
        W32(ifd, pad + 2 + entries.Count * 12, U32(ifd0 + 2 + count * 12)); // same next IFD (the thumbnail)
        var newLength = segLength + ifd.Length;
        if (newLength > ushort.MaxValue) throw new InvalidDataException("the EXIF block is too large to add an orientation to");

        var result = new byte[jpeg.Length + ifd.Length];
        jpeg.AsSpan(0, tiffEnd).CopyTo(result);
        ifd.CopyTo(result, tiffEnd);
        jpeg.AsSpan(tiffEnd).CopyTo(result.AsSpan(tiffEnd + ifd.Length));
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(exifSegment + 2), (ushort)newLength);
        W32(result, tiff + 4, (uint)newIfdOffset);
        return result;
    }

    /// <summary>An APP1 EXIF segment (big-endian TIFF) with only an Orientation tag.</summary>
    private static byte[] MinimalExif(int orientation)
    {
        byte[] tiff =
        [
            (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8,        // header, IFD0 at offset 8
            0, 1,                                           // one entry
            0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0, // Orientation, SHORT, 1, value
            0, 0, 0, 0,                                     // no next IFD
        ];
        var length = 2 + 6 + tiff.Length;
        return [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. "Exif\0\0"u8, .. tiff];
    }

    // ---- PNG: rotate pixels, save losslessly ----

    /// <summary>Bit depth from the PNG header, or 0 if it can't be read.</summary>
    private static int PngBitDepth(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            Span<byte> header = stackalloc byte[25];
            return f.Read(header) == 25 && header[12..16].SequenceEqual("IHDR"u8) ? header[24] : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Chunks copied from the original: colour (profile, gamma, primaries), text, EXIF and time. pHYs
    /// (pixel size) is copied only when the pixels are square, since rotating would swap its axes.
    /// </summary>
    private static readonly HashSet<string> KeptChunks = ["iCCP", "sRGB", "gAMA", "cHRM", "tEXt", "zTXt", "iTXt", "eXIf", "tIME", "pHYs"];

    private static IEnumerable<(string Type, int Start, int Length)> Chunks(byte[] png)
    {
        for (var pos = 8; pos + 12 <= png.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos));
            if (length < 0 || pos + 12 + length > png.Length) yield break;
            yield return (System.Text.Encoding.ASCII.GetString(png, pos + 4, 4), pos, 12 + length);
            pos += 12 + length;
        }
    }

    /// <summary>The encoded PNG with the original's metadata chunks in place of the encoder's own.</summary>
    private static byte[] CopyMetadata(byte[] original, byte[] encoded)
    {
        var kept = Chunks(original).Where(c => KeptChunks.Contains(c.Type)
                                               && (c.Type != "pHYs" || original.AsSpan(c.Start + 8, 4).SequenceEqual(original.AsSpan(c.Start + 12, 4))))
                                   .ToList();
        var output = new MemoryStream();
        output.Write(encoded, 0, 8); // signature
        foreach (var c in Chunks(encoded))
        {
            if (KeptChunks.Contains(c.Type)) continue;
            output.Write(encoded, c.Start, c.Length);
            if (c.Type == "IHDR")
                foreach (var k in kept) output.Write(original, k.Start, k.Length); // must come before PLTE/IDAT
        }
        return output.ToArray();
    }

    private static byte[] RotatePng(byte[] png, bool clockwise) => CopyMetadata(png, RotatePngPixels(png, clockwise));

    private static byte[] RotatePngPixels(byte[] png, bool clockwise)
    {
        using var codec = SKCodec.Create(new MemoryStream(png)) ?? throw new InvalidDataException("not a readable PNG file");
        using var gate = DecodeLimits.Enter(codec.Info.Width, codec.Info.Height)
                         ?? throw new InvalidDataException("the image is too large to rotate");
        using var bitmap = new SKBitmap(codec.Info.WithColorType(SKColorType.Rgba8888).WithAlphaType(SKAlphaType.Unpremul));
        var r = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (r != SKCodecResult.Success) throw new InvalidDataException($"couldn't decode the PNG ({r})");
        // Move the pixels directly (exact, alpha untouched) rather than drawing through a canvas.
        var (w, h) = (bitmap.Width, bitmap.Height);
        using var rotated = new SKBitmap(bitmap.Info.WithSize(h, w));
        unsafe
        {
            var src = new ReadOnlySpan<uint>((void*)bitmap.GetPixels(), w * h);
            var dst = new Span<uint>((void*)rotated.GetPixels(), w * h);
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    dst[clockwise ? x * h + (h - 1 - y) : (w - 1 - x) * h + y] = src[y * w + x];
        }
        using var data = rotated.Encode(SKEncodedImageFormat.Png, 100) ?? throw new IOException("couldn't encode the PNG");
        return data.ToArray();
    }
}
