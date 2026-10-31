using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Autodraw;

/// <summary>Which physical mouse button produced a calibration click.</summary>
public enum ClickEdge
{
    None,
    Left,
    Right
}

/// <summary>
/// Turns a stream of mouse-button snapshots into one event per press. Pure state so the edge
/// handling can be unit tested without touching Windows or the global hook.
/// </summary>
public sealed class ClickEdgeDetector
{
    private bool _leftDown;
    private bool _rightDown;

    /// <summary>
    /// Buttons are treated as already pressed initially, so the click that opened the calibration
    /// flow is never counted as a calibration point.
    /// </summary>
    public ClickEdgeDetector(bool leftDown = true, bool rightDown = true)
    {
        _leftDown = leftDown;
        _rightDown = rightDown;
    }

    /// <summary>Returns the button that went down since the previous call, if any.</summary>
    public ClickEdge Update(bool leftDown, bool rightDown)
    {
        var click = ClickEdge.None;
        if (leftDown && !_leftDown) click = ClickEdge.Left;
        else if (rightDown && !_rightDown) click = ClickEdge.Right;
        _leftDown = leftDown;
        _rightDown = rightDown;
        return click;
    }
}

/// <summary>
/// Captures calibration clicks by polling the physical mouse state instead of relying on the
/// global SharpHook hook.  The low-level hook can fail to install (SetWindowsHookEx) or can stop
/// delivering events while still reporting itself as running, which left picker calibration stuck
/// at "0 / 1 clicks".  Polling also treats a click with a tiny pointer movement as a click, unlike
/// SharpHook's MouseClicked event which is only raised when the pointer did not move at all.
/// Only active while a calibration is in progress; disposed as soon as it finishes.
/// </summary>
public sealed class CalibrationClickWatcher : IDisposable
{
    private const int LeftButton = 0x01;
    private const int RightButton = 0x02;

    private readonly Action<int, int, ClickEdge> _onClick;
    private readonly object _gate = new();
    private ClickEdgeDetector _detector;
    private Timer? _timer;
    private bool _disposed;

    public CalibrationClickWatcher(Action<int, int, ClickEdge> onClick)
    {
        _onClick = onClick ?? throw new ArgumentNullException(nameof(onClick));
        _detector = OperatingSystem.IsWindows()
            ? new ClickEdgeDetector(IsButtonDown(LeftButton), IsButtonDown(RightButton))
            : new ClickEdgeDetector();
        if (OperatingSystem.IsWindows())
            _timer = new Timer(_ => Poll(), null, 10, 10);
    }

    private void Poll()
    {
        lock (_gate)
        {
            if (_disposed) return;

            var click = _detector.Update(IsButtonDown(LeftButton), IsButtonDown(RightButton));
            if (click == ClickEdge.None) return;
            if (!GetCursorPos(out var point)) return;

            try
            {
                _onClick(point.X, point.Y, click);
            }
            catch (Exception ex)
            {
                Utils.Log("Calibration click watcher failed: " + ex);
            }
        }
    }

    private static bool IsButtonDown(int virtualKey)
        => OperatingSystem.IsWindows() && (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
