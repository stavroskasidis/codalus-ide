using System.Globalization;
using DaedalusStudio.Diagnostics.Protocol;
using DaedalusStudio.Rpc;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Diagnostics;
using DaedalusStudio.Workbench.StatusBar;

namespace DaedalusStudio.Shell;

/// <summary>
/// Profiling and diagnostics (M18a, ADR 0044 and 0045): the Performance Profiler (Alt+F2) that launches the startup
/// project or attaches to a running process with CPU Usage and .NET Counters and saves the session under
/// <c>.daedalus/profiler/</c>; profiling reports; the Diagnostic Tools window, which follows the program being run or
/// debugged while it is open; the .NET Counters window. The work happens in <c>daedalus-diaghost</c>.
/// </summary>
public sealed partial class ShellHost
{
    public const string ShowDiagnosticToolsSetting = "diagnostics.showDiagnosticToolsOnDebug";
    private const string ProfilerStatusItem = "daedalus.profiler";

    private static readonly CounterProvider[] s_sessionCounters = [new(DiagnosticToolsViewModel.Runtime, CounterProviderKinds.EventCounters)];

    private ProfilingRun? _profiling;
    private bool _reportsWired;

    public DiagnosticsService Profiling { get; private set; } = null!;

    public DiagnosticToolsViewModel DiagnosticTools { get; private set; } = null!;

    public CountersViewModel CountersWindow { get; private set; } = null!;

    /// <summary>The collection in progress, or null.</summary>
    public ProfilingRun? ActiveProfiling => _profiling;

    HotLineStore Editor.IEditorServices.HotLines => Profiling.HotLines;

    private void InitializeProfiling()
    {
        Profiling = new DiagnosticsService(DiagnosticsHostLaunch.Resolve);
        Profiling.Log += (_, line) => Workbench.Log.AppendLine("[diagnostics] " + line);
        DiagnosticTools = new DiagnosticToolsViewModel(Profiling, (pid, name) => StartProfilingAsync(new ProfilerRequest(Cpu: true, Counters: false, Launch: false, pid), name));
        CountersWindow = new CountersViewModel(Profiling, (title, name, filter, patterns) => Window?.SaveFileAsync(title, name, filter, patterns) ?? Task.FromResult<string?>(null));
        Runs.RunningChanged += (_, _) => Ui.Post(FollowPrograms);
        Debugger.ProcessesChanged += (_, _) => Ui.Post(FollowPrograms);
        Debugger.StateChanged += (_, _) => Ui.Post(() =>
        {
            if (Debugger.IsActive && Workbench.Settings.GetBoolean(ShowDiagnosticToolsSetting, false) && Layout is not null && !Layout.IsVisible(DiagnosticToolsViewModel.ToolWindowId))
            {
                Layout.ShowToolWindow(DiagnosticToolsViewModel.ToolWindowId, focus: false);
                FollowPrograms();
            }
        });
    }

    /// <summary>The solution's sessions folder (the state folder's without a solution).</summary>
    public string ProfilerSessionsRoot
    {
        get
        {
            var folder = Solutions.Solution?.Path is { } path ? (Directory.Exists(path) ? path : Path.GetDirectoryName(path)) : null;
            return ProfilerSessionStore.Root(folder, Paths.State);
        }
    }

    // ------------------------------------------------------------------ the Diagnostic Tools window

    public void ShowDiagnosticTools()
    {
        Layout.ShowToolWindow(DiagnosticToolsViewModel.ToolWindowId, focus: true);
        FollowPrograms();
    }

    public void ShowCounters()
    {
        Layout.ShowToolWindow(CountersViewModel.ToolWindowId, focus: true);
        if (CountersWindow.Processes.Count == 0)
        {
            _ = CountersWindow.RefreshAsync();
        }
    }

    /// <summary>
    /// While the Diagnostic Tools window is open, it follows the newest local program: the debugged one first, else the
    /// newest one run without debugging (not one under <c>dotnet watch</c>, whose process is the watcher).
    /// </summary>
    private void FollowPrograms()
    {
        if (Layout is null || !Layout.IsVisible(DiagnosticToolsViewModel.ToolWindowId))
        {
            return;
        }

        var debugged = Debugger.IsActive ? Debugger.Processes.LastOrDefault(p => p is { Id: not null, Ended: false, Target: null }) : null;
        if (debugged is { Id: { } id })
        {
            _ = DiagnosticTools.AttachAsync(id, debugged.Name);
            return;
        }

        if (Runs.Running.LastOrDefault(p => !p.HasExited && p.HotReload is null) is { } program)
        {
            _ = DiagnosticTools.AttachAsync(program.ProcessId, program.Name);
        }
    }

    // ------------------------------------------------------------------ the Performance Profiler

    /// <summary>Opens the Performance Profiler (Alt+F2).</summary>
    public PerformanceProfilerViewModel OpenProfiler() =>
        OpenManagement("profiler", () => new PerformanceProfilerViewModel(new ProfilerHost(this)), _ => "Performance Profiler");

    /// <summary>The Performance Profiler's view of the shell.</summary>
    private sealed class ProfilerHost(ShellHost shell) : IProfilerHost
    {
        public string? StartupProjectName => shell.Runs.StartupProject is { } project ? Path.GetFileNameWithoutExtension(project) : null;

        public ProfilingRun? Active => shell._profiling;

        public Task<IReadOnlyList<DotnetProcess>> ProcessesAsync(CancellationToken cancellationToken) => shell.Profiling.ProcessesAsync(cancellationToken);

        public Task<ProfilingRun?> StartAsync(ProfilerRequest request) => shell.StartProfilingAsync(request);

        public IReadOnlyList<ProfilerSession> Sessions() => ProfilerSessionStore.List(shell.ProfilerSessionsRoot);

        public void OpenSession(ProfilerSession session) => shell.OpenProfilingReport(session);

        public void DeleteSession(ProfilerSession session)
        {
            try
            {
                ProfilerSessionStore.Delete(session);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                shell.Workbench.Notifications.Error($"{session.Name} could not be deleted: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Starts collecting: the startup project built and launched held before <c>Main</c> (so its startup is profiled
    /// too), or a running process. The trace and the counters go to a new session folder; stopping (or the process
    /// exiting) saves the session and opens its report.
    /// </summary>
    public async Task<ProfilingRun?> StartProfilingAsync(ProfilerRequest request, string? processName = null)
    {
        if (_profiling is not null)
        {
            Workbench.Notifications.Warning("A profiling session is already collecting: stop it first.");
            return null;
        }

        if (!Profiling.IsAvailable)
        {
            Workbench.Notifications.Error("The diagnostics host (daedalus-diaghost) was not found.");
            return null;
        }

        RunningProgram? program = null;
        string target;
        string? project = null;
        int processId;
        if (request.Launch)
        {
            project = Runs.StartupProject;
            if (project is null || !RequireTrustedSolution("Profiling the startup project"))
            {
                if (project is null)
                {
                    Workbench.Notifications.Warning("There is no startup project to profile.");
                }

                return null;
            }

            if (!await BuildBeforeRunAsync(project).ConfigureAwait(true))
            {
                StatusBar.ShowMessage("The build failed, so nothing was profiled.", TimeSpan.FromSeconds(6));
                return null;
            }

            try
            {
                var (plan, _, _) = await Runs.PlanAsync(project, CancellationToken.None).ConfigureAwait(true);
                var environment = new Dictionary<string, string>(plan.Environment, StringComparer.Ordinal) { [ProfilingLaunch.SuspendVariable] = "1" };
                target = Path.GetFileNameWithoutExtension(project);
                program = Runs.StartPlan(project, target, plan with { Environment = environment });
                processId = program.ProcessId;
                if (program.Channel is { } channel)
                {
                    Workbench.Output.Show(channel);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or JsonRpcRemoteException or JsonRpcConnectionLostException or IOException)
            {
                Workbench.Notifications.Error($"Could not start {Path.GetFileNameWithoutExtension(project)}: {ex.Message}");
                return null;
            }
        }
        else if (request.ProcessId is { } pid)
        {
            processId = pid;
            target = processName ?? "process";
        }
        else
        {
            return null;
        }

        var startedAt = DateTimeOffset.Now;
        var folder = ProfilerSessionStore.CreateFolder(ProfilerSessionsRoot, target, startedAt);
        TraceRecordingHandle? trace = null;
        CounterMonitor? counters = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            if (request.Cpu)
            {
                trace = await Profiling.StartTraceAsync(processId, Path.Combine(folder, "cpu.nettrace"), timeout.Token).ConfigureAwait(true);
                target = request.Launch ? target : trace.ProcessName;
            }

            if (request.Counters)
            {
                counters = await Profiling.StartCountersAsync(processId, s_sessionCounters, 1, runtimeEvents: true, timeout.Token).ConfigureAwait(true);
                target = request.Launch ? target : counters.ProcessName;
            }

            if (request.Launch)
            {
                await Profiling.ResumeAsync(processId, timeout.Token).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
            Workbench.Notifications.Error($"Profiling {target} could not start: {ex.Message}");
            program?.Stop();
            if (trace is not null)
            {
                await StopQuietlyAsync(trace).ConfigureAwait(true);
            }

            if (counters is not null)
            {
                await counters.StopAsync(CancellationToken.None).ConfigureAwait(true);
            }

            TryDeleteFolder(folder);
            return null;
        }

        List<string> tools = [];
        if (trace is not null)
        {
            tools.Add(ProfilerTools.Cpu);
        }

        if (counters is not null)
        {
            tools.Add(ProfilerTools.Counters);
        }

        var name = target;
        var run = new ProfilingRun(target, processId, tools, async r =>
        {
            var session = await FinishProfilingAsync(r, folder, name, project, startedAt, trace, counters, program).ConfigureAwait(true);
            if (_profiling == r)
            {
                _profiling = null;
                Workbench.Context.Set("profilerCollecting", false);
            }

            return session;
        });
        _profiling = run;
        Workbench.Context.Set("profilerCollecting", true);
        Workbench.StatusBar.Set(new StatusBarItem(ProfilerStatusItem, $"$(record) Profiling {target}", StatusBarAlignment.Left, 40, ContributionSource.Shell.Id)
        {
            Command = "profiler.stop",
            Tooltip = "Collecting a profile: click to stop and see the report.",
            AccessibleName = $"Profiling {target}: stop collection",
        });

        // The process ending stops the collection by itself.
        void Exited() => Ui.Post(() => _ = run.StopAsync());
        if (trace is not null)
        {
            trace.ProcessExited += (_, _) => Exited();
        }
        else if (counters is not null)
        {
            counters.Stopped += (_, _) => Exited();
        }

        return run;
    }

    private static async Task StopQuietlyAsync(TraceRecordingHandle trace)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await trace.StopAsync(timeout.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
        {
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Stops the tools, saves the session, and opens its report; null when nothing could be saved.</summary>
    private async Task<ProfilerSession?> FinishProfilingAsync(ProfilingRun run, string folder, string name, string? project, DateTimeOffset startedAt, TraceRecordingHandle? trace, CounterMonitor? counters, RunningProgram? program)
    {
        Workbench.StatusBar.Remove(ProfilerStatusItem);
        StatusBar.ShowMessage($"Stopping the profiling of {name}…", TimeSpan.FromSeconds(4));
        string? traceFile = null;
        string? countersFile = null;
        if (trace is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var stopped = await trace.StopAsync(timeout.Token).ConfigureAwait(true);
                traceFile = stopped.Bytes > 0 ? Path.GetFileName(stopped.Path) : null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException)
            {
                Workbench.Notifications.Error($"The CPU trace of {name} could not be finished: {ex.Message}");
            }
        }

        if (counters is not null)
        {
            if (counters.IsRunning)
            {
                await counters.StopAsync(CancellationToken.None).ConfigureAwait(true);
            }

            try
            {
                await File.WriteAllTextAsync(Path.Combine(folder, "counters.json"), CounterExport.ToJson(counters)).ConfigureAwait(true);
                countersFile = "counters.json";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Workbench.Notifications.Error($"The counters of {name} could not be saved: {ex.Message}");
            }
        }

        // A program launched to be profiled stops with the collection.
        if (program is { HasExited: false })
        {
            program.Stop();
        }

        if (traceFile is null && countersFile is null)
        {
            TryDeleteFolder(folder);
            return null;
        }

        var session = new ProfilerSession(
            string.Create(CultureInfo.CurrentCulture, $"{name} {startedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}"),
            startedAt,
            (DateTimeOffset.Now - startedAt).TotalSeconds,
            project is null ? ProfilerSession.ProcessTarget : ProfilerSession.LaunchTarget,
            project,
            name,
            run.ProcessId,
            run.Tools,
            traceFile,
            countersFile)
        { Folder = folder };
        try
        {
            ProfilerSessionStore.Save(session);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Workbench.Notifications.Error($"The profiling session could not be saved: {ex.Message}");
            return null;
        }

        OpenProfilingReport(session);
        return session;
    }

    /// <summary>Stops the collection in progress (the status bar item, Debug → Stop Collection).</summary>
    public Task StopProfilingAsync() => _profiling?.StopAsync() ?? Task.FromResult<ProfilerSession?>(null);

    // ------------------------------------------------------------------ reports

    /// <summary>Opens a session's report (once per session).</summary>
    public ProfilingReportViewModel OpenProfilingReport(ProfilerSession session)
    {
        if (!_reportsWired)
        {
            _reportsWired = true;
            Layout.DocumentClosed += (_, document) =>
            {
                if (document is SolutionManagementDocument { ViewModel: ProfilingReportViewModel report })
                {
                    _ = report.DisposeAsync().AsTask();
                }
            };
        }

        return OpenManagement(
            "profilerReport:" + session.Folder,
            () => new ProfilingReportViewModel(Profiling, session, OpenSourceLineAsync),
            vm => "Report: " + vm.Session.ProcessName);
    }

    /// <summary>Opens a saved session (its folder's session.json) or a <c>.nettrace</c> file someone recorded.</summary>
    public async Task OpenProfilingFileAsync(string? path = null)
    {
        path ??= Window is null ? null : await Window.PickFileAsync("Open Profiling Session", "Profiling sessions and traces", ["session.json", "*.nettrace"]).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        if (ProfilerSessionStore.Load(path) is { } session)
        {
            OpenProfilingReport(session);
            return;
        }

        if (path.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            OpenProfilingReport(new ProfilerSession(Path.GetFileName(path), File.GetLastWriteTime(path), 0, ProfilerSession.ProcessTarget, null, name, 0, [ProfilerTools.Cpu], Path.GetFileName(path), null)
            {
                Folder = Path.GetDirectoryName(Path.GetFullPath(path))!,
            });
            return;
        }

        Workbench.Notifications.Warning($"{Path.GetFileName(path)} is not a profiling session or a .nettrace file.");
    }

    /// <summary>On exit: a collection in progress is abandoned (its folder keeps what was written).</summary>
    private async Task DisposeProfilingAsync()
    {
        DiagnosticTools.Dispose();
        CountersWindow.Dispose();
        await Profiling.DisposeAsync().ConfigureAwait(false);
    }
}
