using System.Collections.Generic;
using SkiaSharp;

namespace Autodraw.Painting;

public enum QuantizeMethod { KMeansPlusPlus, LabColorSpace }
public enum KMeansAlgo { KMeansPlusPlus, MiniBatchKMeans }

public class LabParams
{
    public float LumWeight = 1;
    public float ChromaWeight = 1;
    public float HueWeight = 1;
    public float ContrastThresh = 10;
    public float SpatialSigma = 10;
    public int SpatialIter = 1;
    public float EdgePreservation = 50;
    public float RangeSigma = 20;
}

/// <summary>
/// Thin adapter that routes the Painting namespace through the single palette pipeline
/// implemented in <see cref="ImageProcessing"/>.  This keeps the public Painting API usable
/// without duplicating quantization or color-distance logic.
/// </summary>
public static class PaletteExtractor
{
    public static IReadOnlyList<SKColor> Extract(
        SKBitmap source,
        int paletteSize,
        byte alphaThreshold,
        QuantizeMethod method,
        KMeansAlgo algo,
        LabParams lab)
    {
        var config = new Configuration.AutoDrawConfig
        {
            KClusters = paletteSize,
            LabLumWeight = lab.LumWeight,
            LabChromaWeight = lab.ChromaWeight,
            LabHueWeight = lab.HueWeight,
            LabContrastThreshold = lab.ContrastThresh,
            LabSpatialSigma = lab.SpatialSigma,
            LabSpatialIterations = lab.SpatialIter,
            LabEdgePreservation = lab.EdgePreservation,
            LabRangeSigma = lab.RangeSigma,
            // The Painting enum selects the algorithm explicitly; no legacy cell-based seeds.
            UseSeeds = false,
            KmeansAlgorithm = algo == KMeansAlgo.KMeansPlusPlus ? "KMeans++ (Original)" : "KMeans (Random)",
            QuantizeMethod = method == QuantizeMethod.LabColorSpace ? "median" : "kmeans",
        };

        return ImageProcessing.ExtractPalette(source, paletteSize, alphaThreshold, config);
    }
}
