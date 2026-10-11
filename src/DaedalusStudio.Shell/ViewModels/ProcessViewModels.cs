using System.Collections.ObjectModel;
using System.Globalization;
using DaedalusStudio.Debugging;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Debugging;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>A row of the Processes window.</summary>
public sealed record ProcessRow(int Id, string Name, string Path, string Title, string State, string Kind)
{
    public string IdText => Id.ToString(CultureInfo.InvariantCulture);

    public string AccessibleName => $"{Name}, process {IdText}, {State}, {Kind}";
}

/// <summary>
/// The Processes window (M14b): the processes being debugged with their state, and Detach All, Terminate All and
/// Attach to Process. A session debugs one .NET process (with a browser page in a compound session).
/// </summary>
public sealed class ProcessesViewModel : ObservableObject
{
    public const string ToolWindowId = "debug.processes";

    private readonly DebugService _debugger;
    private readonly Action<string> _run;

    public ProcessesViewModel(DebugService debugger, Action<string> run)
    {
        _debugger = debugger;
        _run = run;
        debugger.StateChanged += (_, _) => Ui.Post(Refresh);
        debugger.ProcessesChanged += (_, _) => Ui.Post(Refresh);
    }

    public ObservableCollection<ProcessRow> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>
    /// Whether Detach All applies: every program was attached to or launched in the terminal (a program launched with
    /// the Debug Console has netcoredbg's console, M14c).
    /// </summary>
    public bool CanDetach => !IsEmpty && _debugger.CanDetach;

    /// <summary>Runs one of the window's commands (attach, detach all, terminate all).</summary>
    public void Run(string commandId) => _run(commandId);

    public void Refresh()
    {
        Rows.Clear();
        if (_debugger.State != DebugState.Idle)
        {
            var state = _debugger.State == DebugState.Stopped ? "Break" : "Running";
            foreach (var process in _debugger.Processes)
            {
                if (process.Id is not { } pid)
                {
                    continue;
                }

                var kind = process.Kind switch
                {
                    DebugProcess.Terminal => "Launched in the terminal",
                    var other => other,
                };
                if (process.Target is { } target)
                {
                    // On another machine: what the session knows, not this machine's process of that id.
                    Rows.Add(new ProcessRow(pid, process.Name, target, string.Empty, process.Ended ? "Exited" : state, kind));
                }
                else
                {
                    var row = Describe(pid, process.Ended ? "Exited" : state, kind);
                    Rows.Add(process.Ended ? row with { Name = process.Name } : row);
                }
            }
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanDetach));
    }

    /// <summary>The process's name, path and window title, as far as the OS tells them.</summary>
    public static ProcessRow Describe(int pid, string state, string kind)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            string path;
            try
            {
                path = process.MainModule?.FileName ?? string.Empty;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                path = string.Empty;
            }

            return new ProcessRow(pid, process.ProcessName, path, process.MainWindowTitle, state, kind);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return new ProcessRow(pid, "(exited)", string.Empty, string.Empty, state, kind);
        }
    }
}

/// <summary>A thread in the Debug Location toolbar.</summary>
public sealed record DebugLocationThread(int Id, string Name)
{
    public override string ToString() => $"[{Id.ToString(CultureInfo.InvariantCulture)}] {(Name.Length == 0 ? "<No Name>" : Name)}";
}

/// <summary>A frame in the Debug Location toolbar.</summary>
public sealed record DebugLocationFrame(StackFrame Frame)
{
    public override string ToString() => Frame.Name;
}

/// <summary>
/// The Debug Location toolbar (M14b), shown while paused: the process, the thread and the stack frame; picking a
/// thread or a frame selects it as the Threads and Call Stack windows do.
/// </summary>
public sealed class DebugLocationViewModel : ObservableObject
{
    private readonly DebugService _debugger;
    private string _process = string.Empty;
    private DebugLocationThread? _thread;
    private DebugLocationFrame? _frame;
    private bool _syncing;

    public DebugLocationViewModel(DebugService debugger)
    {
        _debugger = debugger;
        debugger.StateChanged += (_, _) => Ui.Post(Refresh);
        debugger.FrameChanged += (_, _) => Ui.Post(Refresh);
        debugger.Stopped += (_, _) => Ui.Post(Refresh);
    }

    public string Process
    {
        get => _process;
        private set => SetField(ref _process, value);
    }

    public ObservableCollection<DebugLocationThread> Threads { get; } = [];

    public ObservableCollection<DebugLocationFrame> Frames { get; } = [];

    public DebugLocationThread? Thread
    {
        get => _thread;
        set
        {
            if (SetField(ref _thread, value) && !_syncing && value is not null && value.Id != _debugger.SelectedThreadId)
            {
                _ = _debugger.SelectThreadAsync(value.Id);
            }
        }
    }

    public DebugLocationFrame? Frame
    {
        get => _frame;
        set
        {
            if (SetField(ref _frame, value) && !_syncing && value is not null && value.Frame != _debugger.SelectedFrame)
            {
                _debugger.SelectFrame(value.Frame);
            }
        }
    }

    public void Refresh()
    {
        _syncing = true;
        try
        {
            var stopped = _debugger.State == DebugState.Stopped;
            Process = _debugger.ProcessId is { } pid && _debugger.State != DebugState.Idle
                ? $"[{pid.ToString(CultureInfo.InvariantCulture)}] {ProcessesViewModel.Describe(pid, string.Empty, string.Empty).Name}"
                : string.Empty;
            Replace(Threads, stopped ? _debugger.Threads.Select(t => new DebugLocationThread(t.Id, t.Name)) : []);
            Thread = Threads.FirstOrDefault(t => t.Id == _debugger.SelectedThreadId);
            Replace(Frames, stopped ? _debugger.Frames.Select(f => new DebugLocationFrame(f)) : []);
            Frame = Frames.FirstOrDefault(f => f.Frame == _debugger.SelectedFrame);
        }
        finally
        {
            _syncing = false;
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        var list = items.ToList();
        if (target.SequenceEqual(list))
        {
            return;
        }

        target.Clear();
        foreach (var item in list)
        {
            target.Add(item);
        }
    }
}
