using System;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace Autodraw.Painting.Recreate;

/// <summary>Adds LxPainter's optional open/close macro around any registered colour picker.</summary>
public sealed class MacroColorPicker : IColorPicker
{
    private readonly IColorPicker _inner;
    private readonly IInputSimulator _input;
    private readonly RecreateOptions _options;

    public MacroColorPicker(IColorPicker inner, IInputSimulator input, RecreateOptions options)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task SelectColorAsync(SKColor color, CancellationToken cancellationToken)
    {
        if (_options.MacrosEnabled && _options.OpenPickerPoint is { } open)
        {
            await _options.DelayAsync(_options.OpenPickerDelayMs, cancellationToken).ConfigureAwait(false);
            await _input.ClickAsync(open, cancellationToken).ConfigureAwait(false);
            await _options.DelayAsync(_options.OpenPickerDelayMs, cancellationToken).ConfigureAwait(false);
        }

        await _inner.SelectColorAsync(color, cancellationToken).ConfigureAwait(false);

        if (_options.MacrosEnabled && _options.ClosePickerPoint is { } close)
        {
            await _options.DelayAsync(_options.ClosePickerDelayMs, cancellationToken).ConfigureAwait(false);
            await _input.ClickAsync(close, cancellationToken).ConfigureAwait(false);
            await _options.DelayAsync(_options.ClosePickerDelayMs, cancellationToken).ConfigureAwait(false);
        }
    }
}
