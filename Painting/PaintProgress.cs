using System;

namespace Autodraw.Painting;

public class PaintProgress
{
    public int Completed;
    public int Total;
    public TimeSpan Elapsed;
    public TimeSpan Eta;
    public string Phase = string.Empty;
}
