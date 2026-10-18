using Avalonia;
using Avalonia.Controls;

namespace Codalus.Shell.Controls;

/// <summary>
/// A segmented control (M34h, docs/design/controls.md): one choice of two to four views of the same content (Form or
/// XML, Tree or List, Pretty or Raw, User or Workspace), 24 px, the chosen segment filled with the accent. It replaces a
/// <c>TabControl</c> used as a view switch and pairs of <c>ToggleButton</c>s; tabs stay for different content.
/// <para>
/// A list underneath, so the keyboard and screen readers know it: Tab reaches the chosen segment, the arrow keys (and Home
/// and End) move the choice, and each segment is a list item named by its text. Give the control an
/// <c>AutomationProperties.Name</c> (what it switches) and bind <c>SelectedIndex</c> or <c>SelectedItem</c>:
/// </para>
/// <code>
/// &lt;controls:SegmentedControl SelectedIndex="{Binding ViewIndex}" AutomationProperties.Name="{l:T findResults.view}"&gt;
///   &lt;controls:SegmentedControlItem Icon="tree" Content="{l:T findResults.tree}" /&gt;
///   &lt;controls:SegmentedControlItem Icon="list" Content="{l:T findResults.list}" /&gt;
/// &lt;/controls:SegmentedControl&gt;
/// </code>
/// </summary>
public class SegmentedControl : ListBox
{
    static SegmentedControl()
    {
        SelectionModeProperty.OverrideDefaultValue<SegmentedControl>(SelectionMode.Single | SelectionMode.AlwaysSelected);
    }

    protected override Type StyleKeyOverride => typeof(SegmentedControl);

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => new SegmentedControlItem();

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey) =>
        NeedsContainer<SegmentedControlItem>(item, out recycleKey);
}

/// <summary>A segment of a <see cref="SegmentedControl"/>: its text, and an optional <see cref="Icon"/> kind before it.</summary>
public class SegmentedControlItem : ListBoxItem
{
    public static readonly StyledProperty<string?> IconProperty = AvaloniaProperty.Register<SegmentedControlItem, string?>(nameof(Icon));

    protected override Type StyleKeyOverride => typeof(SegmentedControlItem);

    /// <summary>An icon before the text (an <see cref="Controls.Icon"/> kind), or null.</summary>
    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}
