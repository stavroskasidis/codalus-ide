using Avalonia.Controls;
using Avalonia.Input;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.ViewModels;

namespace DaedalusStudio.Shell.Views;

/// <summary>The Performance Profiler page (M18).</summary>
public sealed partial class PerformanceProfilerView : LoadingView
{
    public PerformanceProfilerView()
    {
        InitializeComponent();
        SessionList.DoubleTapped += (_, _) => (DataContext as PerformanceProfilerViewModel)?.OpenSessionCommand.Execute(null);
    }

    public override void FocusPrimary() => CpuBox.Focus();
}

/// <summary>A profiling report (M18): the timeline's selection filters, the flame graph zooms, double-clicks go to source.</summary>
public sealed partial class ProfilingReportView : LoadingView
{
    public ProfilingReportView()
    {
        InitializeComponent();
        ProfilingReportViewModel? Vm() => DataContext as ProfilingReportViewModel;
        Timeline.SelectionChanged += (_, range) => _ = Vm()?.SelectRangeAsync(range.Start, range.End);
        Flame.FrameActivated += (_, frame) => _ = Vm()?.ZoomToAsync(frame);
        Flame.ZoomOutRequested += (_, _) => _ = Vm()?.ZoomOutAsync();
        FunctionList.DoubleTapped += (_, _) => _ = Vm()?.GoToSourceAsync(Vm()?.SelectedFunction);
        FunctionList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                _ = Vm()?.GoToSourceAsync(Vm()?.SelectedFunction);
                e.Handled = true;
            }
        };
        CallerList.DoubleTapped += (_, _) => Navigate(CallerList);
        CalleeList.DoubleTapped += (_, _) => Navigate(CalleeList);
        HotFileList.DoubleTapped += (_, _) =>
        {
            if (HotFileList.SelectedItem is HotFileRow row && Vm() is { } vm)
            {
                _ = vm.OpenHotFileAsync(row);
            }
        };
    }

    /// <summary>A caller or callee becomes the function shown.</summary>
    private void Navigate(ListBox list)
    {
        if (list.SelectedItem is CpuFunctionRowViewModel row && DataContext is ProfilingReportViewModel vm)
        {
            _ = vm.ShowCallerCalleeAsync(row.Function.Id);
        }
    }

    public override void FocusPrimary() => Tabs.Focus();

    /// <summary>The flame graph control (tests).</summary>
    internal FlameGraph FlameGraph => Flame;
}

/// <summary>The Diagnostic Tools window (M18).</summary>
public sealed partial class DiagnosticToolsView : UserControl, IPaneView
{
    public DiagnosticToolsView() => InitializeComponent();

    public void FocusPrimary() => RecordButton.Focus();
}

/// <summary>The .NET Counters window (M18).</summary>
public sealed partial class CountersView : UserControl, IPaneView
{
    public CountersView() => InitializeComponent();

    public void FocusPrimary() => TargetBox.Focus();
}
