using Avalonia.Controls;
using Avalonia.Input;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// The start window (M38). Keys: Alt+S searches, Down moves from the search box into the list, Enter opens the selected
/// entry (or the first match from the search box), Delete removes it from the list, Escape continues without code.
/// </summary>
public sealed partial class StartWindow : Window
{
    public StartWindow()
    {
        InitializeComponent();
        CreateIcon.Content = CommandIcons.Create("templates.newProject");
        OpenIcon.Content = CommandIcons.Create("workbench.openSolution");
        FolderIcon.Content = CommandIcons.Create("workbench.openFolder");
        CloneIcon.Content = CommandIcons.Create("git.clone");
        Opened += (_, _) => SearchBox.Focus();
        List.DoubleTapped += (_, _) => OpenSelected();
    }

    private StartWindowViewModel? Model => DataContext as StartWindowViewModel;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.S when e.KeyModifiers == KeyModifiers.Alt:
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Enter when SearchBox.IsFocused && Model?.FirstEntry is { } first:
                first.OpenCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter when List.IsKeyboardFocusWithin:
                OpenSelected();
                e.Handled = true;
                break;
            case Key.Down when SearchBox.IsFocused && Model?.FirstEntry is { } entry:
                Model.Selected = entry;
                (List.ContainerFromItem(entry) as Control)?.Focus();
                e.Handled = true;
                break;
            case Key.Delete when List.IsKeyboardFocusWithin && Model?.Selected is StartEntryRowViewModel row:
                row.RemoveCommand.Execute(null);
                e.Handled = true;
                break;
        }

        base.OnKeyDown(e);
    }

    private void OpenSelected()
    {
        if (Model?.Selected is StartEntryRowViewModel row)
        {
            row.OpenCommand.Execute(null);
        }
    }
}
