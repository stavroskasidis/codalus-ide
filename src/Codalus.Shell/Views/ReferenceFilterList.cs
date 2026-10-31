using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// A References column filter's values (<see cref="ReferenceFilterValue"/>), each with its check box, in a list box that
/// creates only the rows in view. The list is one Tab stop: reaching it selects a row (the first checked one, else the
/// first), the arrow keys move, and Space checks or unchecks the selected row (or the focused check box's). Each row is a
/// list item named by its value that screen readers hear as checked or not (the toggle pattern).
/// </summary>
internal sealed class ReferenceFilterList : ListBox
{
    public ReferenceFilterList()
    {
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(GotFocusEvent, OnFocused, RoutingStrategies.Bubble);
    }

    protected override Type StyleKeyOverride => typeof(ListBox);

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => new ReferenceFilterListItem();

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey) =>
        NeedsContainer<ReferenceFilterListItem>(item, out recycleKey);

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // The focused check box's row, else the selected row (the list box takes Space to select, which does nothing here).
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None
            && ((e.Source as StyledElement)?.DataContext as ReferenceFilterValue ?? SelectedItem as ReferenceFilterValue) is { } value)
        {
            value.IsChecked = !value.IsChecked;
            e.Handled = true;
        }
    }

    /// <summary>
    /// Tab reaches the list itself, or a row, with nothing selected: a row is selected and focused, so Space has one to
    /// check and the arrow keys move from it. A row or check box focused otherwise (a click) becomes the selected row.
    /// </summary>
    private void OnFocused(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is not StyledElement source || ItemsSource is not IReadOnlyList<ReferenceFilterValue> { Count: > 0 } values)
        {
            return;
        }

        if (!ReferenceEquals(source, this))
        {
            if (source.DataContext is not ReferenceFilterValue focused)
            {
                return;
            }

            if (e.NavigationMethod != NavigationMethod.Tab)
            {
                SelectedItem = focused; // a click, an arrow key
                return;
            }

            SelectedItem ??= First(values);
            if (ReferenceEquals(SelectedItem, focused))
            {
                return;
            }
        }
        else
        {
            SelectedItem ??= First(values);
        }

        var index = SelectedIndex;
        ScrollIntoView(index);
        ContainerFromIndex(index)?.Focus(NavigationMethod.Directional);
    }

    private static ReferenceFilterValue First(IReadOnlyList<ReferenceFilterValue> values)
    {
        foreach (var value in values)
        {
            if (value.IsChecked)
            {
                return value;
            }
        }

        return values[0];
    }
}

/// <summary>A <see cref="ReferenceFilterList"/>'s row: a list item that screen readers hear as its value, checked or not.</summary>
internal sealed class ReferenceFilterListItem : ListBoxItem
{
    private ReferenceFilterValue? _value; // whose check it follows while shown
    private ReferenceFilterListItemAutomationPeer? _peer;
    private bool _attached;

    protected override Type StyleKeyOverride => typeof(ListBoxItem);

    internal ReferenceFilterValue? Value => DataContext as ReferenceFilterValue;

    protected override AutomationPeer OnCreateAutomationPeer() => _peer = new ReferenceFilterListItemAutomationPeer(this);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Follow(Value);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Follow(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        AutomationProperties.SetName(this, Value?.Display ?? string.Empty);
        if (_attached)
        {
            Follow(Value);
        }
    }

    /// <summary>Follows a value's check (unsubscribing while detached, so a closed flyout's rows aren't kept by the values).</summary>
    private void Follow(ReferenceFilterValue? value)
    {
        if (ReferenceEquals(value, _value))
        {
            return;
        }

        if (_value is not null)
        {
            _value.PropertyChanged -= OnValueChanged;
        }

        _value = value;
        if (value is not null)
        {
            value.PropertyChanged += OnValueChanged;
        }
    }

    private void OnValueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReferenceFilterValue.IsChecked) && sender is ReferenceFilterValue value && _peer is { } peer)
        {
            var state = value.IsChecked ? ToggleState.On : ToggleState.Off;
            peer.RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty, state == ToggleState.On ? ToggleState.Off : ToggleState.On, state);
        }
    }
}

/// <summary>A filter value's row for screen readers: a list item named by its value, with its check as the toggle pattern.</summary>
internal sealed class ReferenceFilterListItemAutomationPeer(ReferenceFilterListItem owner) : ListItemAutomationPeer(owner), IToggleProvider
{
    public ToggleState ToggleState => owner.Value is { IsChecked: true } ? ToggleState.On : ToggleState.Off;

    public void Toggle()
    {
        if (owner.Value is { } value)
        {
            value.IsChecked = !value.IsChecked;
        }
    }

    protected override string? GetNameCore() => owner.Value?.Display ?? base.GetNameCore();
}
