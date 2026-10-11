using System.Diagnostics;
using System.Globalization;
using DaedalusStudio.Debugging;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Debugging;

namespace DaedalusStudio.Shell;

/// <summary>
/// Debugger depth (ADR 0021): QuickWatch, exception settings, Run to Cursor, function, temporary and dependent
/// breakpoints with labels, import and export, string visualizers, the Threads and Modules windows, and reattaching.
/// </summary>
public sealed partial class ShellHost
{
    private (int Id, string Name)? _lastAttached;

    public QuickWatchViewModel QuickWatch { get; private set; } = null!;

    public ThreadsViewModel ThreadList { get; private set; } = null!;

    public ModulesViewModel ModuleList { get; private set; } = null!;

    public ParallelStacksViewModel ParallelStacksWindow { get; private set; } = null!;

    public ParallelWatchViewModel ParallelWatchWindow { get; private set; } = null!;

    public ProcessesViewModel ProcessesWindow { get; private set; } = null!;

    public DebugLocationViewModel DebugLocation { get; private set; } = null!;

    public ExceptionSettingsViewModel ExceptionSettingsWindow { get; private set; } = null!;

    private void InitializeDebugDepth()
    {
        QuickWatch = new QuickWatchViewModel(Debugger, AddWatchAsync);
        ThreadList = new ThreadsViewModel(Debugger);
        ModuleList = new ModulesViewModel(Debugger);
        ParallelStacksWindow = new ParallelStacksViewModel(Debugger);
        ParallelWatchWindow = new ParallelWatchViewModel(Debugger);
        ProcessesWindow = new ProcessesViewModel(Debugger, Run);
        DebugLocation = new DebugLocationViewModel(Debugger);
        Toolbar.DebugLocation = DebugLocation;
        ExceptionSettingsWindow = new ExceptionSettingsViewModel(Debugger.ExceptionSettings);
        Debugger.ExceptionSettings.Changed += (_, _) => Ui.Post(SaveEditorSession);
        Views.BreakpointsView.RunCommand = Run;
        Views.BreakpointsView.AskLabels = async row =>
        {
            var result = await Dialogs.ShowAsync(new DialogRequest($"Labels: {row.Location}", "Labels, separated by commas:", ["OK", "Cancel"]) { Input = row.Labels }).ConfigureAwait(true);
            return result.Button == "OK" ? result.Input ?? string.Empty : null;
        };
        Debugger.Breakpoints.FunctionsChanged += (_, _) => Ui.Post(() => Workbench.Context.Set("hasBreakpoints", Debugger.Breakpoints.All.Count + Debugger.Breakpoints.Functions.Count > 0));
    }

    // ------------------------------------------------------------------ session format

    private static Docking.SessionBreakpoint ToSession(BreakpointSpec b) => new(b.Path, b.Line, b.Condition, b.HitCondition, b.LogMessage, b.Enabled)
    {
        Temporary = b.Temporary,
        DependsOnPath = b.DependsOn?.Path,
        DependsOnLine = b.DependsOn?.Line,
        Labels = b.Labels,
        Filter = b.Filter,
    };

    private static BreakpointSpec FromSession(Docking.SessionBreakpoint b) => new(
        b.Path, b.Line, b.Condition, b.HitCondition, b.LogMessage, b.Enabled, b.Temporary,
        b is { DependsOnPath: { } path, DependsOnLine: { } line } ? new BreakpointLocation(path, line) : null,
        b.Labels,
        b.Filter);

    private static Docking.SessionFunctionBreakpoint ToSession(FunctionBreakpointSpec f) => new(f.Name, f.Condition, f.Enabled, f.Labels);

    private static FunctionBreakpointSpec FromSession(Docking.SessionFunctionBreakpoint f) => new(f.Name, f.Condition, f.Enabled, f.Labels);

    // ------------------------------------------------------------------ QuickWatch and visualizers

    /// <summary>Shift+F9: QuickWatch on the selection or the expression under the caret (or <paramref name="expression"/>).</summary>
    public async Task ShowQuickWatchAsync(string? expression = null)
    {
        if (expression is null && ActiveEditor is { Controller: { } controller })
        {
            var selection = controller.Editor.SelectedText;
            expression = selection.Length > 0 && !selection.Contains('\n', StringComparison.Ordinal) ? selection : controller.ExpressionAt(controller.Editor.CaretOffset);
        }

        Layout.ShowToolWindow(QuickWatchViewModel.ToolWindowId, focus: true);
        await QuickWatch.EvaluateAsync(expression ?? QuickWatch.Expression).ConfigureAwait(true);
    }

    /// <summary>Opens a string value in a visualizer: the whole string, evaluated again by its expression when it has one.</summary>
    public async Task VisualizeAsync(VariableNode node, VisualizerKind kind)
    {
        if (kind is VisualizerKind.Grid or VisualizerKind.Image)
        {
            await VisualizeCollectionAsync(node, kind).ConfigureAwait(true);
            return;
        }

        var value = node.Value;
        if (node.EvaluateName is { } expression && Debugger.State == DebugState.Stopped)
        {
            try
            {
                value = (await Debugger.EvaluateAsync(expression, "clipboard").ConfigureAwait(true)).Result;
            }
            catch (Exception ex) when (ex is DapException or DapConnectionLostException or InvalidOperationException or OperationCanceledException)
            {
                // Keep the value shown in the tree.
            }
        }

        await OpenVisualizerAsync(node.EvaluateName ?? node.Name, Visualizers.Unquote(value), kind).ConfigureAwait(true);
    }

    /// <summary>The grid and image visualizers (M14b): read from the paused program by the value's expression.</summary>
    private async Task VisualizeCollectionAsync(VariableNode node, VisualizerKind kind)
    {
        if (node.EvaluateName is not { } expression || Debugger.State != DebugState.Stopped)
        {
            StatusBar.ShowMessage($"The {Visualizers.Title(kind)} needs the program to be paused.", TimeSpan.FromSeconds(4));
            return;
        }

        try
        {
            if (kind == VisualizerKind.Grid)
            {
                var grid = await CollectionVisualizers.GridAsync(Debugger, expression, node.Type).ConfigureAwait(true);
                await OpenVisualizerAsync(expression, CollectionVisualizers.Format(grid, expression), kind).ConfigureAwait(true);
                return;
            }

            var bytes = await CollectionVisualizers.BytesAsync(Debugger, expression).ConfigureAwait(true);
            if (CollectionVisualizers.ImageExtension(bytes) is not { } extension)
            {
                StatusBar.ShowMessage($"{expression} doesn't hold a PNG, JPEG, GIF, BMP, WebP or ICO image.", TimeSpan.FromSeconds(5));
                return;
            }

            var folder = Path.Combine(Path.GetTempPath(), "daedalus-visualizers");
            Directory.CreateDirectory(folder);
            var safe = string.Concat(expression.Select(c => char.IsLetterOrDigit(c) || c is '_' ? c : '_')).Trim('_');
            var path = Path.Combine(folder, (safe.Length == 0 ? "image" : safe) + extension);
            // An earlier image of the same expression is closed: the viewer doesn't reload a changed file.
            if (Layout.FindDocument<Views.ImageDocument>(Views.ImageDocument.IdFor(Path.GetFullPath(path))) is { } stale)
            {
                Layout.CloseDocument(stale);
            }

            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(true);
            OpenImage(path);
        }
        catch (Exception ex) when (ex is DapException or DapConnectionLostException or InvalidOperationException or OperationCanceledException or IOException or FormatException or OverflowException)
        {
            Workbench.Notifications.Error($"The {Visualizers.Title(kind)} couldn't read {expression}: {ex.Message}");
        }
    }

    /// <summary>A plugin's visualizer (proposed API 0.2.7): its text in a read-only document, or its table as a grid.</summary>
    public async Task VisualizeWithPluginAsync(VariableNode node, string visualizerId)
    {
        if (Plugins.DebugVisualizers.Find(visualizerId) is not { } visualizer || node.EvaluateName is not { } expression || Debugger.State != DebugState.Stopped)
        {
            StatusBar.ShowMessage("Visualizers need the program to be paused.", TimeSpan.FromSeconds(4));
            return;
        }

        try
        {
            var view = await visualizer.Visualize(new DaedalusStudio.Plugins.Protocol.DebugVisualizeParams(visualizerId, expression, node.Type, node.Value), CancellationToken.None).ConfigureAwait(true);
            if (view.Columns is { } columns && view.Rows is { } rows)
            {
                var grid = new DebugGrid(columns, [.. rows.Select(r => (IReadOnlyList<string>)[.. r, .. Enumerable.Repeat(string.Empty, Math.Max(0, columns.Length - r.Length))])], Truncated: false);
                await OpenVisualizerAsync(expression, CollectionVisualizers.Format(grid, expression), visualizer.Title, ".txt").ConfigureAwait(true);
            }
            else
            {
                var extension = view.LanguageId is { } language && Workbench.LanguageDefinitions.Get(language)?.Extensions is [var first, ..] ? first : ".txt";
                await OpenVisualizerAsync(expression, view.Text ?? string.Empty, visualizer.Title, extension).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Workbench.Notifications.Error($"{visualizer.Title} couldn't show {expression}: {ex.Message}");
        }
    }

    /// <summary>Shows <paramref name="text"/> in a read-only, colored document (JSON and XML indented).</summary>
    public Task OpenVisualizerAsync(string name, string text, VisualizerKind kind) =>
        OpenVisualizerAsync(name, Visualizers.Format(kind, text), Visualizers.Title(kind), Visualizers.Extension(kind));

    /// <summary>Shows <paramref name="text"/> in a read-only document titled <paramref name="title"/>, colored by <paramref name="extension"/>'s language.</summary>
    public Task OpenVisualizerAsync(string name, string text, string title, string extension)
    {
        var safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '.' ? c : '_')).Trim('_', '.');
        var path = Path.Combine(title, (safe.Length == 0 ? "value" : safe) + extension);
        var uri = "daedalus-visualizer:///" + Uri.EscapeDataString(title + "/" + name);
        if (Editors.FirstOrDefault(e => e.Model.Uri == uri) is { } open)
        {
            Layout.CloseDocument(open);
        }

        var model = CodeDocument.ReadOnly(path, uri, text, title);
        model.LanguageId = Languages.LanguageIdOf(path);
        var view = CreateView(model, primary: true);
        Layout.OpenDocument(view, focus: true);
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ exception settings

    /// <summary>Ctrl+Alt+E: the Exception Settings window.</summary>
    public void ShowExceptionSettings() => Layout.ShowToolWindow(ExceptionSettingsViewModel.ToolWindowId, focus: true);

    /// <summary>The exception helper's "Break when this exception type is thrown".</summary>
    public void SetBreakOnException(string type, bool value) => Debugger.ExceptionSettings.Set(type, value);

    // ------------------------------------------------------------------ execution

    /// <summary>
    /// Ctrl+F10: runs to the caret's line. While paused it continues to the line (Force: past other breakpoints); with
    /// no session, it starts debugging with a temporary breakpoint there.
    /// </summary>
    public async Task RunToCursorAsync(bool force = false)
    {
        if (ActiveEditor is not { Controller: { } controller } editor)
        {
            return;
        }

        await RunToLineAsync(editor.Model.Path, controller.Editor.TextArea.Caret.Line, force).ConfigureAwait(true);
    }

    /// <summary>Runs to <paramref name="line"/> of <paramref name="path"/> (Run to Cursor, Run to Click).</summary>
    public async Task RunToLineAsync(string path, int line, bool force = false)
    {
        switch (Debugger.State)
        {
            case DebugState.Stopped:
                await Debugger.RunToCursorAsync(path, line, force).ConfigureAwait(true);
                break;
            case DebugState.Idle:
                if (Debugger.Breakpoints.At(path, line) is null)
                {
                    Debugger.Breakpoints.Set(new BreakpointSpec(path, line, Temporary: true));
                }

                await StartDebuggingAsync().ConfigureAwait(true);
                break;
            default:
                StatusBar.ShowMessage("Run to Cursor needs the program to be paused (Break All, Ctrl+Alt+Break).", TimeSpan.FromSeconds(4));
                break;
        }
    }

    /// <summary>Shift+Alt+P: attaches to the last process again (by id while it runs, otherwise by name).</summary>
    public async Task ReattachAsync()
    {
        if (_lastAttached is not { } last)
        {
            if (!await ReattachRemoteAsync().ConfigureAwait(true))
            {
                await AttachToProcessAsync().ConfigureAwait(true);
            }

            return;
        }

        var processes = await Task.Run(ProcessList.Get).ConfigureAwait(true);
        var target = processes.FirstOrDefault(p => p.Id == last.Id && p.Name == last.Name) ?? processes.FirstOrDefault(p => p.Name == last.Name);
        if (target is null)
        {
            StatusBar.ShowMessage($"No process named {last.Name} is running.", TimeSpan.FromSeconds(5));
            return;
        }

        await AttachToProcessAsync(target.Id.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(true);
    }

    private void RememberAttached(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            _lastAttached = (processId, process.ProcessName);
            _lastRemote = null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _lastAttached = null;
        }
    }

    // ------------------------------------------------------------------ breakpoints

    /// <summary>Debug → New Breakpoint → Function Breakpoint: breaks when a method by that name is entered.</summary>
    public async Task NewFunctionBreakpointAsync()
    {
        var name = await Dialogs.ShowAsync(new DialogRequest(
            "New Function Breakpoint",
            "Break when this function is entered: Method, Type.Method or Namespace.Type.Method.",
            ["OK", "Cancel"])
        {
            Input = ActiveEditor?.Controller?.ExpressionAt(ActiveEditor.Controller.Editor.CaretOffset) ?? string.Empty,
            Validate = text => text.Trim().Length == 0 ? "Enter a function name." : null,
        }).ConfigureAwait(true);
        if (name.Button != "OK" || name.Input?.Trim() is not { Length: > 0 } function)
        {
            return;
        }

        var condition = await Dialogs.ShowAsync(new DialogRequest(
            "Condition",
            "Break only when this C# expression is true (leave empty for always):",
            ["OK", "Cancel"]) { Input = string.Empty }).ConfigureAwait(true);
        if (condition.Button != "OK")
        {
            return;
        }

        Debugger.Breakpoints.SetFunction(new FunctionBreakpointSpec(function, string.IsNullOrWhiteSpace(condition.Input) ? null : condition.Input.Trim()));
        Layout.ShowToolWindow(BreakpointsViewModel.ToolWindowId, focus: false);
    }

    /// <summary>Insert Temporary Breakpoint: a breakpoint on the caret's line that is removed once it is hit.</summary>
    public void InsertTemporaryBreakpoint()
    {
        if (ActiveEditor is { Controller: { } controller } editor && !editor.Model.IsReadOnly)
        {
            Debugger.Breakpoints.Set(new BreakpointSpec(editor.Model.Path, controller.Editor.TextArea.Caret.Line, Temporary: true));
        }
    }

    /// <summary>Breakpoint settings beyond conditions and actions: labels, temporary, and the breakpoint this one depends on.</summary>
    public async Task EditBreakpointOptionsAsync(string path, int line)
    {
        if (Debugger.Breakpoints.At(path, line) is not { } breakpoint)
        {
            return;
        }

        var labels = await Dialogs.ShowAsync(new DialogRequest(
            $"Breakpoint at line {line.ToString(CultureInfo.InvariantCulture)}: labels",
            "Labels, separated by commas (the Breakpoints window searches them):",
            ["Next", "Cancel"]) { Input = breakpoint.Labels ?? string.Empty }).ConfigureAwait(true);
        if (labels.Button != "Next")
        {
            return;
        }

        var others = Debugger.Breakpoints.All.Where(b => !b.Location.Matches(breakpoint.Location)).ToList();
        var items = new List<QuickPickItem>
        {
            new("Always enabled") { Tag = "none", Detail = breakpoint.DependsOn is null ? "current" : null },
        };
        items.AddRange(others.Select(b => new QuickPickItem($"Only after {Path.GetFileName(b.Path)}, line {b.Line.ToString(CultureInfo.InvariantCulture)} is hit")
        {
            Tag = b.Location,
            Detail = breakpoint.DependsOn is { } d && d.Matches(b.Location) ? "current" : null,
        }));
        items.Add(new QuickPickItem(breakpoint.Temporary ? "Keep it after it is hit" : "Remove it once it is hit (temporary)") { Tag = "temporary" });
        var picked = await Palette.ShowAsync(items, "Enable this breakpoint…").ConfigureAwait(true);
        var updated = breakpoint with { Labels = BreakpointStore.JoinLabels(BreakpointStore.SplitLabels(labels.Input)) };
        updated = picked?.Tag switch
        {
            "none" => updated with { DependsOn = null },
            BreakpointLocation location => updated with { DependsOn = location },
            "temporary" => updated with { Temporary = !breakpoint.Temporary },
            _ => updated,
        };
        Debugger.Breakpoints.Set(updated);
    }

    /// <summary>Exports every breakpoint to a JSON file.</summary>
    public async Task ExportBreakpointsAsync()
    {
        if (Debugger.Breakpoints.All.Count + Debugger.Breakpoints.Functions.Count == 0)
        {
            StatusBar.ShowMessage("There are no breakpoints to export.", TimeSpan.FromSeconds(4));
            return;
        }

        var folder = Solutions.Solution?.Path is { } solution ? Path.GetDirectoryName(solution)! : Environment.CurrentDirectory;
        var target = await Dialogs.ShowAsync(new DialogRequest("Export Breakpoints", "Save the breakpoints to this file:", ["Export", "Cancel"])
        {
            Input = Path.Combine(folder, "breakpoints.json"),
        }).ConfigureAwait(true);
        if (target.Button != "Export" || string.IsNullOrWhiteSpace(target.Input))
        {
            return;
        }

        try
        {
            ExportBreakpoints(target.Input.Trim());
            StatusBar.ShowMessage($"Exported the breakpoints to {target.Input.Trim()}.", TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportError("Could not export the breakpoints: " + ex.Message);
        }
    }

    public void ExportBreakpoints(string file)
    {
        var export = new Docking.BreakpointExport([.. Debugger.Breakpoints.All.Select(ToSession)], [.. Debugger.Breakpoints.Functions.Select(ToSession)]);
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(export, Docking.ShellJsonContext.Default.BreakpointExport));
    }

    /// <summary>Imports breakpoints from a JSON file, adding them to the current ones.</summary>
    public async Task ImportBreakpointsAsync()
    {
        var folder = Solutions.Solution?.Path is { } solution ? Path.GetDirectoryName(solution)! : Environment.CurrentDirectory;
        var source = await Dialogs.ShowAsync(new DialogRequest("Import Breakpoints", "Read breakpoints from this file:", ["Import", "Cancel"])
        {
            Input = Path.Combine(folder, "breakpoints.json"),
            Validate = text => File.Exists(text.Trim()) ? null : "The file doesn't exist.",
        }).ConfigureAwait(true);
        if (source.Button != "Import" || string.IsNullOrWhiteSpace(source.Input))
        {
            return;
        }

        try
        {
            var count = ImportBreakpoints(source.Input.Trim());
            StatusBar.ShowMessage($"Imported {count.ToString(CultureInfo.InvariantCulture)} breakpoint(s).", TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ReportError("Could not import the breakpoints: " + ex.Message);
        }
    }

    public int ImportBreakpoints(string file)
    {
        var import = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(file), Docking.ShellJsonContext.Default.BreakpointExport)
            ?? throw new System.Text.Json.JsonException("The file has no breakpoints.");
        var functions = import.FunctionBreakpoints ?? [];
        Debugger.Breakpoints.Add(import.Breakpoints.Select(FromSession), functions.Select(FromSession));
        return import.Breakpoints.Count + functions.Count;
    }

    // ------------------------------------------------------------------ editor services

    void IEditorServices.ShowQuickWatch(string expression) => _ = ShowQuickWatchAsync(expression);

    void IEditorServices.SetBreakOnException(string type, bool value) => SetBreakOnException(type, value);

    Task IEditorServices.RunToLineAsync(string path, int line) => RunToLineAsync(path, line);

    void IEditorServices.ShowBreakpointMenu(string path, int line, Avalonia.Controls.Control anchor) => ShowBreakpointMenu(path, line, anchor);

    /// <summary>The breakpoint glyph's context menu.</summary>
    private void ShowBreakpointMenu(string path, int line, Avalonia.Controls.Control anchor)
    {
        if (Debugger.Breakpoints.At(path, line) is not { } breakpoint)
        {
            return;
        }

        async Task EditConditionsAsync()
        {
            await OpenFileAsync(path, preview: false, new Position(line - 1, 0)).ConfigureAwait(true);
            await EditBreakpointAsync().ConfigureAwait(true);
        }

        var menu = new Avalonia.Controls.ContextMenu
        {
            ItemsSource = new Avalonia.Controls.Control[]
            {
                MenuItem("Delete Breakpoint", () => Debugger.Breakpoints.Remove(breakpoint)),
                MenuItem(breakpoint.Enabled ? "Disable Breakpoint" : "Enable Breakpoint", () => Debugger.Breakpoints.Set(breakpoint with { Enabled = !breakpoint.Enabled })),
                new Avalonia.Controls.Separator(),
                MenuItem("Conditions and Actions…", () => _ = EditConditionsAsync()),
                MenuItem("Labels, Dependency and Temporary…", () => _ = EditBreakpointOptionsAsync(path, line)),
                new Avalonia.Controls.Separator(),
                MenuItem("Breakpoints Window", () => Layout.ShowToolWindow(BreakpointsViewModel.ToolWindowId, focus: true)),
            },
        };
        menu.Open(anchor);

        static Avalonia.Controls.MenuItem MenuItem(string header, Action action)
        {
            var item = new Avalonia.Controls.MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }
    }
}
