using System.Collections.ObjectModel;
using System.Globalization;
using Codalus.Debugging;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Debugging;

namespace Codalus.Shell.ViewModels;

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
/// <remarks>
/// Each process is described once for the session (its name and path, from the OS), off the UI thread (M56): every step
/// described it five times on the UI thread, for this window and the Debug Location toolbar, shown or not. The rows are
/// built only while the window is shown, and once when it's shown; each time, the window titles (which change) are read
/// again, off the UI thread.
/// </remarks>
public sealed class ProcessesViewModel : ObservableObject, IShownAware
{
    public const string ToolWindowId = "debug.processes";

    private readonly DebugService _debugger;
    private readonly Action<string> _run;
    private readonly ShownRefresh _refresh;
    private readonly Dictionary<int, ProcessRow> _described = [];
    private readonly HashSet<int> _describing = [];
    private readonly Dictionary<int, string> _titles = [];
    private int _session;
    private int _titlesRead;

    public ProcessesViewModel(DebugService debugger, Action<string> run)
    {
        _debugger = debugger;
        _run = run;
        _refresh = new ShownRefresh(Refresh);
        debugger.StateChanged += (_, _) => Ui.Post(OnChanged);
        debugger.ProcessesChanged += (_, _) => Ui.Post(OnChanged);
    }

    public ResettableCollection<ProcessRow> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>
    /// Whether Detach All applies: every program was attached to or launched in the terminal (a program launched with
    /// the Debug Console has netcoredbg's console, M14c).
    /// </summary>
    public bool CanDetach => !IsEmpty && _debugger.CanDetach;

    /// <summary>Describes a process from the OS (tests count it); run off the UI thread, once per process for the session.</summary>
    internal Func<int, ProcessRow> Describer { get; set; } = pid => Describe(pid, string.Empty, string.Empty);

    /// <summary>Reads a process's main window title (tests count it); run off the UI thread each time the shown window refreshes.</summary>
    internal Func<int, string> TitleReader { get; set; } = pid => OperatingSystem.IsWindows() ? TitleOf(pid) : string.Empty;

    /// <summary>A process was described: the Debug Location toolbar shows its name.</summary>
    public event EventHandler? Described;

    void IShownAware.SetViewShown(object view, bool shown) => _refresh.SetShown(view, shown);

    /// <summary>Runs one of the window's commands (attach, detach all, terminate all).</summary>
    public void Run(string commandId) => _run(commandId);

    private void OnChanged()
    {
        if (_debugger.State == DebugState.Idle)
        {
            // The session ended: the next one's processes are described again, and a description still on its way is dropped.
            _session++;
            _described.Clear();
            _describing.Clear();
            _titles.Clear();
        }

        _refresh.Request();
    }

    public void Refresh()
    {
        _refresh.Fresh();
        Build();
        ReadTitles();
    }

    private void Build()
    {
        var rows = new List<ProcessRow>();
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
                    DebugProcess.ConsoleWindow => "Launched in a console window",
                    var other => other,
                };
                if (process.Target is { } target)
                {
                    // On another machine: what the session knows, not this machine's process of that id.
                    rows.Add(new ProcessRow(pid, process.Name, target, string.Empty, process.Ended ? "Exited" : state, kind));
                }
                else if (Known(pid) is { } described)
                {
                    rows.Add(described with { Name = process.Ended ? process.Name : described.Name, Title = _titles.GetValueOrDefault(pid, described.Title), State = process.Ended ? "Exited" : state, Kind = kind });
                }
                else
                {
                    // Until it's described: what the session calls it.
                    rows.Add(new ProcessRow(pid, process.Name, string.Empty, string.Empty, process.Ended ? "Exited" : state, kind));
                }
            }
        }

        Rows.Reset(rows);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanDetach));
    }

    /// <summary>
    /// The described processes' window titles, read again off the UI thread (a title changes, and a program's window may
    /// not exist yet when it's first described): the rows follow when one changed.
    /// </summary>
    private void ReadTitles()
    {
        int[] pids = [.. Rows.Where(r => _described.ContainsKey(r.Id) && r.State != "Exited").Select(r => r.Id)];
        if (pids.Length == 0)
        {
            return;
        }

        var (read, session, asked) = (TitleReader, _session, ++_titlesRead);
        _ = Task.Run(() => pids.Select(pid => (Pid: pid, Title: read(pid))).ToList()).ContinueWith(task =>
        {
            var titles = task.IsCompletedSuccessfully ? task.Result : null;
            _ = task.Exception; // observed: the titles stay, and are read again at the next refresh
            Ui.Post(() =>
            {
                if (titles is null || session != _session || asked != _titlesRead)
                {
                    return;
                }

                var changed = false;
                foreach (var (pid, title) in titles)
                {
                    changed |= _titles.GetValueOrDefault(pid) != title;
                    _titles[pid] = title;
                }

                if (changed)
                {
                    Build();
                }
            });
        }, TaskScheduler.Default);
    }

    /// <summary>The process's name: the OS's once it's described, what the session calls it until then.</summary>
    public string NameOf(int pid) =>
        Known(pid)?.Name ?? _debugger.Processes.FirstOrDefault(p => p.Id == pid)?.Name ?? string.Empty;

    /// <summary>The process's description, or null while it's being made (it's asked for then).</summary>
    private ProcessRow? Known(int pid)
    {
        if (_described.TryGetValue(pid, out var row))
        {
            return row;
        }

        if (_debugger.State != DebugState.Idle && _describing.Add(pid))
        {
            var session = _session;
            var describe = Describer;
            _ = Task.Run(() => describe(pid)).ContinueWith(task =>
            {
                var described = task.IsCompletedSuccessfully ? task.Result : null;
                _ = task.Exception; // observed: a description that failed is asked for again at the next refresh
                Ui.Post(() =>
                {
                    if (session != _session)
                    {
                        return;
                    }

                    _describing.Remove(pid);
                    if (described is null)
                    {
                        return;
                    }

                    _described[pid] = described;
                    _refresh.Request();
                    Described?.Invoke(this, EventArgs.Empty);
                });
            }, TaskScheduler.Default);
        }

        return null;
    }

    /// <summary>The process's main window title (Windows: other systems have none to tell).</summary>
    public static string TitleOf(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.MainWindowTitle;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return string.Empty;
        }
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
    private readonly ProcessesViewModel _processes;
    private string _process = string.Empty;
    private DebugLocationThread? _thread;
    private DebugLocationFrame? _frame;
    private bool _syncing;

    /// <param name="processes">The Processes window's, which describes each process once (M56).</param>
    public DebugLocationViewModel(DebugService debugger, ProcessesViewModel processes)
    {
        _debugger = debugger;
        _processes = processes;
        debugger.StateChanged += (_, _) => Ui.Post(Refresh);
        debugger.FrameChanged += (_, _) => Ui.Post(Refresh);
        debugger.Stopped += (_, _) => Ui.Post(Refresh);
        processes.Described += (_, _) => Refresh();
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
                ? $"[{pid.ToString(CultureInfo.InvariantCulture)}] {_processes.NameOf(pid)}"
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
