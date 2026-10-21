using System.Numerics;
using SkiaSharp;

namespace Autodraw.Painting;

public record Stroke(Vector2[] Points, SKColor Color, Brush Brush, StrokeMode Mode);
