using System.Globalization;
using DaedalusStudio.Debugging;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Platform;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Rpc;
using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Shell.Views;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Debugging;
using DaedalusStudio.Workbench.Output;
using DaedalusStudio.Workbench.StatusBar;

namespace DaedalusStudio.Shell;

/// <summary>Debugging (ADR 0007): sessions on netcoredbg, breakpoints, stepping and the debugger's tool windows.</summary>
public sealed partial class ShellHost
{
    public const string JustMyCodeSetting = "debug.justMyCode";

    /// <summary>Step over properties and operators (M14b).</summary>
    public const string StepFilteringSetting = "debug.stepOverPropertiesAndOperators";

    private const string DebugStatusItem = "daedalus.debug";
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

    public OutputChannel DebugLog => Workbench.Output.GetOrCreate("daedalus.debug", "Debug");

    private void InitializeDebugging()
    {
        Debugger = new DebugService(DebugAdapterLocator.Resolve) { JustMyCode = Workbench.Settings.GetBoolean(JustMyCodeSetting, true) };
        // [DebuggerDisplay] and [DebuggerBrowsable] from the companion host (M14b); netcoredbg doesn't apply them.
        Debugger.Displays = new DebuggerDisplays(async (type, modules, cancellationToken) =>
            await Companion.TypeDisplayAsync(type, modules, cancellationToken).ConfigureAwait(false));
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
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text).ConfigureAwait(true);
            }
        };

        Debugger.StateChanged += (_, _) => Ui.Post(OnDebugStateChanged);
        Debugger.Stopped += (_, stop) => Ui.Post(() => _ = OnDebugStoppedAsync(stop));
        Debugger.Output += (_, output) => OnDebugOutput(output);
        Debugger.Ended += (_, code) => Ui.Post(() => StatusBar.ShowMessage(
            code is { } c ? $"The program exited with code {c.ToString(CultureInfo.InvariantCulture)}." : "Debugging stopped.", TimeSpan.FromSeconds(6)));
        Debugger.Breakpoints.Changed += (_, _) => Ui.Post(() => Workbench.Context.Set("hasBreakpoints", Debugger.Breakpoints.All.Count + Debugger.Breakpoints.Functions.Count > 0));
        Watch.Changed += (_, _) => SaveEditorSession();
        InitializeDebugDepth();
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

    private void OnDebugOutput(DebugOutput output)
    {
        DebugLog.Append(output.Text);
        OnServerOutput(output);
        if (_pendingBrowserUrl is { } url && output.Category == "stdout"
            && (output.Text.Contains("Now listening on:", StringComparison.Ordinal) || output.Text.Contains("Application started.", StringComparison.Ordinal)))
        {
            _pendingBrowserUrl = null;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                ShellIntegration.OpenUrl(uri);
            }
        }
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

        if (!await BuildBeforeRunAsync(project).ConfigureAwait(true))
        {
            if (Builds.LastBuild is { Result: { Canceled: false, Errors: > 0 } })
            {
                StatusBar.ShowMessage("The build failed, so debugging did not start.", TimeSpan.FromSeconds(6));
            }

            return;
        }

        _ = OfferDevCertificateAsync(project);
        try
        {
            var (info, profiles, error) = await Runs.GetProfilesAsync(project, CancellationToken.None).ConfigureAwait(true);
            var profile = Runs.ResolveProfile(project, profiles);
            var plan = Runs.Adjust(LaunchPlanner.Plan(info, profile), info);
            DebugLog.Clear();
            if (error is not null)
            {
                DebugLog.AppendLine("Ignoring the launch settings: " + error);
            }

            if (LaunchPlanner.BrowserDebugTarget(info, profile) is { } target)
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
            if (LaunchPlanner.PageDebugTarget(profile, Workbench.Settings.GetBoolean(JavaScriptDebugSetting, true)) is { } page)
            {
                // The server and its page in one session (ADR 0027).
                await StartCompoundDebuggingAsync(project, plan, page).ConfigureAwait(true);
                return;
            }

            _pendingBrowserUrl = plan.BrowserUrl;
            await Debugger.LaunchAsync(plan, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonRpcRemoteException or JsonRpcConnectionLostException)
        {
            Workbench.Notifications.Error($"Could not start debugging {Path.GetFileNameWithoutExtension(project)}: {ex.Message}");
        }
    }

    /// <summary>Debug → Attach to Process: the .NET processes first.</summary>
    public async Task AttachToProcessAsync(string? argument = null)
    {
        if (Debugger.IsActive)
        {
            StatusBar.ShowMessage("A debug session is already running.", TimeSpan.FromSeconds(4));
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
                Detail = p.IsDotnet ? ".NET" + (p.Title is null ? string.Empty : " · " + p.Title) : p.Title,
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
            Workbench.Notifications.Error($"Could not attach to process {processId.ToString(CultureInfo.InvariantCulture)}: {ex.Message}");
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

        if (state == DebugState.Idle)
        {
            Workbench.StatusBar.Remove(DebugStatusItem);
            _pendingBrowserUrl = null;
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

    private async Task OnDebugStoppedAsync(StopInfo stop)
    {
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
            StatusBar.ShowMessage($"Exception thrown: {type}: {message}", TimeSpan.FromSeconds(10));
        }

        if (Debugger.SelectedFrame is { Source.Path: { } path } frame && File.Exists(path))
        {
            await OpenFileAsync(path, preview: false, new Position(Math.Max(0, frame.Line - 1), Math.Max(0, frame.Column - 1))).ConfigureAwait(true);
        }
        else
        {
            StatusBar.ShowMessage("The program is paused in code without source (external code).", TimeSpan.FromSeconds(6));
        }
    }
}
