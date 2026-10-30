using Avalonia.Controls;
using Avalonia.Input;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views.DataTips;

namespace Codalus.Shell.Views;

/// <summary>QuickWatch: Enter or Reevaluate evaluates the expression; F2 edits a value in place; Add Watch copies it to Watch.</summary>
public sealed partial class QuickWatchView : UserControl, IPaneView
{
    public QuickWatchView()
    {
        InitializeComponent();
        ShownTracker.Track(this); // refreshed only while shown (M56)
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
        DataContextChanged += (_, _) =>
        {
            if (DataContext is QuickWatchViewModel vm)
            {
                Rows.Attach(vm.Roots, new VariableRowActions { SetValue = vm.SetValueAsync, Reason = vm.ReadOnlyReason }, vm.Expansion);
            }
        };
    }

    public void FocusPrimary()
    {
        Input.Focus();
        Input.SelectAll();
    }
}
