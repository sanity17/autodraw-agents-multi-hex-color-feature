using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autodraw.Painting.ColorInput;

namespace Autodraw.Painting;

public static class StrokeExecutor
{
    public static Task Execute(
        List<Stroke> strokes,
        IColorInputMethod colorInput,
        PaintOptions opts,
        IProgress<PaintProgress> progress,
        CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}
