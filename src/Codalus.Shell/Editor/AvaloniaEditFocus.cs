using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaEdit;

namespace Codalus.Shell.Editor;

/// <summary>
/// AvaloniaEdit's commands keep the last element that got the focus in a static (<c>RoutedCommand._inputElement</c>,
/// their target when run as an <see cref="System.Windows.Input.ICommand"/>), and never clear it: a closed editor or
/// window that had the focus stayed reachable through it, with its view and its shell, until another element took the
/// focus. A null target makes those commands do nothing, as before any element had the focus.
/// </summary>
internal static class AvaloniaEditFocus
{
    /// <summary>Forgets the last focused element when it is <paramref name="closing"/> or inside it.</summary>
    public static void Release(Visual closing)
    {
        try
        {
            ref var element = ref LastFocused(null);
            if (element is Visual visual && (visual == closing || closing.IsVisualAncestorOf(visual)))
            {
                element = null;
            }
        }
        catch (MissingFieldException)
        {
            // Another AvaloniaEdit, which keeps no such static.
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "_inputElement")]
    private static extern ref IInputElement? LastFocused(RoutedCommand? command);
}
