using System.Collections.Generic;
using Autodraw.Painting;

namespace Autodraw.Painting.Persistence;

public class Intervals
{
    public int MoveMs = 100;
    public int ClickUpMs = 20;
    public int ClickDownMs = 20;
    public int AfterColorChangeMs = 150;
}

public class LayerSpec
{
    public string Hex = string.Empty;
    public bool Enabled = true;
}

public class Recipe
{
    public int Version = 1;
    public string Name = string.Empty;
    public string Profile = string.Empty;
    public string Mode = string.Empty;
    public Brush Brush = new();
    public int PaletteSize = 8;
    public string ColorSpace = string.Empty;
    public float RdpTolerance = 1.5f;
    public Intervals Intervals = new();
    public List<LayerSpec> Layers = new();
}
