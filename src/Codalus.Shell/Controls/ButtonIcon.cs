using Avalonia;
using Avalonia.Controls;

namespace Codalus.Shell.Controls;

/// <summary>
/// The icon of a button in the control set (M34h, docs/design/controls.md): an <see cref="Icon"/> kind on a
/// <c>Button</c>, <c>ToggleButton</c> or <c>DropDownButton</c>, drawn by the <c>icon</c> class (icon only: the button's
/// name goes in <c>AutomationProperties.Name</c> and its tooltip) or the <c>iconText</c> class (the icon, then the
/// button's text, which stays its name):
/// <code>
/// &lt;Button Classes="iconText" controls:ButtonIcon.Kind="add" Content="{l:T containers.add}" /&gt;
/// &lt;Button Classes="icon" controls:ButtonIcon.Kind="refresh" ToolTip.Tip="…" AutomationProperties.Name="…" /&gt;
/// </code>
/// The icon takes its kind's palette color like any <see cref="Icon"/>, the accent's text on a primary button.
/// </summary>
public static class ButtonIcon
{
    public static readonly AttachedProperty<string?> KindProperty =
        AvaloniaProperty.RegisterAttached<ContentControl, string?>("Kind", typeof(ButtonIcon));

    public static string? GetKind(ContentControl control) => control.GetValue(KindProperty);

    public static void SetKind(ContentControl control, string? value) => control.SetValue(KindProperty, value);
}
