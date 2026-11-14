using System.Globalization;
using Codalus.Debugging;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Previewer;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Settings;

namespace Codalus.Shell;

/// <summary>
/// XAML breakpoints, Break When Value Changes and break at a failed binding (M64b, ADR 0103), through the live agent
/// an Avalonia app (or a WPF app on Windows) gets when it's debugged from here:
/// <list type="bullet">
/// <item>A breakpoint in a <c>.axaml</c> or <c>.xaml</c> file's margin is a XAML breakpoint: never sent to the
/// debugger, which can't bind it, but to the agent (in the app's environment at launch, so the first window's
/// elements are caught, and again on each change). On an element's start tag it stops when the element is created;
/// on an attribute's line, when XAML sets that property. The agent says whether each can stop (the glyph's state).</item>
/// <item>The agent breaks into the debugger itself, after saying why (<see cref="LiveSession.XamlStopping"/>); this
/// side (<see cref="XamlStopExplainer"/>) shows the stop at the XAML line, with the app's own frames under it.</item>
/// <item><c>debug.breakOnFailedBinding</c> (off by default) stops at a binding that fails, as at a thrown exception,
/// at the binding's XAML.</item>
/// </list>
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>Whether a binding that fails in the app being debugged stops it, at its XAML (M64b; off by default).</summary>
    public const string BreakOnFailedBindingSetting = "debug.breakOnFailedBinding";

    /// <summary>The exception id a failed binding's stop reads as (the exception helper shows it as one).</summary>
    public const string BindingFailureExceptionId = "codalus/xamlBindingFailure";

    private bool _xamlBreakpointsQueued;
    private string? _xamlStopText;

    /// <summary>The last stop the live agent made (M64b), for the status bar and tests.</summary>
    public LiveXamlStop? LastXamlStop { get; private set; }

    public bool BreakOnFailedBinding
    {
        get => Workbench.Settings.GetBoolean(BreakOnFailedBindingSetting, false);
        set => _ = Workbench.Settings.SetAsync(BreakOnFailedBindingSetting, value, SettingScope.User, CancellationToken.None);
    }

    private void InitializeXamlBreakpoints()
    {
        Debugger.ExternalBreakpointFiles = XamlBreakpoints.IsXamlFile;
        Debugger.StopExplainer = new XamlStopExplainer(this);
        Debugger.Breakpoints.Changed += (_, path) =>
        {
            if (path is null || XamlBreakpoints.IsXamlFile(path))
            {
                Ui.Post(QueueXamlBreakpoints);
            }
        };
        Workbench.Settings.Changed += (_, e) =>
        {
            if (e.Keys.Contains(BreakOnFailedBindingSetting))
            {
                Ui.Post(QueueXamlBreakpoints);
            }
        };
    }

    /// <summary>A new run's session: the breakpoints again once it connects, and when the agent says their states changed.</summary>
    private void HookXamlBreakpoints(LiveSession session)
    {
        session.Connected += (_, _) => Ui.Post(() => _ = SyncXamlBreakpointsAsync(session));
        session.XamlBreakpointsChanged += (_, _) => Ui.Post(() => _ = SyncXamlBreakpointsAsync(session));
    }

    /// <summary>The XAML breakpoints as the agent takes them, and each one's target (any thread: the files are read from disk, as the app was built).</summary>
    private (string Encoded, List<(BreakpointSpec Spec, XamlBreakpointTarget Target)> Targets) XamlBreakpointList()
    {
        var targets = new List<(BreakpointSpec, XamlBreakpointTarget)>();
        foreach (var group in Debugger.Breakpoints.All.Where(b => b.Enabled && XamlBreakpoints.IsXamlFile(b.Path)).GroupBy(b => b.Path, SolutionService.PathComparer))
        {
            var text = TryRead(group.Key);
            foreach (var breakpoint in group)
            {
                targets.Add((breakpoint, text is null
                    ? new XamlBreakpointTarget(XamlBreakpointKind.None, breakpoint.Line, 0, string.Empty, string.Empty, false, Localizer.Get("msg.xamlBreakpoints.fileUnreadable", "The XAML file couldn't be read."))
                    : XamlBreakpoints.Resolve(text, breakpoint.Line)));
            }
        }

        var encoded = XamlBreakpoints.Encode(
            targets.Select(t => new XamlBreakpoints.AgentBreakpoint(BreakpointId(t.Item1), t.Item1.Path, t.Item2)),
            Workbench.Settings.GetBoolean(BreakOnFailedBindingSetting, false));
        return (encoded, targets);
    }

    private static string BreakpointId(BreakpointSpec breakpoint) => breakpoint.Path + ":" + breakpoint.Line.ToString(CultureInfo.InvariantCulture);

    /// <summary>The breakpoints' changes reach the running app's agent (a quarter of a second after the first: edits come in bursts).</summary>
    private void QueueXamlBreakpoints()
    {
        if (_xamlBreakpointsQueued || Live is not { IsConnected: true } session)
        {
            return;
        }

        _xamlBreakpointsQueued = true;
        _ = Task.Delay(250).ContinueWith(_ => Ui.Post(() =>
        {
            _xamlBreakpointsQueued = false;
            _ = SyncXamlBreakpointsAsync(session);
        }), TaskScheduler.Default);
    }

    /// <summary>Sends the XAML breakpoints to the app's agent and shows what it says of each, while debugging (UI thread).</summary>
    internal async Task SyncXamlBreakpointsAsync(LiveSession session)
    {
        if (!ReferenceEquals(Live, session) || !session.IsConnected)
        {
            return;
        }

        try
        {
            var (encoded, targets) = await Task.Run(XamlBreakpointList).ConfigureAwait(true);
            var states = await session.SetXamlBreakpointsAsync(encoded, CancellationToken.None).ConfigureAwait(true);
            if (!Debugger.IsActive)
            {
                return; // run without the debugger: nothing can stop, and a glyph shows no state then
            }

            var byId = states.ToDictionary(s => s.Id, StringComparer.Ordinal);
            foreach (var file in targets.GroupBy(t => t.Spec.Path, SolutionService.PathComparer))
            {
                Debugger.Breakpoints.SetExternalStates(file.Key, [.. file.Select(t => (t.Spec.Line, t.Target.Kind == XamlBreakpointKind.None
                    ? new BreakpointState(false, t.Target.Problem)
                    : byId.TryGetValue(BreakpointId(t.Spec), out var state)
                        ? new BreakpointState(state.Bound, state.Bound ? null : state.Message)
                        : new BreakpointState(false, Localizer.Get("msg.xamlBreakpoints.notBound", "The app's live agent didn't take this XAML breakpoint."))))]);
            }
        }
        catch (PreviewerException ex)
        {
            PreviewerLog.AppendLine("XAML breakpoints: " + ex.Message);
        }
    }

    /// <summary>The launch's breakpoints for the agent (<see cref="AddLiveAgent"/>, any thread).</summary>
    private string XamlBreakpointsAtLaunch() => XamlBreakpointList().Encoded;

    /// <summary>A XAML stop's line in the status bar and the Debug output (UI thread, from <see cref="OnDebugStoppedAsync"/>).</summary>
    private void ShowXamlStop(StopInfo stop)
    {
        if (stop.Description is { Length: > 0 } text && text == Volatile.Read(ref _xamlStopText))
        {
            DebugLog.AppendLine(text);
            StatusBar.ShowMessage(text, TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// The stops the live agent makes (M64b): the stopped thread is in the agent's <c>LiveBreak.Stop</c> (or the WPF
    /// agent's <c>WpfBreak.Stop</c>), which told the session why just before; the stop shows a frame for the XAML line
    /// in front (its id the agent frame's, whose Locals are the stop and the element), the app's frames under it.
    /// </summary>
    internal sealed class XamlStopExplainer(ShellHost shell) : IStopExplainer
    {
        private const string AvaloniaAgent = "Codalus.Previewer.Agent.";
        private const string WpfAgent = "Codalus.Previewer.Wpf.";
        private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(3);

        private static bool IsAgent(StackFrame frame) => frame.Name.Contains(AvaloniaAgent, StringComparison.Ordinal) || frame.Name.Contains(WpfAgent, StringComparison.Ordinal);

        private static bool IsBreak(StackFrame frame) =>
            frame.Name.Contains("LiveBreak.Stop", StringComparison.Ordinal) || frame.Name.Contains("WpfBreak.Stop", StringComparison.Ordinal);

        public bool Claims(IReadOnlyList<StackFrame> frames) => frames.Take(12).Any(IsBreak);

        public async Task<ExplainedStop?> ExplainAsync(IReadOnlyList<StackFrame> frames, CancellationToken cancellationToken)
        {
            if (shell.Live is not { } session)
            {
                shell.PreviewerLog.AppendLine("XAML breakpoints: the app stopped in the live agent's break, but no live session is open.");
                return null;
            }

            if (await session.TakeStopAsync(s_wait, cancellationToken).ConfigureAwait(false) is not { } stop)
            {
                shell.PreviewerLog.AppendLine($"XAML breakpoints: the app stopped in the live agent's break, but the agent said nothing of it within {s_wait.TotalSeconds:0} s (connected: {session.IsConnected}).");
                return null;
            }

            // The app's frames: from the first after the agent's (Debugger.Break and the agent's own come first).
            var lastAgent = -1;
            for (var i = 0; i < frames.Count && i < 24; i++)
            {
                if (IsAgent(frames[i]))
                {
                    lastAgent = i;
                }
            }

            var below = frames.Skip(lastAgent + 1).ToList();
            var id = (frames.FirstOrDefault(IsBreak) ?? below.FirstOrDefault() ?? frames[0]).Id;
            var element = Short(stop.ElementType) + (stop.ElementName.Length > 0 ? " '" + stop.ElementName + "'" : string.Empty);
            var file = stop.File.Length > 0 ? Path.GetFileName(stop.File) : string.Empty;
            string description;
            StackFrame top;
            StackFrame? selected;
            ExceptionInfoResponse? exception = null;
            var reason = "breakpoint";
            switch (stop.Kind)
            {
                case "attribute":
                    // At the breakpoint's line, the attribute's (the app knows its element's).
                    var line = BreakpointLine(stop.BreakpointId) ?? stop.Line;
                    description = Localizer.Format("msg.xamlBreakpoints.attribute", "XAML breakpoint: {0} of {1} set to '{2}' ({3}, line {4}).", stop.Property, element, stop.NewValue, file, line.ToString(CultureInfo.CurrentCulture));
                    top = new StackFrame(id, $"[XAML] {element}.{stop.Property} = {stop.NewValue}", new Source(file, stop.File), line, line == stop.Line ? Math.Max(1, stop.Column) : 1);
                    selected = top;
                    break;
                case "value":
                    reason = "data breakpoint";
                    description = Localizer.Format("msg.xamlBreakpoints.valueChanged", "Break When Value Changes: {0} of {1} changed from '{2}' to '{3}'.", stop.Property, element, stop.OldValue, stop.NewValue);
                    top = new StackFrame(id, $"[Break When Value Changes] {element}.{stop.Property}: {stop.OldValue} → {stop.NewValue}", null, 0, 0, PresentationHint: "label");

                    // Where the change was made: the first frame with source under the agent's (the app's code).
                    selected = below.FirstOrDefault(f => f.Source?.Path is not null) ?? top;
                    break;
                case "binding":
                    reason = "exception";
                    var location = await Task.Run(() => shell.LocateFailedBinding(stop), cancellationToken).ConfigureAwait(false);
                    description = Localizer.Format("msg.xamlBreakpoints.bindingFailed", "The binding of {0} to '{1}' failed: {2}", stop.Property, stop.BindingPath, stop.Message);
                    top = location is null
                        ? new StackFrame(id, $"[Binding failed] {element}.{stop.Property}", null, 0, 0, PresentationHint: "label")
                        : new StackFrame(id, $"[Binding failed] {element}.{stop.Property}", new Source(Path.GetFileName(location.Path), location.Path), location.Line + 1, location.Column + 1);
                    selected = top;
                    exception = new ExceptionInfoResponse(BindingFailureExceptionId, description, "always",
                        new ExceptionDetails(description, Localizer.Get("msg.xamlBreakpoints.bindingFailure", "Binding failure"), Localizer.Get("msg.xamlBreakpoints.bindingFailure", "Binding failure"), EvaluateName: "stop"));
                    break;
                default:
                    description = Localizer.Format("msg.xamlBreakpoints.element", "XAML breakpoint: {0} created ({1}, line {2}).", element, file, stop.Line.ToString(CultureInfo.CurrentCulture));
                    top = new StackFrame(id, $"[XAML] {element} created", new Source(file, stop.File), stop.Line, Math.Max(1, stop.Column));
                    selected = top;
                    break;
            }

            shell.LastXamlStop = stop;
            Volatile.Write(ref shell._xamlStopText, description);
            return new ExplainedStop(reason, description, [top, .. below], selected, exception);
        }

        private static string Short(string type) => type[(type.LastIndexOf('.') + 1)..];

        /// <summary>The line of a breakpoint's id (<see cref="BreakpointId"/>: its path and line).</summary>
        private static int? BreakpointLine(string id) =>
            id.LastIndexOf(':') is var colon and > 0 && int.TryParse(id.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var line) ? line : null;
    }

    /// <summary>A failed binding's XAML: the attribute on the element the app's source information names, else as the Error List finds it.</summary>
    private XamlBindingLocation? LocateFailedBinding(LiveXamlStop stop)
    {
        if (stop.File.Length > 0 && stop.Line > 0 && TryRead(stop.File) is { } text
            && XamlBindingLocator.FindAtElement(stop.File, text, stop.Line, stop.Column, stop.Property) is { } exact)
        {
            return exact;
        }

        var texts = XamlFiles().Select(f => (Path: f, Text: TryRead(f))).Where(f => f.Text is not null).Select(f => (f.Path, f.Text!)).ToList();
        return XamlBindingLocator.Find(new LiveBindingFailure(stop.Property, stop.BindingPath, stop.Message, stop.ElementType, stop.ElementName, stop.Ancestors), texts);
    }
}
