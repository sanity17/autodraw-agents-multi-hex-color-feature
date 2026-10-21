using Autodraw.Painting.Persistence;

namespace Autodraw.Painting;

public class PaintOptions
{
    public StrokeMode Mode = StrokeMode.Fill;
    public int PaletteSize = 8;
    public byte AlphaThreshold = 200;
    public ColorSpace ColorSpace = ColorSpace.Lab;
    public QuantizeMethod QuantizeMethod = QuantizeMethod.KMeansPlusPlus;
    public KMeansAlgo KMeansAlgo = KMeansAlgo.KMeansPlusPlus;
    public LabParams Lab = new();
    public Brush Brush = new() { Size = 1, Spacing = 1.5f, Shape = BrushShape.Round };
    public float RdpTolerance = 1.5f;
    public bool InterleaveSameColor = true;
    public Intervals Intervals = new();
    public bool UseBilateral;
    public int BilateralDiameter = 9;
    public float BilateralSigmaColor = 75f;
    public float BilateralSigmaSpace = 75f;
    public bool UseSeeds;
    public int SeedsCount = 400;
    public bool RemoveStray;
    public string? PickerProfileName;
    public int CheckpointEveryN = 100;
}
