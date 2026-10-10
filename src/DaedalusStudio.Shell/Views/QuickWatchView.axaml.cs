using Avalonia.Controls;
using Avalonia.Input;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>QuickWatch: Enter or Reevaluate evaluates the expression; F2 edits a value; Add Watch copies it to Watch.</summary>
public sealed partial class QuickWatchView : UserControl, IPaneView
{
    public QuickWatchView()
    {
        InitializeComponent();
        Input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is QuickWatchViewModel vm)
            {
                _ = vm.EvaluateAsync();
                e.Handled = true;
            }
        };
        Reevaluate.Click += (_, _) => _ = (DataContext as QuickWatchViewModel)?.EvaluateAsync();
        AddWatch.Click += (_, _) => _ = (DataContext as QuickWatchViewModel)?.AddWatchAsync();
        VariableTreeSupport.Attach(
            Tree,
            (node, value) => DataContext is QuickWatchViewModel vm ? vm.SetValueAsync(node, value) : Task.FromResult<string?>(null),
            node => !node.IsError && (node.EvaluateName is not null || node.ParentReference is not null));
    }

    public void FocusPrimary()
    {
        Input.Focus();
        Input.SelectAll();
    }
}
