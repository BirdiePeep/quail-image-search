using System;

namespace QuailImageSearch.Services.Models;

/// <summary>
/// Port of Pillow's antialiased resize (Image.resize with BICUBIC or BILINEAR), which CLIP-family
/// preprocessing uses. Matching it matters: plain sampling aliases when shrinking photos, which
/// measurably shifts the embeddings.
/// </summary>
public static class PillowResize
{
    public enum Filter { Bilinear, Bicubic }

    private const double A = -0.5; // Pillow's bicubic parameter

    private static double Bilinear(double x)
    {
        x = Math.Abs(x);
        return x < 1 ? 1 - x : 0;
    }

    private static double Bicubic(double x)
    {
        x = Math.Abs(x);
        if (x < 1) return ((A + 2) * x - (A + 3)) * x * x + 1;
        if (x < 2) return (((x - 5) * x + 8) * x - 4) * A;
        return 0;
    }

    /// <summary>Resizes interleaved RGBA bytes; horizontal pass first, then vertical, like Pillow.</summary>
    public static byte[] Resize(byte[] src, int srcW, int srcH, int dstW, int dstH, Filter filter = Filter.Bicubic)
    {
        var tmp = srcW == dstW ? src : Pass(src, srcW, srcH, dstW, horizontal: true, filter);
        return srcH == dstH ? tmp : Pass(tmp, dstW, srcH, dstH, horizontal: false, filter);
    }

    private static byte[] Pass(byte[] src, int w, int h, int outSize, bool horizontal, Filter filter)
    {
        var inSize = horizontal ? w : h;
        var (bounds, weights, kSize) = Coefficients(inSize, outSize, filter);
        var (ow, oh) = horizontal ? (outSize, h) : (w, outSize);
        var dst = new byte[ow * oh * 4];

        System.Threading.Tasks.Parallel.For(0, oh, y =>
        {
            for (var x = 0; x < ow; x++)
            {
                var o = horizontal ? x : y;
                var (min, count) = bounds[o];
                double r = 0, g = 0, b = 0, a = 0;
                for (var k = 0; k < count; k++)
                {
                    var wgt = weights[o * kSize + k];
                    var i = horizontal ? (y * w + min + k) * 4 : ((min + k) * w + x) * 4;
                    r += src[i] * wgt;
                    g += src[i + 1] * wgt;
                    b += src[i + 2] * wgt;
                    a += src[i + 3] * wgt;
                }
                var d = (y * ow + x) * 4;
                dst[d] = Clip(r);
                dst[d + 1] = Clip(g);
                dst[d + 2] = Clip(b);
                dst[d + 3] = Clip(a);
            }
        });
        return dst;
    }

    private static byte Clip(double v) => (byte)Math.Clamp((int)Math.Round(v, MidpointRounding.AwayFromZero), 0, 255);

    /// <summary>Per output pixel: first input index, tap count and normalized weights (Pillow's precompute_coeffs).</summary>
    private static ((int Min, int Count)[] Bounds, double[] Weights, int KSize) Coefficients(int inSize, int outSize, Filter filter)
    {
        Func<double, double> kernel = filter == Filter.Bilinear ? Bilinear : Bicubic;
        var scale = (double)inSize / outSize;
        var filterScale = Math.Max(scale, 1.0);
        var support = (filter == Filter.Bilinear ? 1.0 : 2.0) * filterScale;
        var kSize = (int)Math.Ceiling(support) * 2 + 1;
        var bounds = new (int, int)[outSize];
        var weights = new double[outSize * kSize];
        for (var xx = 0; xx < outSize; xx++)
        {
            var center = (xx + 0.5) * scale;
            var ss = 1.0 / filterScale;
            var xmin = Math.Max((int)(center - support + 0.5), 0);
            var xmax = Math.Min((int)(center + support + 0.5), inSize) - xmin;
            double total = 0;
            for (var x = 0; x < xmax; x++)
            {
                var w = kernel((x + xmin - center + 0.5) * ss);
                weights[xx * kSize + x] = w;
                total += w;
            }
            if (total != 0)
                for (var x = 0; x < xmax; x++) weights[xx * kSize + x] /= total;
            bounds[xx] = (xmin, xmax);
        }
        return (bounds, weights, kSize);
    }
}
