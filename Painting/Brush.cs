namespace Autodraw.Painting;

public enum BrushShape { Round, Square }

public class Brush
{
    public float Size = 1;
    public float Spacing = 1.5f;
    public BrushShape Shape = BrushShape.Round;
    public float Jitter;
}
