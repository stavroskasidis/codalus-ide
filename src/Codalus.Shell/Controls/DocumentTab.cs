using Avalonia;
using Avalonia.Controls;

namespace Codalus.Shell.Controls;

/// <summary>
/// A document tab's states that Dock's tab doesn't know (M34d): preview and pinned. ShellStyles.axaml binds them from
/// the document and styles the tab by them (the preview tab's color, the pinned tab's pin).
/// </summary>
public static class DocumentTab
{
    public static readonly AttachedProperty<bool> IsPreviewProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsPreview", typeof(DocumentTab));

    public static readonly AttachedProperty<bool> IsPinnedProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsPinned", typeof(DocumentTab));

    public static bool GetIsPreview(Control control) => control.GetValue(IsPreviewProperty);

    public static void SetIsPreview(Control control, bool value) => control.SetValue(IsPreviewProperty, value);

    public static bool GetIsPinned(Control control) => control.GetValue(IsPinnedProperty);

    public static void SetIsPinned(Control control, bool value) => control.SetValue(IsPinnedProperty, value);
}
