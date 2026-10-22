using System.Globalization;
using Codalus.Debugging;
using Codalus.LanguageServices.Protocol;
using Codalus.Platform;
using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Build;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.FileSystem;
using Codalus.Workbench.Output;
using Codalus.Workbench.StatusBar;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>Debugging (ADR 0007): sessions on netcoredbg, breakpoints, stepping and the debugger's tool windows.</summary>
public sealed partial class ShellHost
{
    public const string JustMyCodeSetting = "debug.justMyCode";

    /// <summary>Step over properties and operators (M14b).</summary>
    public const string StepFilteringSetting = "debug.stepOverPropertiesAndOperators";

    private const string DebugStatusItem = "codalus.debug";
    private bool _debugWindowsShown;
    private string? _pendingBrowserUrl;

    public DebugService Debugger { get; private set; } = null!;

    public VariablesViewModel Locals { get; private set; } = null!;

    public VariablesViewModel Autos { get; private set; } = null!;

    public WatchViewModel Watch { get; private set; } = null!;

    public CallStackViewModel CallStack { get; private set; } = null!;

    public DebugConsoleViewModel DebugConsole { get; private set; } = null!;

    public BreakpointsViewModel BreakpointList { get; private set; } = null!;

    DebugService Editor.IEditorServices.Debugger => Debugger;

    public OutputChannel DebugLog => Workbench.Output.GetOrCreate("codalus.debug", "Debug");

    private void InitializeDebugging()
    {
        Debugger = new DebugService(DebugAdapterLocator.Resolve) { JustMyCode = Workbench.Settings.GetBoolean(JustMyCodeSetting, true) };
        // [DebuggerDisplay] and [DebuggerBrowsable] from the companion host (M14b); netcoredbg doesn't apply them.
        Debugger.Displays = new DebuggerDisplays(async (type, modules, cancellationToken) =>
            await Companion.TypeDisplayAsync(type, modules, cancellationToken).ConfigureAwait(false));
        // The solution's own code (D44): a module named as one of its projects, or built under one's folder.
        Debugger.IsUserModule = path => path is not null && Solutions.Projects.Values.Any(p =>
            string.Equals(p.Name, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)
            || (Path.GetDirectoryName(p.Path) is { Length: > 0 } folder && path.StartsWith(folder + Path.DirectorySeparatorChar, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)));
        Locals = new VariablesViewModel(Debugger, autos: false, LineText);
        Autos = new VariablesViewModel(Debugger, autos: true, LineText);
        Watch = new WatchViewModel(Debugger);
        CallStack = new CallStackViewModel(Debugger, (path, line) => OpenFileAsync(path, preview: false, new Position(Math.Max(0, line - 1), 0)));
        DebugConsole = new DebugConsoleViewModel(Debugger, DebugLog, EditorFont);
        BreakpointList = new BreakpointsViewModel(Debugger, (path, line) => OpenFileAsync(path, preview: false, new Position(Math.Max(0, line - 1), 0)));
        VariableActions.AskValue = async node =>
        {
            var result = await Dialogs.ShowAsync(new DialogRequest($"Set {node.Name}", "New value (a C# expression):", ["Set", "Cancel"]) { Input = node.Value }).ConfigureAwait(true);
            return result.Button == "Set" ? result.Input : null;
        };
        VariableActions.AddWatch = expression => AddWatchAsync(expression);
        VariableActions.QuickWatch = expression => ShowQuickWatchAsync(expression);
        VariableActions.Visualize = VisualizeAsync;
        VariableActions.PluginVisualizers = type => [.. Plugins.DebugVisualizers.For(type).Select(v => (v.Id, v.Title))];
        VariableActions.VisualizeWithPlugin = VisualizeWithPluginAsync;
        VariableActions.CopyText = async text =>
        {
            if (Window?.Clipboard is { } clipboard)
            {
                await Codalus.Shell.Infrastructure.SafeClipboard.TrySetTextAsync(clipboard, text).ConfigureAwait(true);
            }
        };

        Debugger.StateChanged += (_, _) => Ui.Post(OnDebugStateChanged);
        Debugger.Stopped += (_, stop) => Ui.Post(() => _ = OnDebugStoppedAsync(stop));
        Debugger.Output += (_, output) => OnDebugOutput(output);
        Debugger.Ended += (_, code) => Ui.Post(() => StatusBar.ShowMessage(
            code is { } c ? $"The program exited with code {c.ToString(CultureInfo.InvariantCulture)}." : "Debugging stopped.", TimeSpan.FromSeconds(6)));
        Debugger.Breakpoints.Changed += (_, _) => Ui.Post(() => Workbench.Context.Set("hasBreakpoints", Debugger.Breakpoints.All.Count + Debugger.Breakpoints.Functions.Count > 0));
        Watch.Changed += (_, _) => SaveEditorSession();
        Debugger.ProcessesChanged += (_, _) => Ui.Post(() => Workbench.Context.Set("debugCanDetach", Debugger.CanDetach));
        InitializeDebugDepth();
        InitializeLiveTree();
        InitializeDebugSessions();
        OnDebugStateChanged();
    }

    private string? LineText(string path, int line)
    {
        if (Editors.FirstOrDefault(e => SolutionPaths.Comparer.Equals(e.Model.Path, path)) is { } editor)
        {
            var text = editor.Model.Text;
            return line >= 1 && line <= text.LineCount ? text.GetText(text.GetLineByNumber(line)) : null;
        }

        return null;
    }

    /// <summary>The debugged program's stdout since its last line break: the debugger sends output in any chunks.</summary>
    private readonly System.Text.StringBuilder _stdoutLine = new();

    private void OnDebugOutput(DebugOutput output)
    {
        // The program's standard error shows in the error color (M34h).
        if (output.Category == "stderr")
        {
            DebugLog.AppendError(output.Text);
        }
        else
        {
            DebugLog.Append(output.Text);
        }

        if (output.Category != "stdout")
        {
            return;
        }

        // Markers are looked for in whole lines: "Now listening on:" can arrive split across two output events.
        List<string> lines = [];
        lock (_stdoutLine)
        {
            _stdoutLine.Append(output.Text);
            var text = _stdoutLine.ToString();
            var end = text.LastIndexOf('\n');
            if (end >= 0)
            {
                lines.AddRange(text[..end].Split('\n'));
                _stdoutLine.Remove(0, end + 1);
            }
        }

        foreach (var line in lines)
        {
            OnDebuggedLine(output with { Text = line });
        }
    }

    private void OnDebuggedLine(DebugOutput output)
    {
        OnServerOutput(output);
        if (output.Text.Contains("Now listening on:", StringComparison.Ordinal) || output.Text.Contains("Application started.", StringComparison.Ordinal))
        {
            // One page per server that starts listening (several with multiple startup projects, M14c).
            var url = Interlocked.Exchange(ref _pendingBrowserUrl, null);
            if (url is null)
            {
                Ui.Post(() =>
                {
                    if (_pendingBrowserUrls.TryDequeue(out var next) && Uri.TryCreate(next, UriKind.Absolute, out var nextUri))
                    {
                        OpenExternalUrl(nextUri);
                    }
                });
            }
            else if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                OpenExternalUrl(uri);
            }
        }
    }

    /// <summary>
    /// A server whose output the Debug Console doesn't show (a console window of its own), or that never logs "Now
    /// listening on", still gets its browser: once the launch profile's address accepts connections.
    /// </summary>
    private void OpenBrowserLater()
    {
        if (Volatile.Read(ref _pendingBrowserUrl) is not { } pending || !Uri.TryCreate(pending, UriKind.Absolute, out var address))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            if (await ServerProbe.WaitUntilListeningAsync(address, TimeSpan.FromMinutes(2), () => Debugger.State != DebugState.Idle && Volatile.Read(ref _pendingBrowserUrl) is not null, CancellationToken.None).ConfigureAwait(false)
                && Interlocked.Exchange(ref _pendingBrowserUrl, null) is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                Ui.Post(() => OpenExternalUrl(uri));
            }
        });
    }

    // ------------------------------------------------------------------ start and stop

    /// <summary>F5: continues a stopped program, or builds the startup project and starts it under the debugger.</summary>
    public async Task StartDebuggingAsync(string? argument = null)
    {
        if (Debugger.State == DebugState.Stopped)
        {
            await Debugger.ContinueAsync().ConfigureAwait(true);
            return;
        }

        if (Debugger.IsActive)
        {
            return;
        }

        if (argument is null && await StartFolderLaunchAsync(debug: true).ConfigureAwait(true))
        {
            return; // a folder's launch configuration (ADR 0033)
        }

        if (argument is null && Runs.MultipleStartup)
        {
            await StartStartupProjectsAsync(debug: true).ConfigureAwait(true);
            return;
        }

        var project = argument is not null ? Path.GetFullPath(argument) : Runs.StartupProject;
        if (project is null && ActiveEditor?.Model.Path is { } htmlFile && IsHtmlFile(htmlFile) && File.Exists(htmlFile))
        {
            // Nothing to build: a page debugs in the browser (ADR 0027).
            await DebugWebPageAsync(htmlFile).ConfigureAwait(true);
            return;
        }

        if (project is null)
        {
            Workbench.Notifications.Warning(Solutions.IsOpen
                ? "There is no startup project: the solution has no executable project. Set one with 'Set as Startup Project'."
                : "Open a solution to debug it.");
            return;
        }

        if (Remote is null && IsMobileProject(project))
        {
            // A .NET MAUI or mobile app (M20a): it runs on its device; the Mono debugger comes later (TODO(issue): M20b).
            await StartOnDeviceAsync(project, debug: true).ConfigureAwait(true);
            return;
        }

        if (WarnIfWindowsOnly(project))
        {
            return; // a WinForms, WPF, WinUI or .NET Framework app off Windows (M40a, M44, M43a)
        }

        if (await TryStartPackagedAppAsync(project, debug: true).ConfigureAwait(true))
        {
            return; // a packaged WinUI 3 app (M44): started by Windows, then netcoredbg attaches
        }

        if (!await BuildBeforeRunAsync(project).ConfigureAwait(true))
        {
            if (Builds.LastBuild is { Result: { Canceled: false, Errors: > 0 } })
            {
                StatusBar.ShowMessage(Localizer.Get("msg.debug.theBuildFailedSoDebuggingDid", "The build failed, so debugging did not start."), TimeSpan.FromSeconds(6));
            }

            return;
        }

        DevCertificateCheck = OfferDevCertificateAsync(project);
        try
        {
            var (info, profiles, error) = await Runs.GetProfilesAsync(project, CancellationToken.None).ConfigureAwait(true);
            var profile = Runs.ResolveProfile(project, profiles);
            if (Remote is { } remote)
            {
                // A remote window (M21): netcoredbg runs there, through the server.
                DebugLog.Clear();
                await StartRemoteAsync(remote, project, info, profile, debug: true).ConfigureAwait(true);
                return;
            }

            if (IsAspireHost(project))
            {
                // An Aspire AppHost debugs its projects in its session (ADR 0043).
                await StartAspireAsync(project, debug: true).ConfigureAwait(true);
                return;
            }

            if (IsContainerProfile(profile))
            {
                // The "Container (Dockerfile)" profile (ADR 0042).
                await StartInContainerAsync(project, info, profile!, debug: true).ConfigureAwait(true);
                return;
            }

            var plan = await Runs.LaunchPlanAsync(info, profile, CancellationToken.None).ConfigureAwait(true);
            DebugLog.Clear();
            if (error is not null)
            {
                DebugLog.AppendLine("Ignoring the launch settings: " + error);
            }

            if (plan.Wsl is null && LaunchPlanner.BrowserDebugTarget(info, profile) is { } target)
            {
                // A Blazor WebAssembly app runs in the browser: debug it there (ADR 0026).
                Layout.ShowToolWindow(DebugConsoleViewModel.ToolWindowId, focus: false);
                await StartBrowserDebuggingAsync(project, target).ConfigureAwait(true);
                return;
            }

            DebugLog.AppendLine($"> {plan.FileName} {plan.Arguments}".TrimEnd());
            foreach (var note in plan.Notes)
            {
                DebugLog.AppendLine(note);
            }

            Debugger.JustMyCode = Workbench.Settings.GetBoolean(JustMyCodeSetting, true);
            Debugger.StepFiltering = Workbench.Settings.GetBoolean(StepFilteringSetting, true);
            Layout.ShowToolWindow(DebugConsoleViewModel.ToolWindowId, focus: false);
            if (plan.Wsl is null && LaunchPlanner.PageDebugTarget(profile, Workbench.Settings.GetBoolean(JavaScriptDebugSetting, true)) is { } page)
            {
                // The server and its page in one session (ADR 0027).
                await StartCompoundDebuggingAsync(project, plan, page).ConfigureAwait(true);
                return;
            }

            var name = Path.GetFileNameWithoutExtension(project);
            var launch = plan.Wsl is not null
                ? await WslLaunchAsync(name, plan).ConfigureAwait(true)
                : new DebugLaunch(name, plan) { Console = profile?.CommandName == LaunchProfile.RoslynComponent ? DebugConsoleKind.Internal : ConsoleFor(info) };
            if (launch is null)
            {
                return;
            }

            _pendingBrowserUrl = plan.BrowserUrl;
            await Debugger.LaunchAsync([launch], CancellationToken.None).ConfigureAwait(true);
            OpenBrowserLater();
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException or OperationCanceledException or IOException)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.debug.couldNotStartDebugging", "Could not start debugging {0}: {1}", Path.GetFileNameWithoutExtension(project), ex.Message));
        }
    }

    /// <summary>Debug → Attach to Process: the .NET processes first.</summary>
    public async Task AttachToProcessAsync(string? argument = null)
    {
        if (Debugger.IsActive)
        {
            StatusBar.ShowMessage(Localizer.Get("msg.debug.aDebugSessionIsAlreadyRunning", "A debug session is already running."), TimeSpan.FromSeconds(4));
            return;
        }

        int processId;
        if (argument is not null && int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            processId = id;
        }
        else
        {
            var processes = await Task.Run(ProcessList.Get).ConfigureAwait(true);
            var items = processes.Select(p => new QuickPickItem($"{p.Name} ({p.Id.ToString(CultureInfo.InvariantCulture)})")
            {
                Tag = p.Id,
                Detail = p.Runtime is { } runtime ? runtime + (p.Title is null ? string.Empty : " · " + p.Title) : p.Title,
            }).ToList();
            if (await Palette.ShowAsync(items, "Attach to a process (.NET processes are listed first)").ConfigureAwait(true) is not { Tag: int picked })
            {
                return;
            }

            processId = picked;
        }

        try
        {
            DebugLog.Clear();
            DebugLog.AppendLine($"Attaching to process {processId.ToString(CultureInfo.InvariantCulture)}…");
            Layout.ShowToolWindow(DebugConsoleViewModel.ToolWindowId, focus: false);
            await Debugger.AttachAsync(processId, CancellationToken.None).ConfigureAwait(true);
            RememberAttached(processId);
        }
        catch (InvalidOperationException ex)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.debug.couldNotAttachToProcess", "Could not attach to process {0}: {1}", processId.ToString(CultureInfo.InvariantCulture), ex.Message));
        }
    }

    public async Task RestartDebuggingAsync()
    {
        if (!Debugger.IsActive || Debugger.IsAttached)
        {
            return;
        }

        await Debugger.StopAsync().ConfigureAwait(true);
        await StartDebuggingAsync().ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ breakpoints

    /// <summary>F9: toggles a breakpoint on the caret's line of the active editor.</summary>
    public void ToggleBreakpoint()
    {
        if (ActiveEditor is { Controller: { } controller } editor && !editor.Model.IsReadOnly)
        {
            Debugger.Breakpoints.Toggle(editor.Model.Path, controller.Editor.TextArea.Caret.Line);
        }
    }

    /// <summary>Ctrl+F9: enables or disables the breakpoint on the caret's line.</summary>
    public void ToggleBreakpointEnabled()
    {
        if (ActiveEditor is { Controller: { } controller } editor
            && Debugger.Breakpoints.At(editor.Model.Path, controller.Editor.TextArea.Caret.Line) is { } breakpoint)
        {
            Debugger.Breakpoints.Set(breakpoint with { Enabled = !breakpoint.Enabled });
        }
    }

    /// <summary>Sets the condition, hit count, filter and log message of the breakpoint on the caret's line (adding one if needed).</summary>
    public async Task EditBreakpointAsync()
    {
        if (ActiveEditor is not { Controller: { } controller } editor)
        {
            return;
        }

        var line = controller.Editor.TextArea.Caret.Line;
        var breakpoint = Debugger.Breakpoints.At(editor.Model.Path, line) ?? new BreakpointSpec(editor.Model.Path, line);
        static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        var condition = await Dialogs.ShowAsync(new DialogRequest(
            $"Breakpoint at line {line.ToString(CultureInfo.InvariantCulture)}: condition",
            "Break only when this C# expression is true (leave empty for always):",
            ["Next", "Cancel"]) { Input = breakpoint.Condition ?? string.Empty }).ConfigureAwait(true);
        if (condition.Button != "Next")
        {
            return;
        }

        var hits = await Dialogs.ShowAsync(new DialogRequest(
            "Hit count",
            "Break on the n-th hit (\"n\"), from the n-th hit on (\">= n\") or on every n-th hit (\"% n\"). Leave empty for every hit:",
            ["Next", "Cancel"])
        {
            Input = breakpoint.HitCondition ?? string.Empty,
            Validate = text => Clean(text) is not { } t || System.Text.RegularExpressions.Regex.IsMatch(t, @"^(==|>=|%)?\s*\d+$") ? null : "Use n, >= n or % n.",
        }).ConfigureAwait(true);
        if (hits.Button != "Next")
        {
            return;
        }

        var filter = await Dialogs.ShowAsync(new DialogRequest(
            "Filter",
            "Break only on these threads or processes, e.g. ThreadName = \"Worker\" || ThreadId = 12 (ThreadId, ThreadName, ProcessId, ProcessName with =, != , &, ||, !). Leave empty for all:",
            ["Next", "Cancel"])
        {
            Input = breakpoint.Filter ?? string.Empty,
            Validate = text => Clean(text) is not { } t || BreakpointFilter.TryParse(t, out _, out var error) ? null : error,
        }).ConfigureAwait(true);
        if (filter.Button != "Next")
        {
            return;
        }

        var log = await Dialogs.ShowAsync(new DialogRequest(
            "Log message",
            "Make it a logpoint: write this message (with {expressions}, $FUNCTION and $TID) and continue, instead of breaking. Leave empty to break:",
            ["OK", "Cancel"]) { Input = breakpoint.LogMessage ?? string.Empty }).ConfigureAwait(true);
        if (log.Button != "OK")
        {
            return;
        }

        Debugger.Breakpoints.Set(breakpoint with { Condition = Clean(condition.Input), HitCondition = Clean(hits.Input), Filter = Clean(filter.Input), LogMessage = Clean(log.Input) });
    }

    public async Task DeleteAllBreakpointsAsync()
    {
        if (Debugger.Breakpoints.All.Count == 0)
        {
            return;
        }

        var choice = await Dialogs.ShowAsync(new DialogRequest("Delete all breakpoints?", $"{Debugger.Breakpoints.All.Count.ToString(CultureInfo.InvariantCulture)} breakpoint(s) will be deleted.", ["Delete", "Cancel"])).ConfigureAwait(true);
        if (choice.Button == "Delete")
        {
            Debugger.Breakpoints.Clear();
        }
    }


    /// <summary>Adds the selection (or the expression under the caret) to the Watch window.</summary>
    public async Task AddWatchAsync(string? expression = null)
    {
        if (expression is null && ActiveEditor is { Controller: { } controller })
        {
            var selection = controller.Editor.SelectedText;
            expression = selection.Length > 0 && !selection.Contains('\n', StringComparison.Ordinal) ? selection : controller.ExpressionAt(controller.Editor.CaretOffset);
        }

        if (expression is null)
        {
            return;
        }

        Layout.ShowToolWindow(WatchViewModel.ToolWindowId, focus: false);
        await Watch.AddAsync(expression).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ state

    private void OnDebugStateChanged()
    {
        var state = Debugger.State;
        Workbench.Context.Set("debugActive", state != DebugState.Idle);
        Workbench.Context.Set("debugAttached", state != DebugState.Idle && Debugger.IsAttached);
        StatusBar.IsDebugging = state != DebugState.Idle;
        Workbench.Context.Set("debugStopped", state == DebugState.Stopped);
        Workbench.Context.Set("debugRunning", state == DebugState.Running);
        Toolbar.SetDebugState(state != DebugState.Idle, state == DebugState.Stopped, Runs.Running.Count > 0);
        if (state != DebugState.Idle)
        {
            // Plugins with debugger visualizers (proposed API 0.2.7) start with the first session.
            _ = Plugins.ActivateByEventAsync("onDebug", CancellationToken.None);
        }

        Workbench.Context.Set("debugCanDetach", Debugger.CanDetach);
        if (state == DebugState.Idle)
        {
            Workbench.StatusBar.Remove(DebugStatusItem);
            _pendingBrowserUrl = null;
            lock (_stdoutLine)
            {
                _stdoutLine.Clear();
            }

            _pendingBrowserUrls.Clear();
            return;
        }

        var text = state switch
        {
            DebugState.Starting => "$(debug) Starting…",
            DebugState.Stopped => "$(debug) Paused",
            _ => Debugger.IsAttached ? "$(debug) Debugging (attached)" : "$(debug) Debugging",
        };
        Workbench.StatusBar.Set(new StatusBarItem(DebugStatusItem, text, StatusBarAlignment.Left, 47, ContributionSource.Shell.Id)
        {
            Command = state == DebugState.Stopped ? "debug.continue" : "debug.stop",
            Tooltip = state == DebugState.Stopped ? "Click to continue (F5)" : "Click to stop debugging (Shift+F5)",
        });
    }

    private static async Task<bool> SourceExistsAsync(string path)
    {
        try
        {
            return await FileSystems.FileExistsAsync(path, CancellationToken.None).ConfigureAwait(true);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private async Task OnDebugStoppedAsync(StopInfo stop)
    {
        // The IDE comes to the front where the program stopped, as F10 and F11 go to it (the browser or the program's
        // window often has the focus: F11 there was the browser's full screen).
        Window?.BringToFront();
        if (!_debugWindowsShown)
        {
            _debugWindowsShown = true;
            Layout.ShowToolWindow(VariablesViewModel.LocalsWindowId, focus: false);
            Layout.ShowToolWindow(CallStackViewModel.ToolWindowId, focus: false);
        }

        if (stop.Reason == "exception")
        {
            var details = stop.Exception?.Details;
            var type = details?.TypeName ?? stop.Exception?.ExceptionId ?? "Exception";
            var message = details?.Message ?? stop.Exception?.Description ?? stop.Description ?? string.Empty;
            DebugLog.AppendLine($"Exception thrown: '{type}': {message}");
            StatusBar.ShowMessage(Localizer.Format("msg.debug.exceptionThrown", "Exception thrown: {0}: {1}", type, message), TimeSpan.FromSeconds(10));
        }

        // Through the file system that holds it: a remote window's sources are the server's (M21).
        if (Debugger.SelectedFrame is { Source.Path: { } path } frame && await SourceExistsAsync(path).ConfigureAwait(true))
        {
            await OpenFileAsync(path, preview: false, new Position(Math.Max(0, frame.Line - 1), Math.Max(0, frame.Column - 1))).ConfigureAwait(true);
        }
        else
        {
            StatusBar.ShowMessage(Localizer.Get("msg.debug.theProgramIsPausedInCode", "The program is paused in code without source (external code)."), TimeSpan.FromSeconds(6));
        }
    }
}
