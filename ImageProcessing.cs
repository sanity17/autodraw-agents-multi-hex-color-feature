using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Media;
using SkiaSharp;
using Autodraw.Configuration;
using Autodraw.Painting;

namespace Autodraw;

public static class ImageProcessing
{
    private static long Process_MemPressure;

    private static readonly Pattern patternCrosshatch = new()
    {
        Pat =
            "0 0 0 1 0 0 0\n0 0 0 1 0 0 0\n0 0 0 1 0 0 0\n1 1 1 1 1 1 1\n0 0 0 1 0 0 0\n0 0 0 1 0 0 0\n0 0 0 1 0 0 0\n",
        Width = 7, Height = 7
    };

    private static readonly Pattern patternDiagCross = new()
    {
        Pat =
            "1 0 0 0 0 0 1\n0 1 0 0 0 1 0\n0 0 1 0 1 0 0\n0 0 0 1 0 0 0\n0 0 1 0 1 0 0\n0 1 0 0 0 1 0\n1 0 0 0 0 0 1\n",
        Width = 6,
        Height = 6
    };

    private static readonly List<int[]> listCrosshatch = ReadPattern(patternCrosshatch.Pat);
    private static readonly List<int[]> listDiagCross = ReadPattern(patternDiagCross.Pat);

    public static Filters _currentFilters = new()
        { Invert = false, MaxThreshold = 127, AlphaThreshold = 200 };

    public static List<int[]> ReadPattern(string pat)
    {
        var lines = pat.Split('\n');

        List<int[]> positions = new();

        for (var i = 0; i < lines.Length; i++)
        {
            var numbers = lines[i].Split(' ');

            for (var j = 0; j < numbers.Length; j++)
                if (numbers[j] == "1")
                    positions.Add(new[] { j, i });
        }

        return positions;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint MakePixel(byte red, byte green, byte blue, byte alpha)
    {
        return (uint)((alpha << 24) | (blue << 16) | (green << 8) | red);
    }

    private static unsafe void GetPixel(uint* input, out byte red, out byte green, out byte blue, out byte alpha)
        => GetPixel(input, rgba: true, out red, out green, out blue, out alpha);

    /// <summary>Reads a packed 8888 pixel in the bitmap's actual channel order.  The parameterless
    /// overload assumes RGBA; BGRA sources (Skia's Windows default) need the explicit flag or red
    /// and blue are swapped for every colour-sensitive pass.</summary>
    private static unsafe void GetPixel(uint* input, bool rgba, out byte red, out byte green, out byte blue, out byte alpha)
    {
        var value = *input;
        if (rgba)
        {
            red = (byte)(value & 0xFF);
            green = (byte)((value >> 8) & 0xFF);
            blue = (byte)((value >> 16) & 0xFF);
        }
        else
        {
            blue = (byte)(value & 0xFF);
            green = (byte)((value >> 8) & 0xFF);
            red = (byte)((value >> 16) & 0xFF);
        }
        alpha = (byte)((value >> 24) & 0xFF);
    }

    public static byte AdjustThresh(byte rByte, byte gByte, byte bByte, byte aByte, bool doinvert, float maxthresh, float minthresh, byte athresh)
    {
        float lumen = (rByte + gByte + bByte) / 3;
        byte localThresh = (byte)(lumen > maxthresh || lumen < minthresh || aByte < athresh ? 255 : 0);
        localThresh = doinvert == false ? localThresh : (byte)(255 - localThresh);
        return localThresh;
    }

    private static byte GetThreshold(float luminosity, byte alphaByte, Filters filterSettings)
    {
        byte threshByte =
            (byte)(luminosity > filterSettings.MaxThreshold || luminosity < filterSettings.MinThreshold || alphaByte < filterSettings.AlphaThreshold
                ? 255
                : 0);
        return filterSettings.Invert == false ? threshByte : (byte)(255 - threshByte);
    }

    private static unsafe byte GetOutlineAlpha(int y, int x, uint* basePtr, int width, int height, byte threshByte, Filters filterSettings)
    {
        byte returnByte = 255;
        var doOutline = false;
        foreach (var i in Enumerable.Range(0, 4))
        {
            uint* pixelAddress;
            var outOfBounds = i switch
            {
                0 => y - 1 < 0,
                1 => x - 1 < 0,
                2 => x + 1 >= width,
                3 => y + 1 >= height,
                _ => false
            };
            if (outOfBounds)
            {
                doOutline = true;
            }
            else
            {
                pixelAddress = i switch
                {
                    0 => basePtr + width * (y - 1) + x,
                    1 => basePtr + width * y + (x - 1),
                    2 => basePtr + width * y + (x + 1),
                    3 => basePtr + width * (y + 1) + x,
                    _ => throw new ArgumentException($"Invalid value {i}")
                };
                GetPixel(pixelAddress, out var rByte, out var gByte, out var bByte, out var aByte);
                byte localThresh = AdjustThresh(rByte, gByte, bByte, aByte, filterSettings.Invert, filterSettings.MaxThreshold, filterSettings.MinThreshold, filterSettings.AlphaThreshold);
                if (localThresh == 255) doOutline = true;
            }
        }
        if (doOutline) returnByte = 0;
        return returnByte;
    }

    private static byte GetPatternAlpha(int y, int x, byte returnByte, Filters filterSettings)
    {
        if (returnByte != 0 && (filterSettings.Crosshatch || filterSettings.DiagCrosshatch || filterSettings.HorizontalLines > 0 || filterSettings.VerticalLines > 0))
        {
            if (filterSettings.DiagCrosshatch)
                foreach (var patPoint in listDiagCross)
                    if (x % patternDiagCross.Width == patPoint[0] && y % patternDiagCross.Height == patPoint[1])
                        returnByte = 0;
            if (filterSettings.Crosshatch)
                foreach (var patPoint in listCrosshatch)
                    if (x % patternCrosshatch.Width == patPoint[0] && y % patternCrosshatch.Height == patPoint[1])
                        returnByte = 0;
        }
        return returnByte;
    }

    private static bool IsAnyPatternSet(this Filters filters)
    {
        return filters.Crosshatch || filters.DiagCrosshatch;
    }

    private static unsafe SKBitmap GenerateBorder(int thickness, int width, int height)
    {
        SKBitmap borderBitmap = new SKBitmap(width, height);

        var srcPtr = (byte*)borderBitmap.GetPixels().ToPointer();

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            byte returnByte = 255;
            if (x < thickness || x >= width - thickness ||
                y < thickness || y >= height - thickness) returnByte = 0;

            *srcPtr++ = returnByte;
            *srcPtr++ = returnByte;
            *srcPtr++ = returnByte;
            *srcPtr++ = 255;
        }

        return borderBitmap;
    }

    private static unsafe SKBitmap GeneratePattern(decimal horizontal, decimal vertical, int width, int height)
    {
        SKBitmap patternBitmap = new SKBitmap(width, height);

        var srcPtr = (byte*)patternBitmap.GetPixels().ToPointer();

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            byte returnByte = 255;
            if (horizontal > 0)
                if (y % horizontal == 0)
                    returnByte = 0;
            if (vertical > 0)
                if (x % vertical == 0)
                    returnByte = 0;

            *srcPtr++ = returnByte;
            *srcPtr++ = returnByte;
            *srcPtr++ = returnByte;
            *srcPtr++ = 255;
        }

        return patternBitmap;
    }

    public static unsafe SKBitmap Process(SKBitmap sourceBitmap, Filters filterSettings)
    {
        if (Process_MemPressure > 0) GC.RemoveMemoryPressure(Process_MemPressure);
        Process_MemPressure = sourceBitmap.BytesPerPixel * sourceBitmap.Width * sourceBitmap.Height;
        var height = sourceBitmap.Height;
        var width = sourceBitmap.Width;

        SKBitmap outputBitmap = new(width, height);

        var basePtr = (uint*)sourceBitmap.GetPixels().ToPointer();
        var returnPtr = (uint*)outputBitmap.GetPixels().ToPointer();
        var isPatSet = filterSettings.IsAnyPatternSet();
        var outline = filterSettings.Outline;

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var srcPtr = basePtr + width * y + x;
            GetPixel(srcPtr, out var redByte, out var greenByte, out var blueByte, out var alphaByte);
            var luminosity = (redByte + greenByte + blueByte) / 3;
            byte threshByte = GetThreshold(luminosity, alphaByte, filterSettings);

            if (threshByte == 255)
            {
                *returnPtr++ = 0xffffffff;
                continue;
            }

            byte returnByte = 255;
            if (outline)
            {
                returnByte = GetOutlineAlpha(y, x, basePtr, width, height, threshByte, filterSettings);
            }

            if (isPatSet)
            {
                returnByte = GetPatternAlpha(y, x, returnByte, filterSettings);
            }
            else if (!outline)
            {
                returnByte = threshByte;
            }
            *returnPtr++ = MakePixel(returnByte, returnByte, returnByte, 255);
        }

        var invert = new float[20] {
            -1f, 0f,  0f,  0f,  1f,
            0f,  -1f, 0f,  0f,  1f,
            0f,  0f,  -1f, 0f,  1f,
            0f,  0f,  0f,  1f,  0f
        };

        SKImage InvertImage(SKImage image)
        {
            return image.ApplyImageFilter(SKImageFilter.CreateColorFilter(SKColorFilter.CreateColorMatrix(invert)), new SKRectI(0, 0, width, height), new SKRectI(0, 0, width, height), out _, out SKPoint _);
        }

        var OutImage = SKImage.FromBitmap(outputBitmap);
        if (filterSettings.ErosionAdvanced > 0)
        {
            var OutImageAnti = OutImage.ApplyImageFilter(
                SKImageFilter.CreateDilate(filterSettings.ErosionAdvanced, filterSettings.ErosionAdvanced),
                new SKRectI(0, 0, width, height),
                new SKRectI(filterSettings.ErosionAdvanced, filterSettings.ErosionAdvanced, width - filterSettings.ErosionAdvanced, height - filterSettings.ErosionAdvanced),
                out _,
                out SKPoint _
            );
            OutImage = OutImageAnti;
        }

        if (filterSettings.InlineAdvanced > 0 || filterSettings.InlineBorderAdvanced > 0 || filterSettings.OutlineAdvanced > 0 ||
            filterSettings.HorizontalLines > 0 || filterSettings.VerticalLines > 0 || filterSettings.BorderAdvanced > 0)
        {
            SKImageInfo imageInfo = new SKImageInfo(OutImage.Width, OutImage.Height);
            using SKSurface primarySurface = SKSurface.Create(imageInfo);

            SKPaint lightenPaint = new SKPaint();
            lightenPaint.BlendMode = SKBlendMode.Lighten;
            SKPaint darkenPaint = new SKPaint();
            darkenPaint.BlendMode = SKBlendMode.Darken;

            bool hasAppliedAnother = false;

            if (filterSettings.InlineAdvanced > 0)
            {
                using SKPaint inlinePaint = new SKPaint();
                inlinePaint.ImageFilter = SKImageFilter.CreateDilate(filterSettings.InlineAdvanced, filterSettings.InlineAdvanced);
                primarySurface.Canvas.DrawImage(OutImage, 0, 0, inlinePaint);
                primarySurface.Canvas.DrawImage(InvertImage(primarySurface.Snapshot()), 0, 0);
                primarySurface.Canvas.DrawImage(OutImage, 0, 0, lightenPaint);
                hasAppliedAnother = true;
            }

            if (filterSettings.OutlineAdvanced > 0)
            {
                using SKPaint outlinePaint = new SKPaint();
                outlinePaint.ImageFilter = SKImageFilter.CreateDilate(filterSettings.OutlineAdvanced, filterSettings.OutlineAdvanced);
                using SKSurface secondarySurface = SKSurface.Create(imageInfo);
                secondarySurface.Canvas.DrawImage(InvertImage(OutImage), 0, 0, outlinePaint);
                secondarySurface.Canvas.DrawImage(InvertImage(secondarySurface.Snapshot()), 0, 0);
                secondarySurface.Canvas.DrawImage(InvertImage(OutImage), 0, 0, lightenPaint);
                primarySurface.Canvas.DrawImage(secondarySurface.Snapshot(), 0, 0, darkenPaint);
                hasAppliedAnother = true;
            }

            if (filterSettings.InlineBorderAdvanced > 0)
            {
                using SKSurface secondarySurface = SKSurface.Create(imageInfo);
                secondarySurface.Canvas.DrawImage(OutImage, 0, 0);
                secondarySurface.Canvas.DrawImage(SKImage.FromBitmap(GenerateBorder(filterSettings.InlineBorderAdvanced, width, height)), 0, 0, lightenPaint);
                primarySurface.Canvas.DrawImage(secondarySurface.Snapshot(), 0, 0, darkenPaint);
                hasAppliedAnother = true;
            }

            if (filterSettings.HorizontalLines > 0 || filterSettings.VerticalLines > 0)
            {
                filterSettings.HorizontalLines = Math.Min(4096, filterSettings.HorizontalLines);
                filterSettings.VerticalLines = Math.Min(4096, filterSettings.VerticalLines);

                SKBitmap patternBitmap = GeneratePattern(filterSettings.HorizontalLines, filterSettings.VerticalLines, width, height);
                using SKSurface secondarySurface = SKSurface.Create(imageInfo);

                secondarySurface.Canvas.DrawBitmap(patternBitmap, 0, 0);
                secondarySurface.Canvas.DrawImage(OutImage, 0, 0, lightenPaint);

                primarySurface.Canvas.DrawImage(secondarySurface.Snapshot(), 0, 0, darkenPaint);
                hasAppliedAnother = true;
            }

            if (filterSettings.BorderAdvanced > 0)
            {
                if (!hasAppliedAnother)
                {
                    primarySurface.Canvas.DrawImage(OutImage, 0, 0);
                }
                primarySurface.Canvas.DrawImage(SKImage.FromBitmap(GenerateBorder(filterSettings.BorderAdvanced, width, height)), 0, 0, darkenPaint);
            }

            SKImage MergeImage = primarySurface.Snapshot();
            OutImage = MergeImage;
        }

        outputBitmap = SKBitmap.FromImage(OutImage);

        GC.AddMemoryPressure(Process_MemPressure);
        return outputBitmap;
    }

    /// <summary>
    /// Converts an RGBA source bitmap into a 0/255 black/white mask bitmap suitable for the
    /// existing drawing path.  This is intentionally a single pass - the previous implementation
    /// re-thresholded an already binary mask through the full filter pipeline.
    /// </summary>
    public static unsafe SKBitmap ProcessLayerMask(SKBitmap sourceBitmap, Filters filterSettings)
    {
        var width = sourceBitmap.Width;
        var height = sourceBitmap.Height;
        var maskBitmap = new SKBitmap(width, height);
        var srcPtr = (uint*)sourceBitmap.GetPixels().ToPointer();
        var dstPtr = (uint*)maskBitmap.GetPixels().ToPointer();

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            GetPixel(srcPtr++, out _, out _, out _, out var alphaByte);
            *dstPtr++ = alphaByte >= filterSettings.AlphaThreshold
                ? MakePixel(0, 0, 0, 255)
                : MakePixel(255, 255, 255, 255);
        }

        // Apply any user-configured outline / pattern / advanced filters on top of the binary mask.
        var maskFilters = new Filters
        {
            AlphaThreshold = 0,
            MaxThreshold = 127,
            MinThreshold = 0,
            Crosshatch = filterSettings.Crosshatch,
            DiagCrosshatch = filterSettings.DiagCrosshatch,
            HorizontalLines = filterSettings.HorizontalLines,
            VerticalLines = filterSettings.VerticalLines,
            Invert = false,
            Outline = filterSettings.Outline,
            BorderAdvanced = filterSettings.BorderAdvanced,
            OutlineAdvanced = filterSettings.OutlineAdvanced,
            InlineAdvanced = filterSettings.InlineAdvanced,
            InlineBorderAdvanced = filterSettings.InlineBorderAdvanced,
            ErosionAdvanced = filterSettings.ErosionAdvanced
        };

        var processedBitmap = Process(maskBitmap, maskFilters);
        maskBitmap.Dispose();
        return processedBitmap;
    }

    public static unsafe Color GetColor(SKBitmap sourceBitmap)
    {
        var width = sourceBitmap.Width;
        var height = sourceBitmap.Height;
        var srcPtr = (byte*)sourceBitmap.GetPixels().ToPointer();

        for (var row = 0; row < height; row++)
        for (var col = 0; col < width; col++)
        {
            var b = *srcPtr++;
            var g = *srcPtr++;
            var r = *srcPtr++;
            var a = *srcPtr++;

            if (a == 255)
            {
                return Color.FromArgb(a, r, g, b);
            }
        }
        return Color.FromArgb(0, 0, 0, 0);
    }

    public static unsafe SKBitmap NormalizeColor(this SKBitmap SourceBitmap)
    {
        var srcColor = SourceBitmap.ColorType;
        var srcAlpha = SourceBitmap.AlphaType;

        if (srcColor == SKColorType.Bgra8888) return SourceBitmap;

        SKBitmap OutputBitmap = new(SourceBitmap.Width, SourceBitmap.Height);

        var srcPtr = (byte*)SourceBitmap.GetPixels().ToPointer();
        var dstPtr = (byte*)OutputBitmap.GetPixels().ToPointer();

        var width = OutputBitmap.Width;
        var height = OutputBitmap.Height;

        var outColor = OutputBitmap.ColorType;

        for (var row = 0; row < height; row++)
        for (var col = 0; col < width; col++)
            if (srcColor == SKColorType.Gray8 || srcColor == SKColorType.Alpha8)
            {
                var b = *srcPtr++;
                *dstPtr++ = b;
                *dstPtr++ = b;
                *dstPtr++ = b;
                *dstPtr++ = 255;
            }
            else if (srcColor == SKColorType.Rgba8888)
            {
                var r = *srcPtr++;
                var g = *srcPtr++;
                var b = *srcPtr++;
                var a = *srcPtr++;
                *dstPtr++ = b;
                *dstPtr++ = g;
                *dstPtr++ = r;
                *dstPtr++ = a;
            }
            else if (srcColor == SKColorType.Argb4444)
            {
                var r = *srcPtr++;
                var g = *srcPtr++;
                var b = *srcPtr++;
                var a = *srcPtr++;
                *dstPtr++ = (byte)(b * 2);
                *dstPtr++ = (byte)(g * 2);
                *dstPtr++ = (byte)(r * 2);
                *dstPtr++ = (byte)(a * 2);
            }

        SourceBitmap.Dispose();
        SourceBitmap = OutputBitmap;

        return SourceBitmap;
    }

    public class Filters
    {
        public byte AlphaThreshold = 127;
        public byte MaxThreshold = 127;
        public byte MinThreshold = 0;

        public bool Crosshatch = false;
        public bool DiagCrosshatch = false;
        public decimal HorizontalLines = 0;
        public decimal VerticalLines = 0;

        public bool Invert = false;
        public bool Outline = false;

        public int BorderAdvanced = 5;
        public int OutlineAdvanced = 0;
        public int InlineAdvanced = 0;
        public int InlineBorderAdvanced = 0;
        public int ErosionAdvanced = 0;
    }

    public class Pattern
    {
        public int Height = 2;
        public string Pat = "0 0\n0 0";
        public int Width = 2;
    }

    // ---------------------------------------------------------------------
    // Palette extraction
    // ---------------------------------------------------------------------

    /// <summary>
    /// Produces the palette used by the colour-layer workflow.
    /// Histogram -> (median cut | KMeans++) -> merge-to-target-size.  All colour comparisons
    /// go through <see cref="Autodraw.Painting.ColorMatcher.DeltaE2000Lab"/> so this matches the
    /// layer assignment pass exactly.
    /// </summary>
    public static unsafe List<SKColor> ExtractPalette(SKBitmap sourceBitmap, int paletteSize, byte alphaThreshold, AutoDrawConfig? config = null, bool preprocess = true)
    {
        ArgumentNullException.ThrowIfNull(sourceBitmap);
        paletteSize = Math.Clamp(paletteSize, 1, 64);

        // A captured target palette is a hard colour constraint, not a KMeans seed. Every captured
        // swatch is returned, even when the user asked for fewer palette colours: filtering them
        // would invent colours that do not exist in the target application. LxPainter's
        // Palette Match path also uses len(palette_rgb) as k_actual.
        if (config?.CapturedPalette is { Count: > 0 })
        {
            var fixedPalette = config.CapturedPalette
                .Select(value => SKColor.TryParse(value, out var color) ? color : SKColors.Transparent)
                .Where(color => color != SKColors.Transparent)
                .Distinct()
                .ToList();
            if (fixedPalette.Count > 0) return fixedPalette;
        }

        // LxPainter applies the LAB spatial filter and SEEDS superpixels to the pixels used for
        // clustering, not just to the palette.  The analyzer calls this same helper so masks and
        // palette are produced from the identical image.
        SKBitmap? preprocessing = null;
        var prepared = sourceBitmap;
        if (preprocess)
        {
            preprocessing = PrepareQuantizationSource(sourceBitmap, config);
            prepared = preprocessing;
        }

        try
        {
            var histogram = BuildHistogram(prepared, alphaThreshold, config);
            if (histogram.Count == 0) return new();

            // Scale candidate count with actual unique-color diversity, but never exceed the histogram.
            var candidateBudget = Math.Max(paletteSize * 8, Math.Min(histogram.Count, paletteSize * 64));
            var candidates = histogram
                .OrderByDescending(entry => entry.Value)
                .Take(candidateBudget)
                .Select(entry =>
                {
                    var red = ((entry.Key >> 10) & 31) * 8 + 4;
                    var green = ((entry.Key >> 5) & 31) * 8 + 4;
                    var blue = (entry.Key & 31) * 8 + 4;
                    return (Color: new SKColor((byte)red, (byte)green, (byte)blue), Weight: entry.Value);
                })
                .ToList();

            // If the image genuinely has fewer distinct colours than the requested palette, return
            // exactly those colours rather than inventing centroids from noise.
            if (candidates.Count <= paletteSize)
                return candidates.Select(c => c.Color).Distinct().ToList();

            var quantize = (config?.QuantizeMethod ?? string.Empty).Trim();

            // Each LxPainter quantizer keeps its own algorithm.  Previously Wu, NeuQuant, Lab and
            // MiniBatch were silently collapsed into KMeans/Div/MedianCut.
            if (quantize.Contains("wu", StringComparison.OrdinalIgnoreCase))
                return WuQuantizer(candidates, paletteSize);
            if (quantize.Contains("neuquant", StringComparison.OrdinalIgnoreCase))
                return NeuQuantQuantizer(candidates, paletteSize);
            if (quantize.Contains("median", StringComparison.OrdinalIgnoreCase))
                return MedianCut(candidates, paletteSize);
            if (quantize.Contains("div", StringComparison.OrdinalIgnoreCase) ||
                quantize.Contains("lcquant", StringComparison.OrdinalIgnoreCase))
                return DivQuantizer(candidates, paletteSize);
            if (quantize.Contains("minibatch", StringComparison.OrdinalIgnoreCase))
                return KMeans(candidates, paletteSize, config, miniBatch: true);

            // KMeans++ / KMeans (Random) / Lab Color Space / CIELAB / OKLAB / RGB all use KMeans in
            // the colour space selected by the quantizer name (or an explicit assignment override).
            return KMeans(candidates, paletteSize, config);
        }
        finally
        {
            preprocessing?.Dispose();
        }
    }

    /// <summary>
    /// Applies the preprocessing that LxPainter performs before clustering: LAB spatial
    /// smoothing/edge blending for the "Lab Color Space" quantizer and its SLIC-style SEEDS
    /// superpixels when enabled. The caller owns the returned bitmap.
    /// </summary>
    public static SKBitmap PrepareQuantizationSource(SKBitmap source, AutoDrawConfig? config)
    {
        ArgumentNullException.ThrowIfNull(source);
        var current = source.Copy();
        if (config is null) return current;

        var method = config.QuantizeMethod ?? string.Empty;
        if (method.Contains("lab color", StringComparison.OrdinalIgnoreCase))
        {
            var filtered = ApplyLabSpatialFeatures(current, config);
            current.Dispose();
            current = filtered;
        }

        // Reference only skips SEEDS for the external Lcquant executable.
        if (config.UseSeeds && !method.Contains("lcquant", StringComparison.OrdinalIgnoreCase))
        {
            var seeds = ApplySeedsSuperpixels(current, config.SeedsCount);
            current.Dispose();
            current = seeds;
        }

        return current;
    }

    private static unsafe Dictionary<int, int> BuildHistogram(SKBitmap sourceBitmap, byte alphaThreshold, AutoDrawConfig? config)
    {
        var histogram = new Dictionary<int, int>();
        var sourcePtr = (uint*)sourceBitmap.GetPixels().ToPointer();
        var rgba = sourceBitmap.ColorType == SKColorType.Rgba8888;
        var width = sourceBitmap.Width;
        var height = sourceBitmap.Height;
        var pixelCount = width * height;

        // SEEDS is a real superpixel pass in PrepareQuantizationSource; it must not be
        // approximated here by sampling every Nth pixel.
        for (var i = 0; i < pixelCount; i++)
        {
            GetPixel(sourcePtr + i, rgba, out var red, out var green, out var blue, out var alpha);
            if (alpha < alphaThreshold) continue;
            var redBin = red >> 3;
            var greenBin = green >> 3;
            var blueBin = blue >> 3;
            var key = (redBin << 10) | (greenBin << 5) | blueBin;
            histogram[key] = histogram.GetValueOrDefault(key) + 1;
        }
        return histogram;
    }

    private static List<SKColor> KMeans(List<(SKColor Color, int Weight)> candidates, int paletteSize, AutoDrawConfig? config, bool miniBatch = false)
    {
        var candidateCoords = new (double X, double Y, double Z)[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
            candidateCoords[i] = ToColorSpace(candidates[i].Color, config);

        var usePlusPlus = !string.Equals(config?.KmeansAlgorithm, "KMeans (Random)", StringComparison.OrdinalIgnoreCase);
        var random = new Random(0);

        var centroids = new List<(double X, double Y, double Z)>();

        if (usePlusPlus)
        {
            var totalWeight = candidates.Sum(c => (long)c.Weight);
            if (totalWeight <= 0) totalWeight = 1;
            var pick = random.NextInt64(totalWeight);
            long cumulative = 0;
            for (var i = 0; i < candidates.Count; i++)
            {
                cumulative += candidates[i].Weight;
                if (cumulative > pick)
                {
                    centroids.Add(candidateCoords[i]);
                    break;
                }
            }
        }
        if (centroids.Count == 0)
            centroids.Add(candidateCoords[0]);

        while (centroids.Count < paletteSize && centroids.Count < candidates.Count)
        {
            var distances = new double[candidates.Count];
            var totalDist = 0.0;
            for (var i = 0; i < candidates.Count; i++)
            {
                double minDist = double.MaxValue;
                foreach (var centroid in centroids)
                {
                    var d = WeightedSpaceDistance(candidateCoords[i].X, candidateCoords[i].Y, candidateCoords[i].Z, centroid.X, centroid.Y, centroid.Z, config);
                    if (d < minDist) minDist = d;
                }
                distances[i] = candidates[i].Weight * Math.Max(minDist, 0.0);
                totalDist += distances[i];
            }

            if (totalDist <= 0) break;

            if (usePlusPlus)
            {
                var pick = random.NextDouble() * totalDist;
                double cumulative = 0;
                var added = false;
                for (var i = 0; i < candidates.Count; i++)
                {
                    cumulative += distances[i];
                    if (cumulative >= pick)
                    {
                        centroids.Add(candidateCoords[i]);
                        added = true;
                        break;
                    }
                }
                if (!added)
                    centroids.Add(candidateCoords[^1]);
            }
            else
            {
                centroids.Add(candidateCoords[random.Next(candidates.Count)]);
            }
        }

        const int maxIterations = 64;
        const double convergenceThreshold = 0.5;
        var miniBatchSize = Math.Clamp(candidates.Count / 8, 32, 512);
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var xTotals = new double[centroids.Count];
            var yTotals = new double[centroids.Count];
            var zTotals = new double[centroids.Count];
            var weights = new long[centroids.Count];

            if (miniBatch)
            {
                // MiniBatchKMeans updates from a random sample per iteration with a decaying
                // learning rate instead of making a full pass over every candidate.
                var sampleCount = Math.Min(miniBatchSize, candidates.Count);
                var learningRate = 1.0 / (1.0 + iteration * 0.15);
                for (var s = 0; s < sampleCount; s++)
                {
                    var i = random.Next(candidates.Count);
                    var closest = ClosestSpaceCentroid(candidateCoords[i], centroids, config);
                    var weight = Math.Max(1, candidates[i].Weight);
                    var current = centroids[closest];
                    centroids[closest] = (
                        current.X + (candidateCoords[i].X - current.X) * learningRate * weight / (weight + 1),
                        current.Y + (candidateCoords[i].Y - current.Y) * learningRate * weight / (weight + 1),
                        current.Z + (candidateCoords[i].Z - current.Z) * learningRate * weight / (weight + 1));
                }

                if (learningRate < 0.02) break;
                continue;
            }

            for (var i = 0; i < candidates.Count; i++)
            {
                var closest = ClosestSpaceCentroid(candidateCoords[i], centroids, config);
                var weight = candidates[i].Weight;
                xTotals[closest] += candidateCoords[i].X * weight;
                yTotals[closest] += candidateCoords[i].Y * weight;
                zTotals[closest] += candidateCoords[i].Z * weight;
                weights[closest] += weight;
            }

            double maxMovement = 0;
            for (var i = 0; i < centroids.Count; i++)
            {
                if (weights[i] <= 0) continue;
                var newX = xTotals[i] / weights[i];
                var newY = yTotals[i] / weights[i];
                var newZ = zTotals[i] / weights[i];
                var movement = Math.Sqrt(
                    (newX - centroids[i].X) * (newX - centroids[i].X) +
                    (newY - centroids[i].Y) * (newY - centroids[i].Y) +
                    (newZ - centroids[i].Z) * (newZ - centroids[i].Z));
                if (movement > maxMovement) maxMovement = movement;
                centroids[i] = (newX, newY, newZ);
            }

            if (maxMovement < convergenceThreshold) break;
        }

        var colors = centroids.Select(c => FromColorSpace(c.X, c.Y, c.Z, config)).ToList();
        return MergeToTargetSize(colors, candidates, paletteSize, config);
    }

    private static int ClosestSpaceCentroid((double X, double Y, double Z) coord, List<(double X, double Y, double Z)> centroids, AutoDrawConfig? config)
    {
        var closest = 0;
        var distance = double.MaxValue;
        for (var i = 0; i < centroids.Count; i++)
        {
            var d = WeightedSpaceDistance(coord.X, coord.Y, coord.Z, centroids[i].X, centroids[i].Y, centroids[i].Z, config);
            if (d < distance)
            {
                distance = d;
                closest = i;
            }
        }
        return closest;
    }

    private static List<SKColor> MergeToTargetSize(List<SKColor> colors, List<(SKColor Color, int Weight)> candidates, int targetSize, AutoDrawConfig? config)
    {
        var result = new List<SKColor>(targetSize);
        var seen = new HashSet<int>();

        // 1. Keep distinct existing centroids.
        foreach (var color in colors)
        {
            var key = (color.Red << 16) | (color.Green << 8) | color.Blue;
            if (seen.Add(key)) result.Add(color);
            if (result.Count >= targetSize) return result;
        }

        // 2. If we lost any to rounding, replace with the most-popular candidates that are not
        //    already represented by an existing centroid.  The 4.0 minimum ΔE2000 keeps them
        //    visually meaningful instead of near-duplicates.
        foreach (var candidate in candidates.OrderByDescending(c => c.Weight))
        {
            if (result.Count >= targetSize) break;
            var key = (candidate.Color.Red << 16) | (candidate.Color.Green << 8) | candidate.Color.Blue;
            if (seen.Contains(key)) continue;

            ColorMatcher.ToLab(candidate.Color, out var cL, out var cA, out var cB);
            var tooClose = false;
            foreach (var existing in result)
            {
                ColorMatcher.ToLab(existing, out var eL, out var eA, out var eB);
                if (ColorMatcher.DeltaE2000Lab(cL, cA, cB, eL, eA, eB) < 4.0) { tooClose = true; break; }
            }
            if (tooClose) continue;

            seen.Add(key);
            result.Add(candidate.Color);
        }

        return result;
    }

    private static List<SKColor> MedianCut(List<(SKColor Color, int Weight)> candidates, int paletteSize)
    {
        var boxes = new List<List<(SKColor Color, int Weight)>> { candidates };
        while (boxes.Count < paletteSize)
        {
            var box = boxes.Where(b => b.Count > 1).OrderByDescending(ColourRange).FirstOrDefault();
            if (box is null) break;
            boxes.Remove(box);
            var channel = WidestChannel(box);
            var ordered = box.OrderBy(c => Channel(c.Color, channel)).ToList();
            var half = ordered.Sum(c => c.Weight) / 2;
            var accumulated = 0; var split = 1;
            for (; split < ordered.Count; split++) { accumulated += ordered[split - 1].Weight; if (accumulated >= half) break; }
            if (split <= 0) split = 1;
            if (split >= ordered.Count) split = Math.Max(1, ordered.Count - 1);
            boxes.Add(ordered.Take(split).ToList()); boxes.Add(ordered.Skip(split).ToList());
        }
        return boxes.Select(box =>
        {
            if (box.Count == 0) return new SKColor(0, 0, 0);
            var totalWeight = box.Sum(c => c.Weight);
            if (totalWeight <= 0) totalWeight = 1;
            return new SKColor(
                (byte)Math.Clamp((int)Math.Round(box.Sum(c => c.Color.Red * c.Weight) / (double)totalWeight), 0, 255),
                (byte)Math.Clamp((int)Math.Round(box.Sum(c => c.Color.Green * c.Weight) / (double)totalWeight), 0, 255),
                (byte)Math.Clamp((int)Math.Round(box.Sum(c => c.Color.Blue * c.Weight) / (double)totalWeight), 0, 255));
        }).ToList();
    }

    private static List<SKColor> DivQuantizer(List<(SKColor Color, int Weight)> candidates, int paletteSize)
    {
        var boxes = new List<List<(SKColor Color, int Weight)>> { candidates };
        while (boxes.Count < paletteSize)
        {
            int bestIndex = -1;
            int bestChannel = -1;
            double bestScore = -1;
            for (var bi = 0; bi < boxes.Count; bi++)
            {
                var box = boxes[bi];
                if (box.Count < 2) continue;
                var meanR = box.Sum(c => c.Color.Red * c.Weight) / (double)box.Sum(c => c.Weight);
                var meanG = box.Sum(c => c.Color.Green * c.Weight) / (double)box.Sum(c => c.Weight);
                var meanB = box.Sum(c => c.Color.Blue * c.Weight) / (double)box.Sum(c => c.Weight);
                var variances = new double[3];
                var weights = box.Sum(c => c.Weight);
                foreach (var c in box)
                {
                    var wr = c.Weight / (double)weights;
                    variances[0] += wr * Math.Pow(c.Color.Red - meanR, 2);
                    variances[1] += wr * Math.Pow(c.Color.Green - meanG, 2);
                    variances[2] += wr * Math.Pow(c.Color.Blue - meanB, 2);
                }
                var score = variances.Max();
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = bi;
                    bestChannel = Array.IndexOf(variances, variances.Max());
                }
            }

            if (bestIndex < 0 || bestScore <= 0) break;

            var target = boxes[bestIndex];
            boxes.RemoveAt(bestIndex);
            var ordered = target.OrderBy(c => bestChannel == 0 ? c.Color.Red : bestChannel == 1 ? c.Color.Green : c.Color.Blue).ToList();
            var totalWeight = ordered.Sum(c => c.Weight);
            var half = totalWeight / 2;
            var accumulated = 0;
            var split = 1;
            for (; split < ordered.Count; split++)
            {
                accumulated += ordered[split - 1].Weight;
                if (accumulated >= half) break;
            }
            if (split <= 0) split = 1;
            if (split >= ordered.Count) split = Math.Max(1, ordered.Count - 1);
            boxes.Add(ordered.Take(split).ToList());
            boxes.Add(ordered.Skip(split).ToList());
        }

        return boxes.Select(box =>
        {
            if (box.Count == 0) return new SKColor(0, 0, 0);
            var totalWeight = box.Sum(c => c.Weight);
            if (totalWeight <= 0) totalWeight = 1;
            return new SKColor(
                (byte)Math.Clamp((int)Math.Round(box.Sum(c => c.Color.Red * c.Weight) / (double)totalWeight), 0, 255),
                (byte)Math.Clamp((int)Math.Round(box.Sum(c => c.Color.Green * c.Weight) / (double)totalWeight), 0, 255),
                (byte)Math.Clamp((int)Math.Round(box.Sum(c => c.Color.Blue * c.Weight) / (double)totalWeight), 0, 255));
        }).ToList();
    }

    /// <summary>LxPainter's "Wu's Quantizer": variance-minimising box splits with a weighted
    /// median cut and the box carrying the largest total weighted variance split first.</summary>
    private static List<SKColor> WuQuantizer(List<(SKColor Color, int Weight)> candidates, int paletteSize)
    {
        var boxes = new List<List<(SKColor Color, int Weight)>> { candidates };
        while (boxes.Count < paletteSize)
        {
            var bestIndex = -1;
            var bestScore = -1d;
            var bestChannel = 0;
            for (var i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (box.Count < 2) continue;
                var totalWeight = box.Sum(c => (double)c.Weight);
                if (totalWeight <= 0) continue;
                var mean = new[]
                {
                    box.Sum(c => c.Color.Red * (double)c.Weight) / totalWeight,
                    box.Sum(c => c.Color.Green * (double)c.Weight) / totalWeight,
                    box.Sum(c => c.Color.Blue * (double)c.Weight) / totalWeight
                };
                var variances = new double[3];
                foreach (var c in box)
                {
                    var w = c.Weight / totalWeight;
                    variances[0] += w * (c.Color.Red - mean[0]) * (c.Color.Red - mean[0]);
                    variances[1] += w * (c.Color.Green - mean[1]) * (c.Color.Green - mean[1]);
                    variances[2] += w * (c.Color.Blue - mean[2]) * (c.Color.Blue - mean[2]);
                }
                var score = variances[0] + variances[1] + variances[2];
                if (score <= bestScore) continue;
                bestScore = score;
                bestIndex = i;
                bestChannel = Array.IndexOf(variances, variances.Max());
            }

            if (bestIndex < 0) break;

            var target = boxes[bestIndex];
            boxes.RemoveAt(bestIndex);
            var ordered = target.OrderBy(c => Channel(c.Color, bestChannel)).ToList();
            var half = ordered.Sum(c => (double)c.Weight) / 2d;
            var accumulated = 0d;
            var split = 1;
            for (; split < ordered.Count; split++)
            {
                accumulated += ordered[split - 1].Weight;
                if (accumulated >= half) break;
            }
            if (split <= 0) split = 1;
            if (split >= ordered.Count) split = Math.Max(1, ordered.Count - 1);
            boxes.Add(ordered.Take(split).ToList());
            boxes.Add(ordered.Skip(split).ToList());
        }

        return boxes.Select(box =>
        {
            var totalWeight = box.Sum(c => c.Weight);
            if (totalWeight <= 0) return new SKColor(0, 0, 0);
            return new SKColor(
                (byte)Math.Clamp((int)Math.Round(box.Sum(c => c.Color.Red * c.Weight) / (double)totalWeight), 0, 255),
                (byte)Math.Clamp((int)Math.Round(box.Sum(c => c.Color.Green * c.Weight) / (double)totalWeight), 0, 255),
                (byte)Math.Clamp((int)Math.Round(box.Sum(c => c.Color.Blue * c.Weight) / (double)totalWeight), 0, 255));
        }).ToList();
    }

    /// <summary>
    /// Competitive-learning (NeuQuant-style) quantizer. It samples weighted pixels and moves the
    /// best-matching neuron plus a shrinking one-dimensional neighbourhood in RGB, instead of
    /// running KMeans. This is a faithful behavioural stand-in, not bit-identical to the original
    /// C NeuQuant network.
    /// </summary>
    private static List<SKColor> NeuQuantQuantizer(List<(SKColor Color, int Weight)> candidates, int paletteSize)
    {
        var random = new Random(0);
        var neurons = new (double R, double G, double B)[paletteSize];
        var totalWeight = candidates.Sum(c => (long)Math.Max(1, c.Weight));
        var cumulative = new long[candidates.Count];
        long running = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            running += Math.Max(1, candidates[i].Weight);
            cumulative[i] = running;
        }

        int PickWeighted()
        {
            var target = random.NextInt64(Math.Max(1, running));
            var index = Array.BinarySearch(cumulative, target + 1);
            if (index < 0) index = ~index;
            return Math.Clamp(index, 0, candidates.Count - 1);
        }

        for (var i = 0; i < neurons.Length; i++)
        {
            var c = candidates[PickWeighted()].Color;
            neurons[i] = (c.Red, c.Green, c.Blue);
        }

        const int epochs = 32;
        const int samplesPerEpoch = 2048;
        var learningRate = 0.5;
        for (var epoch = 0; epoch < epochs; epoch++)
        {
            var radius = Math.Max(0, (int)Math.Round((paletteSize / 4d) * (1d - epoch / (double)epochs)));
            for (var sample = 0; sample < samplesPerEpoch; sample++)
            {
                var index = PickWeighted();
                var c = candidates[index].Color;
                var best = 0;
                var bestDistance = double.MaxValue;
                for (var n = 0; n < neurons.Length; n++)
                {
                    var dr = neurons[n].R - c.Red;
                    var dg = neurons[n].G - c.Green;
                    var db = neurons[n].B - c.Blue;
                    var distance = dr * dr + dg * dg + db * db;
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = n;
                }

                var low = Math.Max(0, best - radius);
                var high = Math.Min(neurons.Length - 1, best + radius);
                for (var n = low; n <= high; n++)
                {
                    var falloff = 1d - Math.Abs(n - best) / (double)(radius + 1);
                    var rate = learningRate * falloff;
                    neurons[n] = (
                        neurons[n].R + (c.Red - neurons[n].R) * rate,
                        neurons[n].G + (c.Green - neurons[n].G) * rate,
                        neurons[n].B + (c.Blue - neurons[n].B) * rate);
                }
            }
            learningRate = Math.Max(0.02, learningRate * 0.9);
        }

        var colors = neurons.Select(n => new SKColor(
            (byte)Math.Clamp((int)Math.Round(n.R), 0, 255),
            (byte)Math.Clamp((int)Math.Round(n.G), 0, 255),
            (byte)Math.Clamp((int)Math.Round(n.B), 0, 255))).ToList();
        return MergeToTargetSize(colors, candidates, paletteSize, null);
    }

    private static int Channel(SKColor color, int channel) => channel == 0 ? color.Red : channel == 1 ? color.Green : color.Blue;
    private static int WidestChannel(List<(SKColor Color, int Weight)> box)
    {
        var ranges = Enumerable.Range(0, 3).Select(channel => box.Max(c => Channel(c.Color, channel)) - box.Min(c => Channel(c.Color, channel))).ToArray();
        return Array.IndexOf(ranges, ranges.Max());
    }
    private static int ColourRange(List<(SKColor Color, int Weight)> box) => Enumerable.Range(0, 3).Max(channel => box.Max(c => Channel(c.Color, channel)) - box.Min(c => Channel(c.Color, channel)));

    private static double ColorDistance(SKColor color, (float Red, float Green, float Blue) centroid, AutoDrawConfig? config = null)
    {
        var centroidColor = new SKColor((byte)centroid.Red, (byte)centroid.Green, (byte)centroid.Blue);
        var a = ToColorSpace(color, config);
        var b = ToColorSpace(centroidColor, config);
        return WeightedSpaceDistance(a.X, a.Y, a.Z, b.X, b.Y, b.Z, config);
    }

    /// <summary>
    /// Resolves the colour space used for clustering and nearest-palette assignment.  An explicit
    /// <see cref="AutoDrawConfig.ColorDistanceMethod"/> wins; otherwise the LxPainter quantizer
    /// name selects the space, with KMeans++/Wu/NeuQuant/median/div operating in RGB.
    /// </summary>
    public static string ResolveColorSpace(AutoDrawConfig? config)
    {
        if (config is null) return "RGB";

        var explicitSpace = (config.ColorDistanceMethod ?? string.Empty).Trim().ToUpperInvariant();
        if (explicitSpace.Length > 0)
        {
            return explicitSpace switch
            {
                "RGB" or "SRGB" => "RGB",
                "OKLAB" => "OKLAB",
                _ => "CIELAB"
            };
        }

        var method = (config.QuantizeMethod ?? string.Empty).ToUpperInvariant();
        if (method.Contains("OKLAB")) return "OKLAB";
        if (method.Contains("LAB COLOR") || method.Contains("CIELAB")) return "CIELAB";
        return "RGB";
    }

    private static (double X, double Y, double Z) ToColorSpace(SKColor color, AutoDrawConfig? config)
    {
        var method = ResolveColorSpace(config);
        switch (method)
        {
            case "RGB":
            case "SRGB":
                return (color.Red, color.Green, color.Blue);
            case "OKLAB":
                return RgbToOkLab(color);
            default:
                ColorMatcher.ToLab(color, out var l, out var a, out var b);
                return (l, a, b);
        }
    }

    private static SKColor FromColorSpace(double x, double y, double z, AutoDrawConfig? config)
    {
        var method = ResolveColorSpace(config);
        switch (method)
        {
            case "RGB":
            case "SRGB":
                return new SKColor((byte)Math.Clamp(x, 0, 255), (byte)Math.Clamp(y, 0, 255), (byte)Math.Clamp(z, 0, 255));
            case "OKLAB":
                return OkLabToRgb(x, y, z);
            default:
                return LabToRgb(x, y, z);
        }
    }

    private static double WeightedSpaceDistance(double x1, double y1, double z1, double x2, double y2, double z2, AutoDrawConfig? config)
    {
        if (config is null)
        {
            var dx0 = x1 - x2;
            var dy0 = y1 - y2;
            var dz0 = z1 - z2;
            return dx0 * dx0 + dy0 * dy0 + dz0 * dz0;
        }

        var method = ResolveColorSpace(config);
        if (method == "CIELAB")
        {
            var dl = (x1 - x2) * config.LabLumWeight;
            var da = y1 - y2;
            var db = z1 - z2;
            var chroma = Math.Sqrt(da * da + db * db) * config.LabChromaWeight;
            var chroma1 = Math.Sqrt(y1 * y1 + z1 * z1);
            var chroma2 = Math.Sqrt(y2 * y2 + z2 * z2);
            var meanChroma = Math.Sqrt(chroma1 * chroma2);
            var hueDiff = Math.Atan2(z1, y1) - Math.Atan2(z2, y2);
            while (hueDiff > Math.PI) hueDiff -= 2 * Math.PI;
            while (hueDiff < -Math.PI) hueDiff += 2 * Math.PI;
            var hue = Math.Abs(hueDiff) * meanChroma * config.LabHueWeight;
            return dl * dl + chroma * chroma + hue * hue;
        }

        var wx = (x1 - x2) * config.LabLumWeight;
        var wy = (y1 - y2) * config.LabChromaWeight;
        var wz = (z1 - z2) * config.LabHueWeight;
        return wx * wx + wy * wy + wz * wz;
    }

    private static (double X, double Y, double Z) RgbToOkLab(SKColor color)
    {
        static double Linear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        var r = Linear(color.Red / 255.0);
        var g = Linear(color.Green / 255.0);
        var b = Linear(color.Blue / 255.0);
        var l = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b;
        var m = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b;
        var s = 0.0883024619 * r + 0.2817188376 * g + 0.6299796995 * b;
        var l_ = Math.Cbrt(l);
        var m_ = Math.Cbrt(m);
        var s_ = Math.Cbrt(s);
        var L = 0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_;
        var a = 1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_;
        var bb = 0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_;
        return (L, a, bb);
    }

    private static SKColor OkLabToRgb(double L, double a, double b)
    {
        var l_ = L + 0.3963377774 * a + 0.2158037573 * b;
        var m_ = L - 0.1055613458 * a - 0.0638541728 * b;
        var s_ = L - 0.0894841775 * a - 1.2914855480 * b;
        var l = l_ * l_ * l_;
        var m = m_ * m_ * m_;
        var s = s_ * s_ * s_;
        var r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
        var g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
        var bb = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;

        static double Encode(double v) => v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055;
        return new SKColor(
            (byte)Math.Clamp(Encode(r) * 255.0, 0, 255),
            (byte)Math.Clamp(Encode(g) * 255.0, 0, 255),
            (byte)Math.Clamp(Encode(bb) * 255.0, 0, 255));
    }

    private static int ClosestLabCentroid((double L, double A, double B) lab, List<(double L, double A, double B)> centroids, AutoDrawConfig? config)
    {
        var closest = 0;
        var distance = double.MaxValue;
        for (var i = 0; i < centroids.Count; i++)
        {
            var d = WeightedLabDistanceSquared(lab.L, lab.A, lab.B, centroids[i].L, centroids[i].A, centroids[i].B, config);
            if (d < distance)
            {
                distance = d;
                closest = i;
            }
        }
        return closest;
    }

    private static double WeightedLabDistanceSquared(double l1, double a1, double b1, double l2, double a2, double b2, AutoDrawConfig? config)
    {
        if (config is null)
        {
            var dl = l1 - l2;
            var da = a1 - a2;
            var db = b1 - b2;
            return dl * dl + da * da + db * db;
        }

        var dlw = (l1 - l2) * config.LabLumWeight;
        var da2 = a1 - a2;
        var db2 = b1 - b2;
        var chroma = Math.Sqrt(da2 * da2 + db2 * db2) * config.LabChromaWeight;

        var chroma1 = Math.Sqrt(a1 * a1 + b1 * b1);
        var chroma2 = Math.Sqrt(a2 * a2 + b2 * b2);
        var meanChroma = Math.Sqrt(chroma1 * chroma2);

        var hueDiff = Math.Atan2(b1, a1) - Math.Atan2(b2, a2);
        while (hueDiff > Math.PI) hueDiff -= 2 * Math.PI;
        while (hueDiff < -Math.PI) hueDiff += 2 * Math.PI;
        var hue = Math.Abs(hueDiff) * meanChroma * config.LabHueWeight;

        return dlw * dlw + chroma * chroma + hue * hue;
    }

    private static SKColor LabToRgb(double L, double a, double b)
    {
        static double InvF(double t) => t > 0.20689655172413793 ? t * t * t : (t - 16.0 / 116.0) / 7.787;
        var fy = (L + 16.0) / 116.0;
        var fx = fy + a / 500.0;
        var fz = fy - b / 200.0;
        var x = InvF(fx) * 0.95047;
        var y = InvF(fy) * 1.00000;
        var z = InvF(fz) * 1.08883;
        var r = x * 3.2406 + y * -1.5372 + z * -0.4986;
        var g = x * -0.9689 + y * 1.8758 + z * 0.0415;
        var blue = x * 0.0557 + y * -0.2040 + z * 1.0570;
        static double Encode(double v) => v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055;
        return new SKColor(
            (byte)Math.Clamp(Encode(r) * 255.0, 0, 255),
            (byte)Math.Clamp(Encode(g) * 255.0, 0, 255),
            (byte)Math.Clamp(Encode(blue) * 255.0, 0, 255));
    }

    private static int ClosestCentroid(SKColor color, List<(float Red, float Green, float Blue)> centroids, AutoDrawConfig? config = null)
    {
        var closest = 0;
        var distance = double.MaxValue;
        for (var i = 0; i < centroids.Count; i++)
        {
            var candidateDistance = ColorDistance(color, centroids[i], config);
            if (candidateDistance >= distance) continue;
            distance = candidateDistance;
            closest = i;
        }
        return closest;
    }

    /// <summary>
    /// LxPainter's "Lab Color Space" preprocessing: bilateral smoothing inside CIELAB, blended
    /// with the original LAB values through a Sobel edge mask.  The result is converted back to
    /// RGB so the rest of the pipeline can use it like any other bitmap.
    /// </summary>
    public static SKBitmap ApplyLabSpatialFeatures(SKBitmap source, AutoDrawConfig config)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(config);

        var width = source.Width;
        var height = source.Height;
        var count = width * height;
        var l = new double[count];
        var a = new double[count];
        var b = new double[count];
        var alpha = new byte[count];

        var fourByte = source.BytesPerPixel == 4 && source.GetPixels() != IntPtr.Zero;
        var rowBytes = source.RowBytes;
        var rgba = source.ColorType == SKColorType.Rgba8888;
        var redOffset = rgba ? 0 : 2;
        var blueOffset = rgba ? 2 : 0;
        unsafe
        {
            var pixels = fourByte ? (byte*)source.GetPixels().ToPointer() : null;
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                SKColor color;
                if (pixels is not null)
                {
                    var p = pixels + y * rowBytes + x * 4;
                    color = new SKColor(p[redOffset], p[1], p[blueOffset], p[3]);
                    alpha[index] = p[3];
                }
                else
                {
                    color = source.GetPixel(x, y);
                    alpha[index] = color.Alpha;
                }
                ColorMatcher.ToLab(color, out l[index], out a[index], out b[index]);
            }
        }

        var iterations = Math.Clamp(config.LabSpatialIterations, 1, 8);
        var radius = Math.Clamp((int)Math.Round(config.LabSpatialSigma / 2d), 1, 4);
        var diameter = radius * 2 + 1;
        var spaceSigma = Math.Max(config.LabSpatialSigma, .001);
        var rangeSigma = Math.Max(config.LabRangeSigma, .001);
        var spatialWeights = new double[diameter * diameter];
        for (var dy = -radius; dy <= radius; dy++)
        for (var dx = -radius; dx <= radius; dx++)
            spatialWeights[(dy + radius) * diameter + dx + radius] =
                Math.Exp(-(dx * dx + dy * dy) / (2 * spaceSigma * spaceSigma));

        var smoothL = (double[])l.Clone();
        var smoothA = (double[])a.Clone();
        var smoothB = (double[])b.Clone();
        var scratchL = new double[count];
        var scratchA = new double[count];
        var scratchB = new double[count];
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                if (alpha[index] == 0)
                {
                    scratchL[index] = smoothL[index];
                    scratchA[index] = smoothA[index];
                    scratchB[index] = smoothB[index];
                    continue;
                }

                double totalL = 0, totalA = 0, totalB = 0, total = 0;
                for (var dy = -radius; dy <= radius; dy++)
                {
                    var ny = y + dy;
                    if (ny < 0 || ny >= height) continue;
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        var nx = x + dx;
                        if (nx < 0 || nx >= width) continue;
                        var neighbour = ny * width + nx;
                        if (alpha[neighbour] == 0) continue;

                        var dl = smoothL[neighbour] - smoothL[index];
                        var da = smoothA[neighbour] - smoothA[index];
                        var db = smoothB[neighbour] - smoothB[index];
                        var range = dl * dl + da * da + db * db;
                        var weight = spatialWeights[(dy + radius) * diameter + dx + radius]
                                     * Math.Exp(-range / (2 * rangeSigma * rangeSigma));
                        totalL += smoothL[neighbour] * weight;
                        totalA += smoothA[neighbour] * weight;
                        totalB += smoothB[neighbour] * weight;
                        total += weight;
                    }
                }

                if (total <= 0)
                {
                    scratchL[index] = smoothL[index];
                    scratchA[index] = smoothA[index];
                    scratchB[index] = smoothB[index];
                }
                else
                {
                    scratchL[index] = totalL / total;
                    scratchA[index] = totalA / total;
                    scratchB[index] = totalB / total;
                }
            }

            (smoothL, scratchL) = (scratchL, smoothL);
            (smoothA, scratchA) = (scratchA, smoothA);
            (smoothB, scratchB) = (scratchB, smoothB);
        }

        // Sobel gradient magnitude of L, normalised to 0..100 exactly like the reference.
        var gradient = new double[count];
        var maxGradient = 0d;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var gx = 0d;
            var gy = 0d;
            if (x > 0 && x < width - 1 && y > 0 && y < height - 1)
            {
                var top = (y - 1) * width;
                var middle = y * width;
                var bottom = (y + 1) * width;
                gx = -l[top + x - 1] + l[top + x + 1]
                     - 2 * l[middle + x - 1] + 2 * l[middle + x + 1]
                     - l[bottom + x - 1] + l[bottom + x + 1];
                gy = -l[top + x - 1] - 2 * l[top + x] - l[top + x + 1]
                     + l[bottom + x - 1] + 2 * l[bottom + x] + l[bottom + x + 1];
            }
            var magnitude = Math.Sqrt(gx * gx + gy * gy);
            gradient[y * width + x] = magnitude;
            if (magnitude > maxGradient) maxGradient = magnitude;
        }

        var contrast = Math.Max(config.LabContrastThreshold, 1e-5);
        var power = Math.Max(.1, 100d / Math.Max(config.LabEdgePreservation, 1e-5));
        var output = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        unsafe
        {
            var outputPixels = (byte*)output.GetPixels().ToPointer();
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                var normalized = maxGradient > 0 ? gradient[index] / maxGradient * 100d : 0d;
                var edgeMask = Math.Clamp(normalized / contrast, 0d, 1d);
                edgeMask = Math.Pow(edgeMask, power);

                var outL = l[index] * edgeMask + smoothL[index] * (1 - edgeMask);
                var outA = a[index] * edgeMask + smoothA[index] * (1 - edgeMask);
                var outB = b[index] * edgeMask + smoothB[index] * (1 - edgeMask);
                var color = LabToRgb(outL, outA, outB);
                var p = outputPixels + y * output.RowBytes + x * 4;
                // Output is Bgra8888; write the bytes in memory order.
                p[0] = color.Blue;
                p[1] = color.Green;
                p[2] = color.Red;
                p[3] = alpha[index];
            }
        }

        return output;
    }

    /// <summary>
    /// SLIC-style SEEDS superpixels in OKLab with boundary-only neighbour reassignment and a
    /// gradient edge weight, followed by the mean RGB of every superpixel. This replaces the old
    /// histogram stride that only sampled every Nth pixel.
    /// </summary>
    public static SKBitmap ApplySeedsSuperpixels(SKBitmap source, int desiredCount, int iterations = 10)
    {
        ArgumentNullException.ThrowIfNull(source);
        var width = source.Width;
        var height = source.Height;
        if (width <= 0 || height <= 0) return source.Copy();

        desiredCount = Math.Clamp(desiredCount, 1, 10_000_000);
        iterations = Math.Clamp(iterations, 0, 20);
        var count = width * height;
        var l = new double[count];
        var a = new double[count];
        var b = new double[count];
        var red = new byte[count];
        var green = new byte[count];
        var blue = new byte[count];
        var alpha = new byte[count];

        var fourByte = source.BytesPerPixel == 4 && source.GetPixels() != IntPtr.Zero;
        var rowBytes = source.RowBytes;
        var rgba = source.ColorType == SKColorType.Rgba8888;
        var redOffset = rgba ? 0 : 2;
        var blueOffset = rgba ? 2 : 0;
        unsafe
        {
            var pixels = fourByte ? (byte*)source.GetPixels().ToPointer() : null;
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                SKColor color;
                if (pixels is not null)
                {
                    var p = pixels + y * rowBytes + x * 4;
                    color = new SKColor(p[redOffset], p[1], p[blueOffset], p[3]);
                }
                else
                {
                    color = source.GetPixel(x, y);
                }

                red[index] = color.Red;
                green[index] = color.Green;
                blue[index] = color.Blue;
                alpha[index] = color.Alpha;
                var lab = RgbToOkLab(color);
                l[index] = lab.X;
                a[index] = lab.Y;
                b[index] = lab.Z;
            }
        }

        var gradient = new double[count];
        var maxGradient = 0d;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var dx = x + 1 < width ? l[y * width + x + 1] - l[y * width + x] : 0;
            var dy = y + 1 < height ? l[(y + 1) * width + x] - l[y * width + x] : 0;
            var magnitude = dx * dx + dy * dy;
            gradient[y * width + x] = magnitude;
            if (magnitude > maxGradient) maxGradient = magnitude;
        }
        if (maxGradient > 0)
            for (var i = 0; i < count; i++) gradient[i] /= maxGradient;

        var blockSize = Math.Max(1, (int)Math.Sqrt(count / (double)desiredCount));
        var blocksX = (int)Math.Ceiling(width / (double)blockSize);
        var blocksY = (int)Math.Ceiling(height / (double)blockSize);
        var labels = new int[count];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            labels[y * width + x] = (y / blockSize) * blocksX + x / blockSize;

        var regionCount = blocksX * blocksY;
        var regionCounts = new int[regionCount];
        var sumL = new double[regionCount];
        var sumA = new double[regionCount];
        var sumB = new double[regionCount];
        var meanL = new double[regionCount];
        var meanA = new double[regionCount];
        var meanB = new double[regionCount];
        const double edgeWeight = 10d;
        var neighbours = new int[4];

        for (var iteration = 0; iteration < iterations; iteration++)
        {
            Array.Clear(regionCounts);
            Array.Clear(sumL);
            Array.Clear(sumA);
            Array.Clear(sumB);
            for (var i = 0; i < count; i++)
            {
                var region = labels[i];
                regionCounts[region]++;
                sumL[region] += l[i];
                sumA[region] += a[i];
                sumB[region] += b[i];
            }
            for (var region = 0; region < regionCount; region++)
            {
                if (regionCounts[region] == 0) continue;
                meanL[region] = sumL[region] / regionCounts[region];
                meanA[region] = sumA[region] / regionCounts[region];
                meanB[region] = sumB[region] / regionCounts[region];
            }

            var anyMove = false;
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                if (x == 0 || y == 0 || x == width - 1 || y == height - 1) continue;
                var index = y * width + x;
                var region = labels[index];
                if (region == labels[index - 1] && region == labels[index + 1] &&
                    region == labels[index - width] && region == labels[index + width])
                    continue;

                var dl = l[index] - meanL[region];
                var da = a[index] - meanA[region];
                var db = b[index] - meanB[region];
                var currentDistance = dl * dl + da * da + db * db;
                var edgeFactor = 1d + edgeWeight * gradient[index];
                var best = region;
                var bestDistance = currentDistance;

                neighbours[0] = labels[index - 1];
                neighbours[1] = labels[index + 1];
                neighbours[2] = labels[index - width];
                neighbours[3] = labels[index + width];
                for (var n = 0; n < 4; n++)
                {
                    var candidate = neighbours[n];
                    if (candidate == region) continue;
                    dl = l[index] - meanL[candidate];
                    da = a[index] - meanA[candidate];
                    db = b[index] - meanB[candidate];
                    var distance = dl * dl + da * da + db * db;
                    if (!(distance * edgeFactor + 1e-5 < bestDistance)) continue;
                    bestDistance = distance * edgeFactor;
                    best = candidate;
                }

                if (best == region) continue;

                var oldCount = regionCounts[region];
                if (oldCount > 1)
                {
                    meanL[region] = (meanL[region] * oldCount - l[index]) / (oldCount - 1);
                    meanA[region] = (meanA[region] * oldCount - a[index]) / (oldCount - 1);
                    meanB[region] = (meanB[region] * oldCount - b[index]) / (oldCount - 1);
                }
                regionCounts[region] = Math.Max(0, oldCount - 1);

                var newCount = regionCounts[best];
                meanL[best] = (meanL[best] * newCount + l[index]) / (newCount + 1);
                meanA[best] = (meanA[best] * newCount + a[index]) / (newCount + 1);
                meanB[best] = (meanB[best] * newCount + b[index]) / (newCount + 1);
                regionCounts[best] = newCount + 1;
                labels[index] = best;
                anyMove = true;
            }

            if (!anyMove) break;
        }

        // Mean RGB per final superpixel.
        Array.Clear(regionCounts);
        var sumR = new double[regionCount];
        var sumG = new double[regionCount];
        var sumBl = new double[regionCount];
        for (var i = 0; i < count; i++)
        {
            var region = labels[i];
            if (region < 0 || region >= regionCount) continue;
            regionCounts[region]++;
            sumR[region] += red[i];
            sumG[region] += green[i];
            sumBl[region] += blue[i];
        }
        for (var region = 0; region < regionCount; region++)
        {
            if (regionCounts[region] == 0) continue;
            sumR[region] /= regionCounts[region];
            sumG[region] /= regionCounts[region];
            sumBl[region] /= regionCounts[region];
        }

        var output = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        unsafe
        {
            var outputPixels = (byte*)output.GetPixels().ToPointer();
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                var region = labels[index];
                var r = region >= 0 && region < regionCount && regionCounts[region] > 0 ? (byte)Math.Clamp((int)Math.Round(sumR[region]), 0, 255) : red[index];
                var g = region >= 0 && region < regionCount && regionCounts[region] > 0 ? (byte)Math.Clamp((int)Math.Round(sumG[region]), 0, 255) : green[index];
                var bl = region >= 0 && region < regionCount && regionCounts[region] > 0 ? (byte)Math.Clamp((int)Math.Round(sumBl[region]), 0, 255) : blue[index];
                var p = outputPixels + y * output.RowBytes + x * 4;
                // Output is Bgra8888; write the bytes in memory order.
                p[0] = bl;
                p[1] = g;
                p[2] = r;
                p[3] = alpha[index];
            }
        }

        return output;
    }

    public static unsafe List<ColorLayerResult> SplitImageByColors(SKBitmap sourceBitmap, List<SKColor> targetColors, byte alphaThreshold, AutoDrawConfig? config = null)
    {
        var width = sourceBitmap.Width;
        var height = sourceBitmap.Height;

        var results = new List<ColorLayerResult>();
        var layerPtrs = new uint*[targetColors.Count];

        for (int i = 0; i < targetColors.Count; i++)
        {
            var color = targetColors[i];
            // RGBA so the fast MakePixel writer below and SKBitmap.GetPixel agree on channel order.
            var layerBmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            // Clearing once is much cheaper than writing a transparent pixel into every layer
            // for every source pixel (O(layers * pixels) writes).
            layerBmp.Erase(SKColors.Transparent);
            results.Add(new ColorLayerResult { Bitmap = layerBmp, Color = color, PixelCount = 0 });
            layerPtrs[i] = (uint*)layerBmp.GetPixels().ToPointer();
        }

        var targetCoords = targetColors.Select(tc => ToColorSpace(tc, config)).ToArray();

        var basePtr = (uint*)sourceBitmap.GetPixels().ToPointer();
        var rgbaSource = sourceBitmap.ColorType == SKColorType.Rgba8888;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var srcPtr = basePtr + width * y + x;
                GetPixel(srcPtr, rgbaSource, out var r, out var g, out var b, out var a);

                if (a < alphaThreshold) continue;

                var pixelCoord = ToColorSpace(new SKColor(r, g, b), config);

                int closestIndex = 0;
                double minDistance = double.MaxValue;

                for (int i = 0; i < targetColors.Count; i++)
                {
                    var dist = WeightedSpaceDistance(pixelCoord.X, pixelCoord.Y, pixelCoord.Z, targetCoords[i].X, targetCoords[i].Y, targetCoords[i].Z, config);
                    if (dist < minDistance)
                    {
                        minDistance = dist;
                        closestIndex = i;
                    }
                }

                var tc = targetColors[closestIndex];
                *(layerPtrs[closestIndex] + width * y + x) = MakePixel(tc.Red, tc.Green, tc.Blue, 255);
                results[closestIndex].PixelCount++;
            }
        }

        if (config?.RemoveStray == true) MergeStrayPixels(results, width, height);
        // SEEDS is already a superpixel pass; smoothing here would be a second, unrelated filter.
        if (config?.UseSeeds != true &&
            config?.QuantizeStyle?.Contains("vector", StringComparison.OrdinalIgnoreCase) == true)
            SmoothLayerAssignments(results, width, height, null);
        return results;
    }

    /// <summary>Combines the transparent colour masks into a single preview bitmap.</summary>
    public static SKBitmap BuildComposite(IReadOnlyList<ColorLayerResult> layers, int width, int height)
    {
        var composite = new SKBitmap(width, height);
        using var canvas = new SKCanvas(composite);
        canvas.Clear(SKColors.Transparent);
        foreach (var layer in layers)
        {
            if (layer.PixelCount <= 0) continue;
            canvas.DrawBitmap(layer.Bitmap, 0, 0);
        }
        return composite;
    }

    /// <summary>Produces a grayscale copy used by HexedDrawer's Black &amp; White mode.</summary>
    public static SKBitmap ToGrayscale(SKBitmap source)
    {
        var result = source.Copy();
        for (var y = 0; y < result.Height; y++)
        for (var x = 0; x < result.Width; x++)
        {
            var color = result.GetPixel(x, y);
            var luma = (byte)Math.Clamp((int)Math.Round(color.Red * .2126 + color.Green * .7152 + color.Blue * .0722), 0, 255);
            result.SetPixel(x, y, new SKColor(luma, luma, luma, color.Alpha));
        }
        return result;
    }

    /// <summary>Moves isolated single pixels into the dominant neighbouring colour layer instead of
    /// deleting them. This is HexedDrawer's Smart Merge (V5) behaviour.</summary>
    private static unsafe void MergeStrayPixels(IReadOnlyList<ColorLayerResult> layers, int width, int height)
    {
        var labels = BuildLabelMap(layers, width, height);
        var moves = new List<(int X, int Y, int From, int To)>();
        var neighbours = new int[layers.Count];
        var pointers = new uint*[layers.Count];
        for (var i = 0; i < layers.Count; i++) pointers[i] = (uint*)layers[i].Bitmap.GetPixels().ToPointer();
        for (var y = 1; y < height - 1; y++)
        for (var x = 1; x < width - 1; x++)
        {
            var from = labels[y * width + x];
            if (from < 0) continue;

            // Reference definition: a stray has no identical pixel among its four orthogonal
            // neighbours. Edge padding makes border pixels never stray.
            var up = labels[(y - 1) * width + x];
            var down = labels[(y + 1) * width + x];
            var left = labels[y * width + x - 1];
            var right = labels[y * width + x + 1];
            if (up == from || down == from || left == from || right == from) continue;

            Array.Clear(neighbours);
            if (up >= 0) neighbours[up]++;
            if (down >= 0) neighbours[down]++;
            if (left >= 0) neighbours[left]++;
            if (right >= 0) neighbours[right]++;

            var best = -1;
            var bestCount = 0;
            for (var i = 0; i < neighbours.Length; i++)
            {
                if (neighbours[i] <= bestCount) continue;
                bestCount = neighbours[i];
                best = i;
            }
            if (best >= 0) moves.Add((x, y, from, best));
        }

        foreach (var (x, y, from, to) in moves)
        {
            pointers[from][y * width + x] = 0;
            var color = layers[to].Color;
            pointers[to][y * width + x] = MakePixel(color.Red, color.Green, color.Blue, 255);
            layers[from].PixelCount = Math.Max(0, layers[from].PixelCount - 1);
            layers[to].PixelCount++;
        }
    }

    /// <summary>Majority-vote cleanup for Vectorized mode, or spatial/range weighted cleanup for SEEDS.</summary>
    private static unsafe void SmoothLayerAssignments(IReadOnlyList<ColorLayerResult> layers, int width, int height, AutoDrawConfig? config)
    {
        var pointers = new uint*[layers.Count];
        for (var i = 0; i < layers.Count; i++) pointers[i] = (uint*)layers[i].Bitmap.GetPixels().ToPointer();
        var spatial = Math.Max(config?.LabSpatialSigma ?? 1d, .001);
        var range = Math.Max(config?.LabRangeSigma ?? 10d, .001);
        var edge = Math.Max(config?.LabEdgePreservation ?? 0d, 0d);
        var radius = config is null ? 1 : Math.Clamp((int)Math.Round(config.LabSpatialSigma), 1, 6);
        var passes = config is null ? 2 : Math.Clamp(config.LabSpatialIterations, 0, 8);
        for (var pass = 0; pass < passes; pass++)
        {
            var labels = BuildLabelMap(layers, width, height);
            var moves = new List<(int X, int Y, int From, int To)>();
            var counts = new double[layers.Count];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var from = labels[y * width + x];
                if (from < 0) continue;
                Array.Clear(counts);
                var total = 0d;
                for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (config is null && dx == 0 && dy == 0) continue;
                    var nx = x + dx;
                    var ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    var label = labels[ny * width + nx];
                    if (label < 0) continue;
                    var weight = 1d;
                    if (config is not null)
                    {
                        var first = layers[from].Color;
                        var second = layers[label].Color;
                        var dr = first.Red - second.Red;
                        var dg = first.Green - second.Green;
                        var db = first.Blue - second.Blue;
                        var difference = dr * dr + dg * dg + db * db;
                        weight = Math.Exp(-(dx * dx + dy * dy) / (2 * spatial * spatial))
                                 * Math.Exp(-(1 + edge) * difference / (2 * range * range));
                    }
                    counts[label] += weight;
                    total += weight;
                }
                var best = from;
                for (var i = 0; i < counts.Length; i++)
                    if (counts[i] > counts[best]) best = i;
                if (config is null)
                {
                    if (total >= 4 && best != from && counts[best] >= 4) moves.Add((x, y, from, best));
                }
                else if (best != from && counts[best] > counts[from] * 1.05)
                    moves.Add((x, y, from, best));
            }

            foreach (var (x, y, from, to) in moves)
            {
                pointers[from][y * width + x] = 0;
                var color = layers[to].Color;
                pointers[to][y * width + x] = MakePixel(color.Red, color.Green, color.Blue, 255);
                layers[from].PixelCount = Math.Max(0, layers[from].PixelCount - 1);
                layers[to].PixelCount++;
            }
        }
    }

    private static unsafe int[] BuildLabelMap(IReadOnlyList<ColorLayerResult> layers, int width, int height)
    {
        var labels = Enumerable.Repeat(-1, width * height).ToArray();
        for (var i = 0; i < layers.Count; i++)
        {
            var bitmap = layers[i].Bitmap;
            var pixels = (byte*)bitmap.GetPixels().ToPointer();
            if (pixels == null || bitmap.BytesPerPixel != 4)
            {
                // Fallback for exotic bitmap formats; layer bitmaps are normally 4-byte BGRA.
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    if (bitmap.GetPixel(x, y).Alpha != 0) labels[y * width + x] = i;
                }
                continue;
            }

            var rowBytes = bitmap.RowBytes;
            for (var y = 0; y < height; y++)
            {
                var row = pixels + y * rowBytes;
                for (var x = 0; x < width; x++)
                {
                    if (row[x * 4 + 3] != 0) labels[y * width + x] = i;
                }
            }
        }
        return labels;
    }

    /// <summary>Applies LxPainter custom dimensions. Zero means automatic; specifying only one
    /// dimension preserves aspect ratio. The caller owns the returned independent bitmap.</summary>
    public static SKBitmap ResizeToCustomDimensions(SKBitmap source, AutoDrawConfig config)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(config);
        var width = Math.Max(0, config.CustomWidth);
        var height = Math.Max(0, config.CustomHeight);
        if (width == 0 && height == 0) return source.Copy();
        if (width == 0) width = Math.Max(1, (int)Math.Round(source.Width * (height / (double)source.Height)));
        if (height == 0) height = Math.Max(1, (int)Math.Round(source.Height * (width / (double)source.Width)));
        if (width == source.Width && height == source.Height) return source.Copy();
        return source.Resize(new SKSizeI(width, height), SKFilterQuality.High);
    }

    /// <summary>Applies LxPainter's Pixel Size Scaling processing resolution: one processed pixel
    /// covers <paramref name="pixelSize"/> screen pixels. The caller owns the result.</summary>
    public static SKBitmap ResizeForDrawMethod(
        SKBitmap source,
        int canvasWidth,
        int canvasHeight,
        bool pixelScaling,
        int pixelSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!pixelScaling) return source.Copy();

        var size = Math.Max(1, pixelSize);
        var width = Math.Max(1, canvasWidth / size);
        var height = Math.Max(1, canvasHeight / size);
        if (width == source.Width && height == source.Height) return source.Copy();
        return source.Resize(new SKSizeI(width, height), SKFilterQuality.High);
    }

    /// <summary>Copies a bitmap and makes configured background colours transparent before colour quantization.</summary>
    public static SKBitmap PrepareColorImage(SKBitmap source, AutoDrawConfig config)
    {
        var output = source.Copy();
        var background = config.BackgroundColorsToRemove
            .Select(value => SKColor.TryParse(value, out var color) ? color : SKColors.Transparent)
            .Where(color => color != SKColors.Transparent)
            .ToArray();
        if ((!config.RemoveBackground || background.Length == 0) && !config.UseBilateral) return output;
        if (config.RemoveBackground && background.Length > 0)
        {
            for (var y = 0; y < output.Height; y++)
            for (var x = 0; x < output.Width; x++)
            {
                var c = output.GetPixel(x, y);
                if (background.Any(bg => IsBackgroundMatch(c, bg, config)))
                    output.SetPixel(x, y, new SKColor(0, 0, 0, 0));
            }
        }
        if (!config.UseBilateral) return output;
        var filtered = ApplyBilateral(output, config);
        output.Dispose();
        return filtered;
    }

    private static bool IsBackgroundMatch(SKColor color, SKColor background, AutoDrawConfig config)
    {
        if (config.BackgroundTolerance is { } tolerance)
        {
            var red = color.Red - background.Red;
            var green = color.Green - background.Green;
            var blue = color.Blue - background.Blue;
            return Math.Sqrt(red * red + green * green + blue * blue) <= tolerance;
        }
        return ColorMatcher.DeltaE2000(background, color) <= config.LabContrastThreshold;
    }

    // Deliberately bounded radius: a configuration import must not turn a large image into an unbounded operation.
    private static unsafe SKBitmap ApplyBilateral(SKBitmap source, AutoDrawConfig config)
    {
        var result = source.Copy();
        var radius = Math.Clamp(config.BilateralDiameter / 2, 1, 8);
        var space = Math.Max(config.BilateralSpaceSigma, .001);
        var range = Math.Max(config.BilateralColorSigma, .001);
        var diameter = radius * 2 + 1;
        var spatialWeights = new double[diameter * diameter];
        for (var dy = -radius; dy <= radius; dy++)
        for (var dx = -radius; dx <= radius; dx++)
            spatialWeights[(dy + radius) * diameter + dx + radius] = Math.Exp(-(dx * dx + dy * dy) / (2 * space * space));

        // Range weights use a bounded lookup table: the exact value is an exponential of squared
        // channel distance, and evaluating it for every neighbour of every pixel is too slow for
        // live auto-quantization on typical images.
        const int rangeBins = 1024;
        const double maxDifference = 3d * 255d * 255d;
        var rangeWeights = new double[rangeBins];
        for (var i = 0; i < rangeBins; i++)
            rangeWeights[i] = Math.Exp(-(maxDifference * i / (rangeBins - 1)) / (2 * range * range));

        if (source.BytesPerPixel == 4 && result.BytesPerPixel == 4)
        {
            var src = (byte*)source.GetPixels().ToPointer();
            var dst = (byte*)result.GetPixels().ToPointer();
            var rowBytes = source.RowBytes;
            var rowBytesOut = result.RowBytes;
            var rgba = source.ColorType == SKColorType.Rgba8888;
            var redOffset = rgba ? 0 : 2;
            var blueOffset = rgba ? 2 : 0;

            for (var y = 0; y < source.Height; y++)
            {
                var dstRow = dst + y * rowBytesOut;
                for (var x = 0; x < source.Width; x++)
                {
                    var center = src + y * rowBytes + x * 4;
                    var centerAlpha = center[3];
                    if (centerAlpha == 0)
                    {
                        *(uint*)(dstRow + x * 4) = 0;
                        continue;
                    }

                    double red = 0, green = 0, blue = 0, alpha = 0, total = 0;
                    for (var dy = -radius; dy <= radius; dy++)
                    {
                        var ny = y + dy;
                        if (ny < 0 || ny >= source.Height) continue;
                        var sampleRow = src + ny * rowBytes;
                        for (var dx = -radius; dx <= radius; dx++)
                        {
                            var nx = x + dx;
                            if (nx < 0 || nx >= source.Width) continue;
                            var sample = sampleRow + nx * 4;
                            if (sample[3] == 0) continue;

                            var dr = sample[redOffset] - center[redOffset];
                            var dg = sample[1] - center[1];
                            var db = sample[blueOffset] - center[blueOffset];
                            var difference = dr * dr + dg * dg + db * db;
                            var rangeIndex = (int)(difference * (rangeBins - 1) / maxDifference);
                            var weight = spatialWeights[(dy + radius) * diameter + dx + radius]
                                         * rangeWeights[Math.Clamp(rangeIndex, 0, rangeBins - 1)];
                            red += sample[redOffset] * weight;
                            green += sample[1] * weight;
                            blue += sample[blueOffset] * weight;
                            alpha += sample[3] * weight;
                            total += weight;
                        }
                    }

                    var target = dstRow + x * 4;
                    if (total <= 0)
                    {
                        target[0] = target[1] = target[2] = target[3] = 0;
                    }
                    else
                    {
                        target[redOffset] = (byte)Math.Clamp(red / total, 0, 255);
                        target[1] = (byte)Math.Clamp(green / total, 0, 255);
                        target[blueOffset] = (byte)Math.Clamp(blue / total, 0, 255);
                        target[3] = (byte)Math.Clamp(alpha / total, 0, 255);
                    }
                }
            }
            return result;
        }

        for (var y = 0; y < source.Height; y++) for (var x = 0; x < source.Width; x++)
        {
            var center = source.GetPixel(x, y); double red = 0, green = 0, blue = 0, alpha = 0, total = 0;
            for (var dy = -radius; dy <= radius; dy++) for (var dx = -radius; dx <= radius; dx++)
            {
                var nx = x + dx; var ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= source.Width || ny >= source.Height) continue;
                var sample = source.GetPixel(nx, ny);
                var spatial = spatialWeights[(dy + radius) * diameter + dx + radius];
                var difference = Math.Pow(sample.Red - center.Red, 2) + Math.Pow(sample.Green - center.Green, 2) + Math.Pow(sample.Blue - center.Blue, 2);
                var rangeIndex = (int)(difference * (rangeBins - 1) / maxDifference);
                var weight = spatial * rangeWeights[Math.Clamp(rangeIndex, 0, rangeBins - 1)];
                red += sample.Red * weight; green += sample.Green * weight; blue += sample.Blue * weight; alpha += sample.Alpha * weight; total += weight;
            }
            result.SetPixel(x, y, new SKColor((byte)(red / total), (byte)(green / total), (byte)(blue / total), (byte)(alpha / total)));
        }
        return result;
    }

    private static void RemoveSinglePixelComponents(List<ColorLayerResult> layers, int width, int height)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var layer in layers)
            {
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    if (layer.Bitmap.GetPixel(x, y).Alpha == 0) continue;
                    var neighbours = 0;
                    for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
                        if ((dx != 0 || dy != 0) && x + dx >= 0 && y + dy >= 0 && x + dx < width && y + dy < height && layer.Bitmap.GetPixel(x + dx, y + dy).Alpha != 0) neighbours++;
                    if (neighbours == 0)
                    {
                        layer.Bitmap.SetPixel(x, y, new SKColor(0, 0, 0, 0));
                        layer.PixelCount = Math.Max(0, layer.PixelCount - 1);
                        changed = true;
                    }
                }
            }
        }
    }

    private readonly record struct Lab(double L, double A, double B);
    private static Lab ToLab(SKColor c)
    {
        static double Linear(double v) => v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4);
        var r = Linear(c.Red / 255d); var g = Linear(c.Green / 255d); var b = Linear(c.Blue / 255d);
        var x = (r * .4124 + g * .3576 + b * .1805) / .95047; var y = r * .2126 + g * .7152 + b * .0722; var z = (r * .0193 + g * .1192 + b * .9505) / 1.08883;
        static double F(double v) => v > .008856 ? Math.Pow(v, 1d / 3d) : 7.787 * v + 16d / 116d;
        var fx = F(x); var fy = F(y); var fz = F(z); return new Lab(116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }
}

public class ColorLayerResult
{
    public SKBitmap Bitmap { get; set; } = null!;
    public SKColor Color { get; set; }
    public int PixelCount { get; set; }
}

