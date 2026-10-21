using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Autodraw.Configuration;

namespace Autodraw;

public partial class AreaSelectionOverlay : Window
{
    private readonly Screen _screen;
    private Point _start;
    private bool _dragging;

    public event EventHandler<ScreenRect?>? AreaSelected;

    public AreaSelectionOverlay(Screen screen)
    {
        _screen = screen;
        InitializeComponent();
        Position = new PixelPoint(screen.Bounds.X, screen.Bounds.Y);
        Width = Math.Max(1, screen.Bounds.Width / screen.Scaling);
        Height = Math.Max(1, screen.Bounds.Height / screen.Scaling);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _start = e.GetPosition(this);
        _dragging = true;
        SelectionBorder.IsVisible = true;
        UpdateSelection(_start, _start);
        e.Pointer.Capture(Surface);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        UpdateSelection(_start, e.GetPosition(this));
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        var end = e.GetPosition(this);
        var rect = ToScreenRect(_start, end);
        AreaSelected?.Invoke(this, rect);
        Close();
    }

    private void UpdateSelection(Point start, Point end)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var width = Math.Abs(end.X - start.X);
        var height = Math.Abs(end.Y - start.Y);
        Canvas.SetLeft(SelectionBorder, left);
        Canvas.SetTop(SelectionBorder, top);
        SelectionBorder.Width = width;
        SelectionBorder.Height = height;
        var rect = ToScreenRect(start, end);
        SizeText.Text = rect is null ? string.Empty : $"Area: ({rect.Value.Left},{rect.Value.Top}) {rect.Value.Width}x{rect.Value.Height}";
    }

    private ScreenRect? ToScreenRect(Point start, Point end)
    {
        var scale = _screen.Scaling;
        var x1 = _screen.Bounds.X + (int)Math.Round(Math.Min(start.X, end.X) * scale);
        var y1 = _screen.Bounds.Y + (int)Math.Round(Math.Min(start.Y, end.Y) * scale);
        var x2 = _screen.Bounds.X + (int)Math.Round(Math.Max(start.X, end.X) * scale);
        var y2 = _screen.Bounds.Y + (int)Math.Round(Math.Max(start.Y, end.Y) * scale);
        var rect = new ScreenRect(x1, y1, Math.Max(x1, x2 - 1), Math.Max(y1, y2 - 1));
        return rect.Width < 2 || rect.Height < 2 ? null : rect;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        AreaSelected?.Invoke(this, null);
        Close();
    }
}
