using Avalonia;
using Avalonia.Controls;
using Codalus.Shell.Views;

namespace Codalus.Shell;

/// <summary>
/// The windows that cover the main window (the setup, the start window): the main window owns them, so they stay above
/// it, and the one opened last draws the main window's notifications, palette and in-window dialogs
/// (<see cref="CompanionOverlays"/>).
/// </summary>
public sealed partial class ShellHost
{
    private readonly List<Window> _companions = [];

    /// <summary>The window that draws the notifications, the palette and the in-window dialogs: null for the main window.</summary>
    internal Window? OverlayWindow => _companions.Count > 0 ? _companions[^1] : null;

    /// <summary><see cref="OverlayWindow"/> changed: a covering window opened or closed.</summary>
    internal event EventHandler? OverlayWindowChanged;

    /// <summary>
    /// Makes <paramref name="window"/> (not shown yet) draw the overlays while it's open, over its content.
    /// <paramref name="toastMargin"/> keeps the notifications off its own buttons.
    /// </summary>
    private CompanionOverlays AddCompanion(Window window, Thickness toastMargin)
    {
        var overlays = CompanionOverlays.Attach(this, window, toastMargin);
        _companions.Add(window);
        window.Closed += (_, _) =>
        {
            _companions.Remove(window);
            OverlayWindowChanged?.Invoke(this, EventArgs.Empty);
        };
        OverlayWindowChanged?.Invoke(this, EventArgs.Empty);
        return overlays;
    }
}
