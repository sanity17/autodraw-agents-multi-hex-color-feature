using System;
using System.Globalization;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using Autodraw.Configuration;
using Autodraw.Painting.ColorInput;

namespace Autodraw;

/// <summary>
/// Calibration and color selection for the custom color picker shown by the target game.
/// Two modes are supported:
///   - "Wheel":  a hue/saturation wheel plus a separate brightness ramp (existing behavior).
///   - "Slider": three independent horizontal sliders for Hue, Saturation, and Value.
/// The active mode is persisted in the config entry <c>ColorPickerMode</c>. Existing wheel
/// keys (<c>ColorWheel*</c>, <c>ColorBrightness*</c>) are preserved untouched so older
/// configurations continue to work.
/// </summary>
public static class ColorPicker
{
    private const string ModeEntry = "ColorPickerMode";
    private const string WheelMode = "Wheel";
    private const string SliderMode = "Slider";

    public static string GetUsageInstructions() => string.Join(Environment.NewLine, new[]
    {
        "Color picker calibration",
        "",
        "Wheel (F5): click 4 points in order:",
        "1. Hue/saturation wheel center",
        "2. Wheel rim at center's hue",
        "3. Brightness/value ramp top",
        "4. Brightness/value ramp bottom",
        "",
        "Sliders (F7): click 6 points in order:",
        "1. Hue slider: left, then right",
        "2. Saturation slider: left, then right",
        "3. Value slider: left, then right",
        "",
        "HexedDrawer Color Box:",
        "1. Calibrate the hue slider: click its start, then its end",
        "2. Calibrate the box: click its top-left, then bottom-right",
        "",
        "AutoDraw minimizes during capture.",
        "It restores after the final click.",
        "Your selected mode is saved between launches.",
        "Use Set Color Hex in an action to select a color."
    });

    // ---------------------------------------------------------------------
    // Wheel mode
    // ---------------------------------------------------------------------

    public static bool UsesWheel =>
        Config.GetEntry(ModeEntry) == WheelMode && TryGetWheelCalibration(out _, out _, out _, out _);

    public static void SaveWheelCalibration(Vector2 center, Vector2 rim, Vector2 brightnessTop, Vector2 brightnessBottom)
    {
        Config.SetEntry(ModeEntry, WheelMode);
        Config.SetEntry("ColorWheelCenterX", center.X.ToString(CultureInfo.InvariantCulture));
        Config.SetEntry("ColorWheelCenterY", center.Y.ToString(CultureInfo.InvariantCulture));
        Config.SetEntry("ColorWheelRimX", rim.X.ToString(CultureInfo.InvariantCulture));
        Config.SetEntry("ColorWheelRimY", rim.Y.ToString(CultureInfo.InvariantCulture));
        Config.SetEntry("ColorBrightnessTopX", brightnessTop.X.ToString(CultureInfo.InvariantCulture));
        Config.SetEntry("ColorBrightnessTopY", brightnessTop.Y.ToString(CultureInfo.InvariantCulture));
        Config.SetEntry("ColorBrightnessBottomX", brightnessBottom.X.ToString(CultureInfo.InvariantCulture));
        Config.SetEntry("ColorBrightnessBottomY", brightnessBottom.Y.ToString(CultureInfo.InvariantCulture));
        UpdateConfig(c =>
        {
            c.ColorPickingMethod = "Circle";
            c.PickerType = "Wheel";
            c.WheelCenter = new ScreenPoint((int)center.X, (int)center.Y);
            c.WheelEdge = new ScreenPoint((int)rim.X, (int)rim.Y);
            c.WheelAngle = Math.Atan2(rim.Y - center.Y, rim.X - center.X) * 180d / Math.PI;
            c.WheelDark = new ScreenPoint((int)brightnessBottom.X, (int)brightnessBottom.Y);
            c.WheelBright = new ScreenPoint((int)brightnessTop.X, (int)brightnessTop.Y);
        });
    }

    // ---------------------------------------------------------------------
    // Slider mode
    // ---------------------------------------------------------------------

    public static bool UsesSlider =>
        Config.GetEntry(ModeEntry) == SliderMode && TryGetSliderCalibration(out _);

    /// <summary>True when at least one calibration mode is available for dispatching color selection.</summary>
    public static bool IsCalibrated => UsesWheel || UsesSlider;

    /// <summary>
    /// Persists the slider calibration. Each HSV channel is described by a min X, a max X, and a
    /// row Y; the caller supplies two samples per channel (left/right ends) and the midpoint of
    /// their Y values becomes the row's click height.
    /// </summary>
    public static void SaveSliderCalibration(
        Vector2 hueMin, Vector2 hueMax,
        Vector2 satMin, Vector2 satMax,
        Vector2 valMin, Vector2 valMax)
    {
        Config.SetEntry(ModeEntry, SliderMode);
        WriteChannelSlider("ColorSliderHue", hueMin, hueMax);
        WriteChannelSlider("ColorSliderSat", satMin, satMax);
        WriteChannelSlider("ColorSliderVal", valMin, valMax);
    }

    private static void WriteChannelSlider(string prefix, Vector2 a, Vector2 b)
    {
        var minX = MathF.Min(a.X, b.X);
        var maxX = MathF.Max(a.X, b.X);
        var midY = (a.Y + b.Y) / 2f;
        Config.SetEntry($"{prefix}MinX", minX.ToString(CultureInfo.InvariantCulture));
        Config.SetEntry($"{prefix}MaxX", maxX.ToString(CultureInfo.InvariantCulture));
        Config.SetEntry($"{prefix}Y", midY.ToString(CultureInfo.InvariantCulture));
    }

    // ---------------------------------------------------------------------
    // HexedDrawer color-circle / color-box calibration
    // ---------------------------------------------------------------------

    /// <summary>Persists only the hue/saturation wheel centre and rim, leaving the existing
    /// brightness ramp untouched. Used by the separate "Calibrate Color Circle" button.</summary>
    public static void SaveCircleCalibration(Vector2 center, Vector2 rim)
    {
        Config.SetEntry(ModeEntry, WheelMode);
        Config.SetEntry("ColorWheelCenterX", center.X.ToString(CultureInfo.InvariantCulture));
        Config.SetEntry("ColorWheelCenterY", center.Y.ToString(CultureInfo.InvariantCulture));
        Config.SetEntry("ColorWheelRimX", rim.X.ToString(CultureInfo.InvariantCulture));
        Config.SetEntry("ColorWheelRimY", rim.Y.ToString(CultureInfo.InvariantCulture));
        UpdateConfig(c => { c.ColorPickingMethod = "Circle"; c.PickerType = "Wheel"; c.WheelCenter = new ScreenPoint((int)center.X, (int)center.Y); c.WheelEdge = new ScreenPoint((int)rim.X, (int)rim.Y); c.WheelAngle = Math.Atan2(rim.Y - center.Y, rim.X - center.X) * 180d / Math.PI; });
    }

    /// <summary>Persists only the dark/bright ends of the brightness ramp.</summary>
    public static void SaveBrightnessCalibration(Vector2 dark, Vector2 bright)
    {
        Config.SetEntry(ModeEntry, WheelMode);
        Config.SetEntry("ColorBrightnessBottomX", dark.X.ToString(CultureInfo.InvariantCulture));
        Config.SetEntry("ColorBrightnessBottomY", dark.Y.ToString(CultureInfo.InvariantCulture));
        Config.SetEntry("ColorBrightnessTopX", bright.X.ToString(CultureInfo.InvariantCulture));
        Config.SetEntry("ColorBrightnessTopY", bright.Y.ToString(CultureInfo.InvariantCulture));
        UpdateConfig(c => { c.ColorPickingMethod = "Circle"; c.PickerType = "Wheel"; c.WheelDark = new ScreenPoint((int)dark.X, (int)dark.Y); c.WheelBright = new ScreenPoint((int)bright.X, (int)bright.Y); });
    }

    /// <summary>Persists the 2D color box calibration and the rainbow hue slider endpoints.</summary>
    public static void SaveBoxCalibration(Vector2 hueStart, Vector2 hueEnd, Vector2 topLeft, Vector2 bottomRight)
    {
        UpdateConfig(c =>
        {
            c.ColorPickingMethod = "Box";
            c.PickerType = "Box";
            c.BoxHueStart = new ScreenPoint((int)hueStart.X, (int)hueStart.Y);
            c.BoxHueEnd = new ScreenPoint((int)hueEnd.X, (int)hueEnd.Y);
            c.BoxTopLeft = new ScreenPoint((int)topLeft.X, (int)topLeft.Y);
            c.BoxBottomRight = new ScreenPoint((int)bottomRight.X, (int)bottomRight.Y);
        });
    }

    private static void UpdateConfig(Action<AutoDrawConfig> update)
    {
        try
        {
            var config = DdConfigService.Current;
            update(config);
            if (!DdConfigService.TryApply(config, out var error)) Utils.Log("Color calibration write-back rejected: " + error);
        }
        catch (Exception ex)
        {
            Utils.Log("Color calibration write-back failed: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------------
    // Dispatch
    // ---------------------------------------------------------------------

    /// <summary>
    /// Selects a color in the target game's picker. Dispatches on the persisted
    /// <c>ColorPickerMode</c>; wheel mode is the default and keeps every existing
    /// wheel config key unchanged.
    /// </summary>
    public static async Task SelectWheelColor(string hex)
    {
        if (Config.GetEntry(ModeEntry) == SliderMode)
        {
            await SelectSliderColor(hex);
            return;
        }

        if (!TryGetWheelCalibration(out var center, out var rim, out var brightnessTop, out var brightnessBottom)) return;

        var color = SKColor.Parse(hex);
        color.ToHsv(out var hue, out var saturation, out var value);
        var radius = Vector2.Distance(center, rim);

        // This game's wheel is red at 9 o'clock, then yellow/green clockwise.
        var angle = (hue + 180f) * MathF.PI / 180f;
        var wheelPoint = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (radius * saturation / 100f);
        Input.MoveTo((short)MathF.Round(wheelPoint.X), (short)MathF.Round(wheelPoint.Y));
        Input.SendClick(Input.MouseTypes.MouseLeft);

        await Drawing.NOP(500_000);
        var brightnessPoint = Vector2.Lerp(brightnessTop, brightnessBottom, 1f - value / 100f);
        Input.MoveTo((short)MathF.Round(brightnessPoint.X), (short)MathF.Round(brightnessPoint.Y));
        Input.SendClick(Input.MouseTypes.MouseLeft);
        await Drawing.NOP(500_000);
    }

    /// <summary>Selects a colour using the portable dd.json calibration, falling back to legacy calibration.</summary>
    public static async Task SelectColor(string hex)
    {
        var config = DdConfigService.Current;
        if (config.EnableMacros && config.OpenPickerCoordinate is { } open)
        {
            Click(open);
            await Drawing.DelaySeconds(config.DelayOpen);
        }

        var selected = false;
        var color = SKColor.Parse(hex);
        if (config.PickerType.Equals("RGB", StringComparison.OrdinalIgnoreCase) && config.RgbCoordinates.Count == 3)
        {
            var channels = new[] { color.Red, color.Green, color.Blue };
            for (var i = 0; i < 3; i++) { Click(config.RgbCoordinates[i]); Input.ReplaceFocusedText(channels[i].ToString(CultureInfo.InvariantCulture)); await Drawing.DelaySeconds(config.DelayInput); }
            selected = true;
        }
        else if (config.PickerType.Equals("HEX", StringComparison.OrdinalIgnoreCase) && config.HexCoordinate is { } hexPoint)
        {
            Click(hexPoint); Input.ReplaceFocusedText(hex); selected = true;
        }
        else if (config.PickerType.Equals("HSV", StringComparison.OrdinalIgnoreCase) && config.HsvCoordinates.Count == 3)
        {
            color.ToHsv(out var hue, out var saturation, out var value);
            var values = new[] { hue, saturation, value };
            for (var i = 0; i < 3; i++) { Click(config.HsvCoordinates[i]); Input.ReplaceFocusedText(Math.Round(values[i]).ToString(CultureInfo.InvariantCulture)); await Drawing.DelaySeconds(config.DelayInput); }
            selected = true;
        }
        else if (config.PickerType.Equals("Wheel", StringComparison.OrdinalIgnoreCase) && config.WheelCenter is { } center && config.WheelEdge is { } edge && config.WheelDark is { } dark && config.WheelBright is { } bright)
        {
            await SelectConfiguredWheel(color, center, edge, dark, bright, config.WheelDirection, config.WheelAngle); selected = true;
        }
        else if (config.PickerType.Equals("Box", StringComparison.OrdinalIgnoreCase) &&
                 config.BoxHueStart is { } hueStart && config.BoxHueEnd is { } hueEnd &&
                 config.BoxTopLeft is { } boxTopLeft && config.BoxBottomRight is { } boxBottomRight)
        {
            await SelectConfiguredBox(color, hueStart, hueEnd, boxTopLeft, boxBottomRight); selected = true;
        }
        else if (config.IsPaletteMatchPicker)
        {
            // No wheel drive: the nearest captured palette slot's screen coordinate is
            // clicked instead.  selected stays false when no slot resolves, so the
            // legacy wheel fallback below still runs as a safety net.
            var paletteInput = new PaletteGridInput();
            if (paletteInput.IsCalibrated)
            {
                await paletteInput.ApplyColor(hex, CancellationToken.None);
                selected = true;
            }
        }

        if (!selected) await SelectWheelColor(hex);
        if (config.EnableMacros && config.ClosePickerCoordinate is { } close)
        {
            await Drawing.DelaySeconds(config.DelayClose);
            Click(close);
        }
    }

    private static void Click(ScreenPoint point) { Input.MoveTo((short)point.X, (short)point.Y); Input.SendClick(Input.MouseTypes.MouseLeft); }
    private static async Task SelectConfiguredWheel(SKColor color, ScreenPoint center, ScreenPoint edge, ScreenPoint dark, ScreenPoint bright, string direction, double offsetDegrees)
    {
        color.ToHsv(out var hue, out var saturation, out var value);
        var c = new Vector2(center.X, center.Y); var radius = Vector2.Distance(c, new Vector2(edge.X, edge.Y));
        var sign = direction.Equals("CCW", StringComparison.OrdinalIgnoreCase) ? -1f : 1f;
        var angle = ((float)offsetDegrees + sign * hue) * MathF.PI / 180f;
        var point = c + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius * saturation / 100f;
        Input.MoveTo((short)MathF.Round(point.X), (short)MathF.Round(point.Y)); Input.SendClick(Input.MouseTypes.MouseLeft);
        await Drawing.DelaySeconds(DdConfigService.Current.DelayInput);
        var brightness = Vector2.Lerp(new Vector2(dark.X, dark.Y), new Vector2(bright.X, bright.Y), value / 100f);
        Input.MoveTo((short)MathF.Round(brightness.X), (short)MathF.Round(brightness.Y)); Input.SendClick(Input.MouseTypes.MouseLeft);
    }

    private static async Task SelectConfiguredBox(SKColor color, ScreenPoint hueStart, ScreenPoint hueEnd, ScreenPoint topLeft, ScreenPoint bottomRight)
    {
        color.ToHsv(out var hue, out var saturation, out var value);
        var huePoint = Vector2.Lerp(new Vector2(hueStart.X, hueStart.Y), new Vector2(hueEnd.X, hueEnd.Y), Math.Clamp(hue / 360f, 0f, 1f));
        Input.MoveTo((short)MathF.Round(huePoint.X), (short)MathF.Round(huePoint.Y));
        Input.SendClick(Input.MouseTypes.MouseLeft);
        await Drawing.DelaySeconds(DdConfigService.Current.DelayInput);
        var boxPoint = new Vector2(
            topLeft.X + Math.Clamp(saturation / 100f, 0f, 1f) * (bottomRight.X - topLeft.X),
            topLeft.Y + (1f - Math.Clamp(value / 100f, 0f, 1f)) * (bottomRight.Y - topLeft.Y));
        Input.MoveTo((short)MathF.Round(boxPoint.X), (short)MathF.Round(boxPoint.Y));
        Input.SendClick(Input.MouseTypes.MouseLeft);
    }

    /// <summary>Selects a color by clicking each HSV slider at the position mapped from <paramref name="hex"/>.</summary>
    public static async Task SelectSliderColor(string hex)
    {
        if (!TryGetSliderCalibration(out var calibration)) return;

        var color = SKColor.Parse(hex);
        color.ToHsv(out var hue, out var saturation, out var value);

        await ClickChannelSlider(calibration.Hue, hue / 360f);
        await ClickChannelSlider(calibration.Sat, saturation / 100f);
        await ClickChannelSlider(calibration.Val, value / 100f);
    }

    private static async Task ClickChannelSlider(ChannelSlider slider, float t)
    {
        var x = slider.At(t);
        Input.MoveTo((short)MathF.Round(x), (short)MathF.Round(slider.Y));
        Input.SendClick(Input.MouseTypes.MouseLeft);
        await Drawing.NOP(500_000);
    }

    // ---------------------------------------------------------------------
    // Calibration readers
    // ---------------------------------------------------------------------

    /// <summary>A single horizontal slider described by its min X, max X, and click row Y.</summary>
    public readonly record struct ChannelSlider(float MinX, float MaxX, float Y)
    {
        public float At(float t) => MinX + Math.Clamp(t, 0f, 1f) * (MaxX - MinX);
        public bool IsValid => MathF.Abs(MaxX - MinX) > 5;
    }

    /// <summary>The complete slider calibration for all three HSV channels.</summary>
    public readonly record struct SliderCalibration(ChannelSlider Hue, ChannelSlider Sat, ChannelSlider Val)
    {
        public bool IsValid => Hue.IsValid && Sat.IsValid && Val.IsValid;
    }

    public static bool TryGetSliderCalibration(out SliderCalibration calibration)
    {
        calibration = default;
        if (!TryReadChannelSlider("ColorSliderHue", out var hue)) return false;
        if (!TryReadChannelSlider("ColorSliderSat", out var sat)) return false;
        if (!TryReadChannelSlider("ColorSliderVal", out var val)) return false;
        calibration = new SliderCalibration(hue, sat, val);
        return calibration.IsValid;
    }

    private static bool TryReadChannelSlider(string prefix, out ChannelSlider slider)
    {
        slider = default;
        if (!TryRead($"{prefix}MinX", out var minX)) return false;
        if (!TryRead($"{prefix}MaxX", out var maxX)) return false;
        if (!TryRead($"{prefix}Y", out var y)) return false;
        slider = new ChannelSlider(minX, maxX, y);
        return slider.IsValid;
    }

    private static bool TryGetWheelCalibration(out Vector2 center, out Vector2 rim, out Vector2 brightnessTop, out Vector2 brightnessBottom)
    {
        center = rim = brightnessTop = brightnessBottom = default;
        return TryRead("ColorWheelCenterX", out center.X) && TryRead("ColorWheelCenterY", out center.Y)
            && TryRead("ColorWheelRimX", out rim.X) && TryRead("ColorWheelRimY", out rim.Y)
            && TryRead("ColorBrightnessTopX", out brightnessTop.X) && TryRead("ColorBrightnessTopY", out brightnessTop.Y)
            && TryRead("ColorBrightnessBottomX", out brightnessBottom.X) && TryRead("ColorBrightnessBottomY", out brightnessBottom.Y)
            && Vector2.Distance(center, rim) > 5 && Vector2.Distance(brightnessTop, brightnessBottom) > 5;
    }

    private static bool TryRead(string key, out float value) => float.TryParse(Config.GetEntry(key), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
