using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autodraw.Configuration;
using SkiaSharp;

namespace Autodraw.Painting.Recreate;

public enum StrokeOrder
{
    /// <summary>Rows are scanned top-to-bottom and alternate left-to-right / right-to-left.</summary>
    Serpentine = 0,
    /// <summary>Each next run starts at the endpoint nearest the previous run's end.</summary>
    NearestNeighbor = 1
}

public sealed class StrokePlanOptions
{
    public int AlphaThreshold { get; set; } = 200;
    public int BrushSize { get; set; } = 1;
    public StrokeOrder Order { get; set; } = StrokeOrder.Serpentine;

    /// <summary>LxPainter stroke traversal.  <see cref="StrokeAlgorithm.Serpentine"/> keeps the
    /// legacy row-scan order; DFS and Concentric reorder each connected component's runs.</summary>
    public StrokeAlgorithm Algorithm { get; set; } = StrokeAlgorithm.Serpentine;

    /// <summary>Screen-pixel size of one processed pixel for "Pixel Size Scaling".</summary>
    public int PixelSize { get; set; } = 1;

    /// <summary>LxPainter drawing method label, retained for logging and diagnostics.</summary>
    public string DrawMethod { get; set; } = "1:1 Exact (AutoDraw)";

    /// <summary>False uses LxPainter's 8-directional DFS neighbour search.</summary>
    public bool FourDirectional { get; set; }

    public ScreenPoint? Start { get; set; }
}

public enum StrokeAlgorithm
{
    /// <summary>Top-to-bottom rows, alternating direction (the previous engine's behaviour).</summary>
    Serpentine = 0,
    /// <summary>LxPainter "DFS (Default)": depth-first traversal of each connected component.</summary>
    Dfs = 1,
    /// <summary>LxPainter "Concentric": follow increasing distance from the component edge.</summary>
    Concentric = 2
}


/// <summary>A single horizontal mouse-down/mouse-up run in screen coordinates.</summary>
public readonly record struct PlannedStroke(SKPoint Start, SKPoint End)
{
    public double Length => Math.Sqrt(
        (End.X - Start.X) * (End.X - Start.X) +
        (End.Y - Start.Y) * (End.Y - Start.Y));

    public PlannedStroke Reverse() => new(End, Start);
}

public sealed record LayerStrokePlan(SKColor Color, IReadOnlyList<PlannedStroke> Strokes);

/// <summary>
/// Converts an opaque colour mask into horizontal screen-space runs.  It deliberately keeps one
/// down/up pair per run: a paint program can lose hundreds of milliseconds for every extra click,
/// while a single dragged run paints the same row.  Runs are sampled every <c>BrushSize</c> rows so
/// a larger brush does not redundantly repaint the same band.
/// </summary>
public static class StrokePlanner
{
    private readonly record struct Run(int Row, int StartX, int EndX, PlannedStroke Stroke);

    public static unsafe List<PlannedStroke> Plan(
        SKBitmap mask,
        CanvasMapper mapper,
        StrokePlanOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(options);

        var result = new List<PlannedStroke>();
        if (mask.Width <= 0 || mask.Height <= 0 || mapper.Canvas.Width <= 0 || mapper.Canvas.Height <= 0)
            return result;

        var alphaThreshold = Math.Clamp(options.AlphaThreshold, 0, 255);
        // BrushSize is a target-application pixel size, while rows are sampled in image space.
        // Account for a downscaled image so a large brush does not repaint the same screen band.
        var rowStep = Math.Max(1, (int)Math.Floor(options.BrushSize / Math.Max(mapper.Scale, 1e-6)));
        var seen = new HashSet<(int, int, int, int)>();
        var width = mask.Width;
        var height = mask.Height;
        var runAt = new int[width * height];
        Array.Fill(runAt, -1);
        var opaque = new bool[width * height];

        byte* pixelBase = null;
        var fastPixels = mask.BytesPerPixel == 4 && mask.GetPixels() != IntPtr.Zero;
        if (fastPixels) pixelBase = (byte*)mask.GetPixels().ToPointer();

        bool IsOpaque(int x, int y)
        {
            int alpha;
            if (fastPixels)
                alpha = pixelBase![y * mask.RowBytes + x * 4 + 3];
            else
                alpha = mask.GetPixel(x, y).Alpha;
            return alpha > 0 && alpha >= alphaThreshold;
        }

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            opaque[y * width + x] = IsOpaque(x, y);

        // Collect horizontal runs in row order first.  DFS/Concentric then reorder the same runs
        // instead of inventing a different drawing primitive, so brush/back-end behaviour is
        // unchanged and only the traversal differs.
        var rows = new List<List<Run>>();
        var runIndex = 0;
        var sampledRow = 0;
        for (var y = 0; y < height; y += rowStep, sampledRow++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowRuns = new List<Run>();

            var runStart = -1;
            for (var x = 0; x <= width; x++)
            {
                var isOpaque = x < width && opaque[y * width + x];
                if (isOpaque && runStart < 0) runStart = x;
                if (isOpaque || runStart < 0) continue;

                var runEnd = x - 1;
                if (runEnd >= runStart)
                {
                    var (start, end) = mapper.MapRun(y, runStart, runEnd);
                    EnsureLeftToRight(ref start, ref end);
                    var key = Key(start, end);
                    if (seen.Add(key))
                    {
                        var index = runIndex++;
                        rowRuns.Add(new Run(y, runStart, runEnd,
                            new PlannedStroke(new SKPoint(start.X, start.Y), new SKPoint(end.X, end.Y))));
                        for (var runX = runStart; runX <= runEnd; runX++)
                            runAt[y * width + runX] = index;
                    }
                }

                runStart = -1;
            }

            rows.Add(rowRuns);
        }

        var allRuns = rows.SelectMany(row => row).ToList();
        if (options.Algorithm == StrokeAlgorithm.Serpentine)
        {
            for (var row = 0; row < rows.Count; row++)
            {
                var rowRuns = rows[row];
                if (row % 2 == 0)
                {
                    foreach (var run in rowRuns) result.Add(run.Stroke);
                }
                else
                {
                    // Reverse both the line and (through the run order below) the row traversal.
                    for (var index = rowRuns.Count - 1; index >= 0; index--)
                        result.Add(rowRuns[index].Stroke.Reverse());
                }
            }
        }
        else if (allRuns.Count > 0)
        {
            var (components, labels) = BuildComponents(runAt, width, height, options.FourDirectional);
            result = options.Algorithm == StrokeAlgorithm.Concentric
                ? OrderConcentric(allRuns, runAt, components, labels, opaque, width, height, cancellationToken)
                : OrderDepthFirst(allRuns, runAt, components, labels, width, height, cancellationToken);
        }

        if (options.Order == StrokeOrder.NearestNeighbor && result.Count > 1)
            result = OrderNearest(result, options.Start ?? new ScreenPoint(mapper.Canvas.Left, mapper.Canvas.Top), cancellationToken);

        return result;
    }

    private static readonly (int Dx, int Dy)[] Dirs8 =
    {
        (0, -1), (1, 0), (0, 1), (-1, 0), (-1, -1), (1, -1), (1, 1), (-1, 1)
    };

    private static readonly (int Dx, int Dy)[] Dirs4 =
    {
        (0, -1), (1, 0), (0, 1), (-1, 0)
    };

    private static (List<int[]> Components, int[] Labels) BuildComponents(
        int[] runAt,
        int width,
        int height,
        bool fourDirectional)
    {
        var directions = fourDirectional ? Dirs4 : Dirs8;
        var labels = new int[runAt.Length];
        Array.Fill(labels, -1);
        var components = new List<int[]>();
        var stack = new Stack<int>();

        for (var index = 0; index < runAt.Length; index++)
        {
            if (runAt[index] < 0 || labels[index] >= 0) continue;
            var component = new List<int>();
            labels[index] = components.Count;
            stack.Push(index);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                component.Add(current);
                var x = current % width;
                var y = current / width;
                foreach (var (dx, dy) in directions)
                {
                    var nx = x + dx;
                    var ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    var next = ny * width + nx;
                    if (runAt[next] < 0 || labels[next] >= 0) continue;
                    labels[next] = components.Count;
                    stack.Push(next);
                }
            }
            components.Add(component.ToArray());
        }

        return (components, labels);
    }

    /// <summary>
    /// LxPainter "DFS (Default)": connected components are visited largest first, then each is
    /// walked depth-first with the reference direction order.  A run is emitted the first time the
    /// traversal reaches one of its pixels and oriented so it starts on the side that was entered.
    /// </summary>
    private static List<PlannedStroke> OrderDepthFirst(
        List<Run> runs,
        int[] runAt,
        List<int[]> components,
        int[] labels,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var emitted = new bool[runs.Count];
        var visited = new bool[runAt.Length];
        var stack = new Stack<int>();
        var result = new List<PlannedStroke>(runs.Count);

        foreach (var componentIndex in Enumerable.Range(0, components.Count)
                     .OrderByDescending(index => components[index].Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = components[componentIndex][0];
            visited[start] = true;
            stack.Push(start);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                var x = current % width;
                var y = current / width;
                var runIndex = runAt[current];
                if (runIndex >= 0 && !emitted[runIndex])
                {
                    emitted[runIndex] = true;
                    result.Add(OrientByEntry(runs[runIndex], x));
                }

                foreach (var (dx, dy) in Dirs8)
                {
                    var nx = x + dx;
                    var ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    var next = ny * width + nx;
                    if (labels[next] != componentIndex || visited[next]) continue;
                    visited[next] = true;
                    stack.Push(next);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// LxPainter "Concentric": walk outward from the component edge following increasing Chebyshev
    /// depth, penalising direction reversals, then bridge to the cheapest unvisited pixel when no
    /// adjacent one remains.  Runs are emitted as their first pixel is reached.
    /// </summary>
    private static List<PlannedStroke> OrderConcentric(
        List<Run> runs,
        int[] runAt,
        List<int[]> components,
        int[] labels,
        bool[] opaque,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var depths = DistanceTransform(opaque, width, height);
        var emitted = new bool[runs.Count];
        var result = new List<PlannedStroke>(runs.Count);
        var directions = Dirs8;

        foreach (var componentIndex in Enumerable.Range(0, components.Count)
                     .OrderByDescending(index => components[index].Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var unvisited = new HashSet<int>(components[componentIndex]);
            if (unvisited.Count == 0) continue;

            var start = unvisited
                .OrderBy(pixel => depths[pixel] == 1 ? 0 : 1)
                .ThenBy(pixel => pixel / width)
                .ThenBy(pixel => pixel % width)
                .First();
            var current = start;
            var directionX = 1;
            var directionY = 0;

            while (true)
            {
                unvisited.Remove(current);
                var runIndex = runAt[current];
                if (runIndex >= 0 && !emitted[runIndex])
                {
                    emitted[runIndex] = true;
                    result.Add(OrientByEntry(runs[runIndex], current % width));
                }
                if (unvisited.Count == 0) break;

                var currentDepth = depths[current];
                var currentX = current % width;
                var currentY = current / width;
                var next = -1;
                var bestCost = double.MaxValue;

                foreach (var (dx, dy) in directions)
                {
                    var nx = currentX + dx;
                    var ny = currentY + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    var candidate = ny * width + nx;
                    if (labels[candidate] != componentIndex || !unvisited.Contains(candidate)) continue;

                    var depth = depths[candidate];
                    var cost = depth > currentDepth ? (depth - currentDepth) * 8d : 0d;
                    var signX = Math.Sign(dx);
                    var signY = Math.Sign(dy);
                    if (signX != 0 && signX == -directionX) cost += 1;
                    if (signY != 0 && signY == -directionY) cost += 1;
                    if (cost >= bestCost) continue;
                    bestCost = cost;
                    next = candidate;
                }

                if (next < 0)
                {
                    // No neighbour left: bridge to the cheapest unvisited pixel.  Reference uses
                    // Euclidean distance plus a depth penalty and a smaller axis-aligned cost.
                    bestCost = double.MaxValue;
                    foreach (var candidate in unvisited)
                    {
                        var cx = candidate % width;
                        var cy = candidate / width;
                        var depth = depths[candidate];
                        var cost = Math.Sqrt(
                            (cx - currentX) * (cx - currentX) + (cy - currentY) * (cy - currentY));
                        if (depth > currentDepth) cost += (depth - currentDepth) * 8d;
                        var dr = cx - currentX;
                        var dc = cy - currentY;
                        var signX = Math.Sign(dr);
                        var signY = Math.Sign(dc);
                        if (directionX != 0 && signX == -directionX) cost += 1;
                        cost += Math.Abs(dr) * .5;
                        if (directionY != 0 && signY == -directionY) cost += 1;
                        cost += Math.Abs(dc) * .5;
                        if (cost >= bestCost) continue;
                        bestCost = cost;
                        next = candidate;
                    }
                }

                if (next < 0) break;
                var nextX = next % width;
                var nextY = next / width;
                var moveX = Math.Sign(nextX - currentX);
                var moveY = Math.Sign(nextY - currentY);
                if (moveX != 0 || moveY != 0)
                {
                    directionX = moveX;
                    directionY = moveY;
                }
                current = next;
            }
        }

        return result;
    }

    /// <summary>Chebyshev distance from every opaque pixel to the nearest transparent pixel.</summary>
    private static int[] DistanceTransform(bool[] opaque, int width, int height)
    {
        var depth = new int[opaque.Length];
        const int far = int.MaxValue / 4;
        for (var index = 0; index < opaque.Length; index++)
            depth[index] = opaque[index] ? far : 0;

        // Pixels on the canvas border touch the transparent outside even when the mask runs off the
        // edge; without this a filled canvas has no depth-1 pixel and "concentric" never starts on
        // its outer ring.
        for (var x = 0; x < width; x++)
        {
            if (opaque[x]) depth[x] = 1;
            var bottom = (height - 1) * width + x;
            if (opaque[bottom]) depth[bottom] = 1;
        }
        for (var y = 0; y < height; y++)
        {
            var left = y * width;
            if (opaque[left]) depth[left] = 1;
            var right = left + width - 1;
            if (opaque[right]) depth[right] = 1;
        }

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = y * width + x;
            if (depth[index] == 0) continue;
            var best = depth[index];
            if (x > 0) best = Math.Min(best, depth[index - 1] + 1);
            if (y > 0)
            {
                best = Math.Min(best, depth[index - width] + 1);
                if (x > 0) best = Math.Min(best, depth[index - width - 1] + 1);
                if (x < width - 1) best = Math.Min(best, depth[index - width + 1] + 1);
            }
            depth[index] = best;
        }

        for (var y = height - 1; y >= 0; y--)
        for (var x = width - 1; x >= 0; x--)
        {
            var index = y * width + x;
            if (depth[index] == 0) continue;
            var best = depth[index];
            if (x < width - 1) best = Math.Min(best, depth[index + 1] + 1);
            if (y < height - 1)
            {
                best = Math.Min(best, depth[index + width] + 1);
                if (x < width - 1) best = Math.Min(best, depth[index + width + 1] + 1);
                if (x > 0) best = Math.Min(best, depth[index + width - 1] + 1);
            }
            depth[index] = best;
        }

        return depth;
    }

    /// <summary>Starts the run at the side the traversal entered so consecutive runs stay joined.</summary>
    private static PlannedStroke OrientByEntry(Run run, int entryX)
    {
        var stroke = run.Stroke;
        var midpoint = (run.StartX + run.EndX) / 2d;
        return entryX > midpoint ? stroke.Reverse() : stroke;
    }

    private static void EnsureLeftToRight(ref ScreenPoint start, ref ScreenPoint end)
    {
        if (start.X <= end.X) return;
        (start, end) = (end, start);
    }

    private static (int, int, int, int) Key(ScreenPoint start, ScreenPoint end)
    {
        return start.X <= end.X
            ? (start.X, start.Y, end.X, end.Y)
            : (end.X, end.Y, start.X, start.Y);
    }

    /// <summary>
    /// Greedy nearest-run ordering accelerated with a coarse spatial grid.  Each run is registered
    /// at both endpoints, so the next stroke can be entered from whichever end is closer.
    /// </summary>
    private static List<PlannedStroke> OrderNearest(
        List<PlannedStroke> strokes,
        ScreenPoint start,
        CancellationToken cancellationToken)
    {
        const int cellSize = 32;
        var buckets = new Dictionary<(int X, int Y), List<int>>();
        var located = new bool[strokes.Count];
        for (var i = 0; i < strokes.Count; i++)
        {
            located[i] = true;
            AddToBucket(buckets, strokes[i].Start, i, cellSize);
            AddToBucket(buckets, strokes[i].End, i, cellSize);
        }

        var result = new List<PlannedStroke>(strokes.Count);
        var current = new SKPoint(start.X, start.Y);

        while (result.Count < strokes.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentCell = Cell(current.X, current.Y, cellSize);
            var bestIndex = -1;
            var bestDistance = double.MaxValue;
            var reverse = false;
            var foundRing = -1;

            for (var ring = 0; ring < 4096; ring++)
            {
                for (var cx = currentCell.X - ring; cx <= currentCell.X + ring; cx++)
                {
                    for (var cy = currentCell.Y - ring; cy <= currentCell.Y + ring; cy++)
                    {
                        // Only the perimeter is new for this ring.
                        if (ring > 0 &&
                            cx != currentCell.X - ring && cx != currentCell.X + ring &&
                            cy != currentCell.Y - ring && cy != currentCell.Y + ring)
                            continue;
                        if (!buckets.TryGetValue((cx, cy), out var indices)) continue;

                        foreach (var index in indices)
                        {
                            if (!located[index]) continue;
                            var candidate = strokes[index];
                            var startDistance = DistanceSquared(current, candidate.Start);
                            var endDistance = DistanceSquared(current, candidate.End);
                            if (startDistance <= endDistance && startDistance < bestDistance)
                            {
                                bestDistance = startDistance;
                                bestIndex = index;
                                reverse = false;
                            }
                            else if (endDistance < bestDistance)
                            {
                                bestDistance = endDistance;
                                bestIndex = index;
                                reverse = true;
                            }
                        }
                    }
                }

                if (bestIndex >= 0)
                {
                    if (foundRing < 0) foundRing = ring;
                    // One extra ring protects against a near edge of the next cell being closer
                    // than a far corner of the ring that produced the candidate.
                    if (ring >= foundRing + 2) break;
                }
            }

            if (bestIndex < 0)
            {
                // Defensive fallback: never drop a run because of an unexpected spatial edge case.
                bestIndex = Array.IndexOf(located, true);
                if (bestIndex < 0) break;
            }

            var chosen = reverse ? strokes[bestIndex].Reverse() : strokes[bestIndex];
            result.Add(chosen);
            located[bestIndex] = false;
            current = chosen.End;
        }

        return result;
    }

    private static void AddToBucket(
        Dictionary<(int X, int Y), List<int>> buckets,
        SKPoint point,
        int index,
        int cellSize)
    {
        var key = Cell(point.X, point.Y, cellSize);
        if (!buckets.TryGetValue(key, out var list))
        {
            list = new List<int>();
            buckets[key] = list;
        }
        if (!list.Contains(index)) list.Add(index);
    }

    private static (int X, int Y) Cell(float x, float y, int cellSize)
        => (FloorDiv((int)Math.Round(x), cellSize), FloorDiv((int)Math.Round(y), cellSize));

    private static int FloorDiv(int value, int divisor)
    {
        var quotient = value / divisor;
        if (value < 0 && value % divisor != 0) quotient--;
        return quotient;
    }

    private static double DistanceSquared(SKPoint a, SKPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    /// <summary>Renders a diagnostic bitmap of the whole plan.  The caller owns the result.</summary>
    public static SKBitmap RenderPreview(
        IEnumerable<LayerStrokePlan> plans,
        ScreenRect canvas,
        int brushSize)
    {
        var bitmap = new SKBitmap(Math.Max(1, canvas.Width), Math.Max(1, canvas.Height),
            SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var surface = new SKCanvas(bitmap);
        surface.Clear(new SKColor(232, 235, 239));

        using (var border = new SKPaint
               {
                   Style = SKPaintStyle.Stroke,
                   StrokeWidth = 1,
                   IsAntialias = false,
                   Color = new SKColor(90, 98, 108)
               })
        {
            surface.DrawRect(0.5f, 0.5f, canvas.Width - 1, canvas.Height - 1, border);
        }

        foreach (var plan in plans)
        {
            using var paint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(1, brushSize),
                StrokeCap = SKStrokeCap.Round,
                IsAntialias = false,
                Color = plan.Color
            };
            foreach (var stroke in plan.Strokes)
            {
                surface.DrawLine(
                    stroke.Start.X - canvas.Left,
                    stroke.Start.Y - canvas.Top,
                    stroke.End.X - canvas.Left,
                    stroke.End.Y - canvas.Top,
                    paint);
            }
        }

        return bitmap;
    }

    public static SKBitmap RenderPreview(
        IReadOnlyList<PlannedStroke> strokes,
        ScreenRect canvas,
        int brushSize,
        SKColor color)
        => RenderPreview(new[] { new LayerStrokePlan(color, strokes) }, canvas, brushSize);
}
