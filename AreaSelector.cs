using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Autodraw.Configuration;

namespace Autodraw;

/// <summary>Shows one borderless selection overlay per monitor and returns the physical screen
/// rectangle the user dragged.  Used by the HexedDrawer Main tab and the F1 hotkey.</summary>
public static class AreaSelector
{
    private static bool _selecting;

    public static async Task<ScreenRect?> SelectAsync(Window owner)
    {
        if (_selecting) return null;
        _selecting = true;
        var overlays = new List<AreaSelectionOverlay>();
        var completion = new TaskCompletionSource<ScreenRect?>();
        try
        {
            owner.WindowState = WindowState.Minimized;
            await Task.Delay(150);
            foreach (var screen in owner.Screens.All)
            {
                var overlay = new AreaSelectionOverlay(screen);
                void OnAreaSelected(object? sender, ScreenRect? rect)
                {
                    if (!completion.TrySetResult(rect)) return;
                    foreach (var candidate in overlays) candidate.Close();
                }
                overlay.AreaSelected += OnAreaSelected;
                overlays.Add(overlay);
                overlay.Show();
            }

            var result = await completion.Task;
            return result;
        }
        finally
        {
            foreach (var overlay in overlays)
            {
                try { overlay.Close(); } catch { /* already closed */ }
            }
            _selecting = false;
            owner.WindowState = WindowState.Normal;
            owner.Activate();
        }
    }
}
