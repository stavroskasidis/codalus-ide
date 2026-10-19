using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>The Performance Profiler page (M18).</summary>
public sealed partial class PerformanceProfilerView : LoadingView
{
    public PerformanceProfilerView()
    {
        InitializeComponent();
        SessionList.DoubleTapped += (_, _) => (DataContext as PerformanceProfilerViewModel)?.OpenSessionCommand.Execute(null);
    }

    public override void FocusPrimary() => ToolList.Focus();
}

/// <summary>Two profiling sessions compared (M18b).</summary>
public sealed partial class ProfilingComparisonView : LoadingView
{
    public ProfilingComparisonView() => InitializeComponent();

    public override void FocusPrimary() => FunctionList.Focus();
}

/// <summary>A profiling report (M18): the timeline's selection filters, the flame graph zooms, double-clicks go to source.</summary>
public sealed partial class ProfilingReportView : LoadingView
{
    /// <summary>Semibold text for roots and starvation (a compiled-binding converter, AOT-safe).</summary>
    public static readonly IValueConverter BoldWhen = new FuncValueConverter<bool, FontWeight>(bold => bold ? FontWeight.SemiBold : FontWeight.Normal);

    public ProfilingReportView()
    {
        InitializeComponent();
        ProfilingReportViewModel? Vm() => DataContext as ProfilingReportViewModel;
        void OpenException()
        {
            if (ExceptionGroupList.SelectedItem is ExceptionGroupRow row && Vm() is { } vm)
            {
                _ = vm.OpenExceptionAsync(row);
            }
        }

        ExceptionGroupList.DoubleTapped += (_, _) => OpenException();
        ExceptionGroupList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                OpenException();
                e.Handled = true;
            }
        };
        EventCountList.DoubleTapped += (_, _) =>
        {
            if (EventCountList.SelectedItem is EventCountRow row && Vm() is { } vm)
            {
                _ = vm.FilterByAsync(row);
            }
        };
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

/// <summary>The Benchmarks window (M18b).</summary>
public sealed partial class BenchmarksView : UserControl, IPaneView
{
    public BenchmarksView()
    {
        InitializeComponent();
        BenchmarkList.DoubleTapped += (_, _) =>
        {
            if (BenchmarkList.SelectedItem is BenchmarkRowViewModel row && DataContext is BenchmarksViewModel vm)
            {
                _ = vm.OpenAsync(row);
            }
        };
    }

    public void FocusPrimary() => RunAllButton.Focus();
}

/// <summary>The .NET Counters window (M18).</summary>
public sealed partial class CountersView : UserControl, IPaneView
{
    public CountersView()
    {
        InitializeComponent();

        // One box for the id, the name or a running process (M34h): it lists the processes when it opens with none yet.
        TargetBox.DropDownOpened += (_, _) => _ = (DataContext as CountersViewModel)?.EnsureProcessesAsync();
    }

    public void FocusPrimary() => TargetBox.Focus();
}
