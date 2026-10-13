using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>The standard toolbar (M29): see <see cref="ViewModels.ToolbarViewModel"/>.</summary>
public sealed partial class StandardToolbar : UserControl
{
    private readonly List<Control> _builtIn;
    private ToolbarViewModel? _model;

    public StandardToolbar()
    {
        InitializeComponent();
        _builtIn = [.. Panel.Children];
        DataContextChanged += (_, _) =>
        {
            if (_model is not null)
            {
                _model.CustomizationChanged -= OnCustomizationChanged;
            }

            _model = DataContext as ToolbarViewModel;
            if (_model is not null)
            {
                _model.CustomizationChanged += OnCustomizationChanged;
            }

            Rebuild();
        };
    }

    private void OnCustomizationChanged(object? sender, EventArgs e) => Rebuild();

    /// <summary>
    /// The built-in buttons but the ones the user removed, then the commands they added and their own toolbars (M23b,
    /// Tools → Customize), each group after a separator.
    /// </summary>
    private void Rebuild()
    {
        var removed = _model?.Removed ?? new HashSet<string>();
        Panel.Children.Clear();
        foreach (var control in _builtIn)
        {
            if (control is not Button { Tag: string command } || !removed.Contains(command))
            {
                Panel.Children.Add(control);
            }
        }

        foreach (var (toolbar, items) in _model?.Extra ?? [])
        {
            if (items.Count == 0)
            {
                continue;
            }

            var separator = new Border { Classes = { "toolbarSeparator" } };
            if (toolbar is not null)
            {
                ToolTip.SetTip(separator, toolbar);
            }

            Panel.Children.Add(separator);
            foreach (var item in items)
            {
                var button = new Button
                {
                    Classes = { "toolbar" },
                    Command = item.Run,
                    Tag = item.Command,
                    Content = CommandIcons.Create(item.Command) ?? (object)new TextBlock { Text = item.Title, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                };
                AutomationProperties.SetName(button, item.Title);
                ToolTip.SetTip(button, item.Title);
                Panel.Children.Add(button);
            }
        }

        UpdateTips();
    }

    /// <summary>
    /// Tooltips name the button and its shortcut on this OS (M34a, D25): each button's <c>Tag</c> is its command, whose
    /// key binding the view model reports (Undo and Redo use the editor's own keys).
    /// </summary>
    private void UpdateTips()
    {
        if (DataContext is not ToolbarViewModel { KeyLabel: { } keyLabel })
        {
            return;
        }

        foreach (var button in this.GetLogicalDescendants().OfType<Button>())
        {
            if (button.Tag is string command && AutomationProperties.GetName(button) is { Length: > 0 } name)
            {
                ToolTip.SetTip(button, keyLabel(command) is { } keys ? $"{name} ({keys})" : name);
            }
        }
    }
}
