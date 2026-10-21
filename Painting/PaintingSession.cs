using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace Autodraw.Painting;

/// <summary>
/// Wraps the legacy Drawing.Draw / Drawing.DrawStack path behind the new subsystem entry point.
/// Currently delegates directly to the existing implementation so behavior is unchanged.
/// </summary>
public class PaintingSession
{
    public async Task Run(
        SKBitmap source,
        Vector2 anchor,
        PaintOptions opts,
        CancellationToken ct)
    {
        // Preserve existing behavior: delegate straight to the legacy drawing path.
        await Drawing.Draw(source, anchor);
    }
}
