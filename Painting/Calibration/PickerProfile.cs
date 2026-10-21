using System.Collections.Generic;
using System.Numerics;
using SkiaSharp;

namespace Autodraw.Painting.Calibration;

public class PaletteEntry
{
    public Vector2 Coord;
    public SKColor Rgb;
}

public class PickerProfile
{
    public string Name = string.Empty;
    public string PickerType = string.Empty;
    public Dictionary<string, Vector2> Points = new();
    public Dictionary<string, float> Floats = new();
    public List<PaletteEntry> CapturedPalette = new();
}
