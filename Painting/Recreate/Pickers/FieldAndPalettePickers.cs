using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodraw.Configuration;
using Autodraw.Painting;
using SkiaSharp;

namespace Autodraw.Painting.Recreate;

/// <summary>Shared input sequence for LxPainter's HEX, RGB and HSV text-field pickers.</summary>
public abstract class TextFieldColorPicker : IColorPicker
{
    private readonly IInputSimulator _input;
    private readonly RecreateOptions _options;
    private readonly IReadOnlyList<ScreenPoint> _points;

    protected TextFieldColorPicker(IInputSimulator input, RecreateOptions options, IReadOnlyList<ScreenPoint> points)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _points = points ?? throw new ArgumentNullException(nameof(points));
    }

    protected abstract IReadOnlyList<string> Values(SKColor color);

    public async Task SelectColorAsync(SKColor color, CancellationToken cancellationToken)
    {
        var values = Values(color);
        if (values.Count != _points.Count)
            throw new ColorSelectionException("The text-field calibration does not match the number of colour channels.");

        for (var i = 0; i < _points.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _input.ClickAsync(_points[i], cancellationToken).ConfigureAwait(false);
            await Delay(cancellationToken).ConfigureAwait(false);
            await _input.ReplaceTextAsync(values[i], cancellationToken).ConfigureAwait(false);
            await Delay(cancellationToken).ConfigureAwait(false);
        }
    }

    private Task Delay(CancellationToken token) => _options.DelayAsync(_options.PickerStepDelayMs, token);
}

public sealed class HexColorPicker : TextFieldColorPicker
{
    public HexColorPicker(IInputSimulator input, RecreateOptions options, ScreenPoint field)
        : base(input, options, new[] { field }) { }

    protected override IReadOnlyList<string> Values(SKColor color) =>
        new[] { $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}" };
}

public sealed class RgbColorPicker : TextFieldColorPicker
{
    public RgbColorPicker(IInputSimulator input, RecreateOptions options, IReadOnlyList<ScreenPoint> fields)
        : base(input, options, fields) { }

    protected override IReadOnlyList<string> Values(SKColor color) => new[]
    {
        color.Red.ToString(CultureInfo.InvariantCulture),
        color.Green.ToString(CultureInfo.InvariantCulture),
        color.Blue.ToString(CultureInfo.InvariantCulture)
    };
}

public sealed class HsvColorPicker : TextFieldColorPicker
{
    public HsvColorPicker(IInputSimulator input, RecreateOptions options, IReadOnlyList<ScreenPoint> fields)
        : base(input, options, fields) { }

    protected override IReadOnlyList<string> Values(SKColor color)
    {
        var hsv = ColorMath.ToHsv(color);
        return new[]
        {
            Math.Round(hsv.H).ToString(CultureInfo.InvariantCulture),
            Math.Round(hsv.S * 100d).ToString(CultureInfo.InvariantCulture),
            Math.Round(hsv.V * 100d).ToString(CultureInfo.InvariantCulture)
        };
    }
}

public abstract class FieldPickerDefinition : IPickerDefinition
{
    public abstract string Id { get; }
    public abstract string DisplayNameKey { get; }
    public abstract IReadOnlyList<PickerPointRequirement> Points { get; }

    public string? Validate(PickerCalibration calibration, ScreenRect? screen)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        foreach (var requirement in Points)
        {
            if (calibration.Point(requirement.Key) is not { } point)
                return $"{Id} calibration is missing '{requirement.Key}'.";
            if (screen is { } bounds && !ScreenGeometry.Contains(bounds, point))
                return $"{Id} calibration point ({point.X}, {point.Y}) is outside the screen.";
        }
        return null;
    }

    public abstract IColorPicker Create(IInputSimulator input, PickerCalibration calibration, RecreateOptions options, Action<string>? log);

    protected static IReadOnlyList<ScreenPoint> RequiredPoints(PickerCalibration calibration, IEnumerable<string> keys)
    {
        var points = new List<ScreenPoint>();
        foreach (var key in keys)
        {
            if (calibration.Point(key) is not { } point)
                throw new ColorSelectionException($"Colour picker calibration is missing '{key}'.");
            points.Add(point);
        }
        return points;
    }
}

public sealed class HexPickerDefinition : FieldPickerDefinition
{
    public override string Id => "HEX";
    public override string DisplayNameKey => "picker_style_hex";
    public override IReadOnlyList<PickerPointRequirement> Points { get; } = new[]
    {
        new PickerPointRequirement(PickerPoints.HexField, "calib_step_hex")
    };

    public override IColorPicker Create(IInputSimulator input, PickerCalibration calibration, RecreateOptions options, Action<string>? log)
        => new HexColorPicker(input, options, RequiredPoints(calibration, Points.Select(point => point.Key))[0]);
}

public sealed class RgbPickerDefinition : FieldPickerDefinition
{
    public override string Id => "RGB";
    public override string DisplayNameKey => "picker_style_rgb";
    public override IReadOnlyList<PickerPointRequirement> Points { get; } = new[]
    {
        new PickerPointRequirement(PickerPoints.RgbRed, "calib_step_rgb_r"),
        new PickerPointRequirement(PickerPoints.RgbGreen, "calib_step_rgb_g"),
        new PickerPointRequirement(PickerPoints.RgbBlue, "calib_step_rgb_b")
    };

    public override IColorPicker Create(IInputSimulator input, PickerCalibration calibration, RecreateOptions options, Action<string>? log)
        => new RgbColorPicker(input, options, RequiredPoints(calibration, Points.Select(point => point.Key)));
}

public sealed class HsvPickerDefinition : FieldPickerDefinition
{
    public override string Id => "HSV";
    public override string DisplayNameKey => "picker_style_hsv";
    public override IReadOnlyList<PickerPointRequirement> Points { get; } = new[]
    {
        new PickerPointRequirement(PickerPoints.HsvHue, "calib_step_hsv_h"),
        new PickerPointRequirement(PickerPoints.HsvSaturation, "calib_step_hsv_s"),
        new PickerPointRequirement(PickerPoints.HsvValue, "calib_step_hsv_v")
    };

    public override IColorPicker Create(IInputSimulator input, PickerCalibration calibration, RecreateOptions options, Action<string>? log)
        => new HsvColorPicker(input, options, RequiredPoints(calibration, Points.Select(point => point.Key)));
}

/// <summary>Clicks the captured swatch nearest to the requested colour using CIE ΔE2000.</summary>
public sealed class PaletteMatchColorPicker : IColorPicker
{
    private readonly IInputSimulator _input;
    private readonly RecreateOptions _options;
    private readonly IReadOnlyList<PaletteSlot> _slots;

    public PaletteMatchColorPicker(IInputSimulator input, RecreateOptions options, IReadOnlyList<PaletteSlot> slots)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _slots = slots ?? throw new ArgumentNullException(nameof(slots));
    }

    public async Task SelectColorAsync(SKColor color, CancellationToken cancellationToken)
    {
        PaletteSlot? best = null;
        var bestDistance = double.MaxValue;
        foreach (var slot in _slots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SKColor.TryParse(slot.Hex, out var candidate)) continue;
            var distance = ColorMatcher.DeltaE2000(color, candidate);
            if (distance >= bestDistance) continue;
            best = slot;
            bestDistance = distance;
        }
        if (best is not { } selected)
            throw new ColorSelectionException("Palette Match has no valid captured colours.");

        await _input.ClickAsync(selected.Coord, cancellationToken).ConfigureAwait(false);
        await _options.DelayAsync(_options.PickerStepDelayMs, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class PaletteMatchPickerDefinition : IPickerDefinition
{
    public string Id => "Palette Match";
    public string DisplayNameKey => "picker_style_palette";
    public IReadOnlyList<PickerPointRequirement> Points { get; } = Array.Empty<PickerPointRequirement>();

    public string? Validate(PickerCalibration calibration, ScreenRect? screen)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        if (calibration.PaletteSlots.Count == 0) return "Palette Match requires at least one captured palette swatch.";
        foreach (var slot in calibration.PaletteSlots)
        {
            if (!SKColor.TryParse(slot.Hex, out _)) return $"Palette colour '{slot.Hex}' is not #RRGGBB.";
            if (screen is { } bounds && !ScreenGeometry.Contains(bounds, slot.Coord))
                return $"Palette slot ({slot.Coord.X}, {slot.Coord.Y}) is outside the screen.";
        }
        return null;
    }

    public IColorPicker Create(IInputSimulator input, PickerCalibration calibration, RecreateOptions options, Action<string>? log)
    {
        if (Validate(calibration, null) is { } error) throw new ColorSelectionException(error);
        return new PaletteMatchColorPicker(input, options, calibration.PaletteSlots.ToArray());
    }
}
