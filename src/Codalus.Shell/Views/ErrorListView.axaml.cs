using Avalonia.Controls;
using Avalonia.Input;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

public sealed partial class ErrorListView : UserControl, IPaneView
{
    public ErrorListView()
    {
        InitializeComponent();
        List.DoubleTapped += (_, _) => OpenSelected();
        List.Tapped += (_, e) =>
        {
            // A header toggles with one click.
            if (e.Source is Avalonia.StyledElement { DataContext: ErrorListGroup group } && DataContext is ErrorListViewModel vm)
            {
                var index = vm.Items.IndexOf(group);
                vm.ToggleGroup(group);
                List.SelectedIndex = index;
            }
        };
        List.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                OpenSelected();
                e.Handled = true;
            }
        };
        List.ContextRequested += OnContextRequested;
        Scope.SelectionChanged += (_, _) =>
        {
            if (DataContext is ErrorListViewModel vm)
            {
                vm.CurrentDocumentOnly = Scope.SelectedIndex == 1;
            }
        };
    }

    private void OpenSelected()
    {
        if (DataContext is not ErrorListViewModel vm)
        {
            return;
        }

        if (List.SelectedItem is ErrorListRow row)
        {
            _ = vm.OpenAsync(row);
        }
        else if (List.SelectedItem is ErrorListGroup group)
        {
            var index = List.SelectedIndex;
            vm.ToggleGroup(group);
            List.SelectedIndex = index; // the header, rebuilt
        }
    }

    /// <summary>
    /// A row's menu (M51): Go To, Copy, and while AI is on Fix with AI and Explain with AI. Built when it opens, for the row
    /// under the pointer (or the selected one, from the keyboard).
    /// </summary>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not ErrorListViewModel vm)
        {
            return;
        }

        if (e.Source is Avalonia.StyledElement { DataContext: ErrorListRow under })
        {
            List.SelectedItem = under;
        }

        if (List.SelectedItem is not ErrorListRow row)
        {
            return;
        }

        e.Handled = true;
        var menu = new ContextMenu { ItemsSource = MenuItems(vm, row) };
        menu.Open(List);
    }

    /// <summary>The items of <paramref name="row"/>'s menu (tests).</summary>
    internal List<Control> MenuItems(ErrorListViewModel vm, ErrorListRow row)
    {
        var items = new List<Control>
        {
            MenuEntry.Create(Codalus.Workbench.Localization.Localizer.Get("errorList.goTo", "Go To"), "go-to", click: () => _ = vm.OpenAsync(row)),
            MenuEntry.Create(Codalus.Workbench.Localization.Localizer.Get("errorList.copy", "Copy"), "copy", click: () => _ = TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard ? Infrastructure.SafeClipboard.TrySetTextAsync(clipboard, ErrorListViewModel.CopyText(row)) : Task.CompletedTask),
        };
        if (vm.CanUseAi())
        {
            items.Add(new Separator());
            items.Add(MenuEntry.Create(Codalus.Workbench.Localization.Localizer.Get("errorList.fixWithAi", "Fix with AI"), "sparkle", click: () => _ = vm.FixWithAi?.Invoke(row)));
            items.Add(MenuEntry.Create(Codalus.Workbench.Localization.Localizer.Get("errorList.explainWithAi", "Explain with AI"), "comment", click: () => _ = vm.ExplainWithAi?.Invoke(row)));
        }

        return items;
    }

    public void FocusPrimary() => List.Focus();
}
