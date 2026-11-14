using System.Globalization;
using System.Text;
using System.Text.Json;
using Codalus.Workbench.Ai.Ide;
using Codalus.Workbench.Build;
using Codalus.Workbench.Previewer;

namespace Codalus.Shell;

/// <summary>
/// M60b's tools for agents (ADR 0099): the shell's side of <see cref="IdeDesktopAppTools"/>. The desktop app the IDE runs
/// or debugs is reached through the live session the Live Visual Tree uses (<see cref="Live"/>, M19b and M40b): its
/// windows with a picture of each, its controls with their text, state and bounds, and input sent to a control inside
/// the app. The input is an action under <c>ai.agent.ideActions</c>, with no approval of its own (D52). A program an
/// agent starts with <c>offScreen</c> gets an X server of its own on Linux (<see cref="OffScreenDisplay"/>).
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>How long the desktop-app tools wait for an app that is starting to connect and open a window.</summary>
    private static readonly TimeSpan s_desktopAppWait = TimeSpan.FromSeconds(60);

    /// <summary>The most windows desktop_app_windows takes a picture of.</summary>
    private const int MaxDesktopAppPictures = 6;

    private OffScreenDisplay? _offScreen;

    /// <summary>The display the program being started gets (an agent's run or debug launch with <c>offScreen</c>).</summary>
    private string? _offScreenLaunch;

    private async Task<IdeToolResult> DesktopAppToolAsync(string tool, JsonElement args, CancellationToken cancellationToken)
    {
        var session = await DesktopAppAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            return tool switch
            {
                "desktop_app_windows" => await DesktopAppWindowsAsync(session, Int(args, "window"), Bool(args, "pictures") != false, cancellationToken).ConfigureAwait(true),
                "desktop_app_controls" => new(await DesktopAppControlsTextAsync(session, Int(args, "window"), Str(args, "filter"), cancellationToken).ConfigureAwait(true)),
                "desktop_app_input" => await DesktopAppInputAsync(session, args, cancellationToken).ConfigureAwait(true),
                _ => throw new Codalus.Workbench.Ai.Mcp.McpException($"There is no tool '{tool}'."),
            };
        }
        catch (PreviewerException ex)
        {
            throw new IdeToolException(ex.Message);
        }
    }

    /// <summary>
    /// The live session of the desktop app the IDE runs or debugs, once its agent has connected: waits while one is
    /// starting (an agent's launch, a program running whose agent hasn't connected yet).
    /// </summary>
    private async Task<LiveSession> DesktopAppAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + s_desktopAppWait;
        while (true)
        {
            if (Live is { IsConnected: true } connected)
            {
                return connected;
            }

            var starting = _agentLaunch is { IsCompleted: false }
                || (Live is not null && (Runs.Running.Any(p => !p.HasExited) || Debugger.IsActive));
            if (!starting || DateTime.UtcNow > deadline)
            {
                throw new IdeToolException(Live is null
                    ? "No desktop app is running with the live agent. Start an Avalonia app (or a WPF app on Windows) with run_project or debug_start; the live agent joins it while Live Visual Tree (debug.liveVisualTree) is on."
                    : starting
                        ? string.Create(CultureInfo.InvariantCulture, $"The desktop app's live agent hasn't connected after {s_desktopAppWait.TotalSeconds:0} s: read get_run_output.")
                        : "The desktop app isn't running any more (it ended). Start it again with run_project or debug_start.");
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>The app's windows, once it has one: the agent answers once the app has started its UI framework.</summary>
    private static async Task<IReadOnlyList<LiveWindow>> DesktopAppWindowListAsync(LiveSession session, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + s_desktopAppWait;
        PreviewerException? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await session.WindowsAsync(cancellationToken).ConfigureAwait(true) is { Count: > 0 } windows)
                {
                    return windows;
                }
            }
            catch (PreviewerException ex) when (session.IsConnected)
            {
                last = ex; // "The app hasn't started Avalonia yet."
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(true);
        }

        throw new IdeToolException(string.Create(CultureInfo.InvariantCulture, $"The desktop app has no window after {s_desktopAppWait.TotalSeconds:0} s{(last is null ? "" : " (" + last.Message + ")")}."));
    }

    private static async Task<LiveWindow> DesktopAppWindowAsync(LiveSession session, int? id, CancellationToken cancellationToken)
    {
        var windows = await DesktopAppWindowListAsync(session, cancellationToken).ConfigureAwait(true);
        return id is { } wanted
            ? windows.FirstOrDefault(w => w.Id == wanted) ?? throw new IdeToolException($"The app has no window {wanted.ToString(CultureInfo.InvariantCulture)}. Its windows: {string.Join(", ", windows.Select(Describe))}.")
            : windows[0];
    }

    private static string Describe(LiveWindow window) =>
        string.Create(CultureInfo.InvariantCulture, $"{window.Id} \"{window.Title}\" ({window.Type}{(window.Width is { } w && window.Height is { } h ? $", {w:0.##}x{h:0.##}" : "")})");

    private static async Task<IdeToolResult> DesktopAppWindowsAsync(LiveSession session, int? only, bool pictures, CancellationToken cancellationToken)
    {
        var windows = await DesktopAppWindowListAsync(session, cancellationToken).ConfigureAwait(true);
        if (only is { } id)
        {
            windows = [await DesktopAppWindowAsync(session, id, cancellationToken).ConfigureAwait(true)];
        }

        var text = new StringBuilder().AppendLine(windows.Count == 1 ? "The app has 1 window:" : $"The app has {windows.Count.ToString(CultureInfo.InvariantCulture)} windows:");
        var images = new List<IdeToolImage>();
        foreach (var window in windows)
        {
            text.Append("- ").Append(Describe(window));
            if (pictures && images.Count < MaxDesktopAppPictures)
            {
                try
                {
                    var picture = await session.PictureAsync(window.Id, cancellationToken).ConfigureAwait(true);
                    images.Add(new IdeToolImage(picture.Png, "image/png"));
                    text.Append(string.Create(CultureInfo.InvariantCulture, $": picture {images.Count} ({picture.Width}x{picture.Height} pixels)"));
                }
                catch (PreviewerException ex)
                {
                    text.Append(": no picture (").Append(ex.Message).Append(')');
                }
            }

            text.AppendLine();
        }

        text.Append("desktop_app_controls lists a window's controls; desktop_app_input clicks, types, presses keys and scrolls in them.");
        return new IdeToolResult(text.ToString()) { Images = images };
    }

    private static async Task<string> DesktopAppControlsTextAsync(LiveSession session, int? windowId, string? filter, CancellationToken cancellationToken)
    {
        var window = await DesktopAppWindowAsync(session, windowId, cancellationToken).ConfigureAwait(true);
        var controls = await session.ControlsAsync(window.Id, cancellationToken).ConfigureAwait(true);
        return "Window " + Describe(window) + ":\n" + FormatControls(controls, filter);
    }

    /// <summary>
    /// The controls as lines, indented by depth: <c>#id Type (Name) "text" [state] at x,y width x height</c>; with a
    /// filter, the controls whose type, name or text has it, and their ancestors.
    /// </summary>
    internal static string FormatControls(IReadOnlyList<LiveControl> controls, string? filter)
    {
        var byId = controls.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        HashSet<int>? shown = null;
        if (filter is { Length: > 0 })
        {
            shown = [];
            foreach (var control in controls.Where(c => c.Type.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || c.Text.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            {
                for (var current = control; current is not null && shown.Add(current.Id); current = byId.GetValueOrDefault(current.ParentId))
                {
                }
            }

            if (shown.Count == 0)
            {
                return $"No control's type, name or text contains '{filter}'.";
            }
        }

        var depth = new Dictionary<int, int>();
        var sb = new StringBuilder();
        foreach (var control in controls)
        {
            var level = depth[control.Id] = depth.TryGetValue(control.ParentId, out var parent) ? parent + 1 : 0;
            if (shown is not null && !shown.Contains(control.Id))
            {
                continue;
            }

            sb.Append(' ', level * 2).Append('#').Append(control.Id.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(control.Type);
            if (control.Name.Length > 0)
            {
                sb.Append(" (").Append(control.Name).Append(')');
            }

            if (control.Text.Length > 0)
            {
                sb.Append(" \"").Append(control.Text).Append('"');
            }

            if (control.State.Count > 0)
            {
                sb.Append(" [").Append(string.Join(", ", control.State)).Append(']');
            }

            if (control.Bounds is { } b)
            {
                sb.Append(string.Create(CultureInfo.InvariantCulture, $" at {b.X:0.#},{b.Y:0.#} {b.Width:0.#}x{b.Height:0.#}"));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private async Task<IdeToolResult> DesktopAppInputAsync(LiveSession session, JsonElement args, CancellationToken cancellationToken)
    {
        RequireTrustedForAgentActions();
        var kind = Str(args, "kind") ?? throw new IdeToolException("A kind is needed: click, text, keys or scroll.");
        var id = Int(args, "control");
        if (id is null && Str(args, "name") is { Length: > 0 } name)
        {
            foreach (var window in await DesktopAppWindowListAsync(session, cancellationToken).ConfigureAwait(true))
            {
                if ((await session.ControlsAsync(window.Id, cancellationToken).ConfigureAwait(true)).FirstOrDefault(c => c.Name == name) is { } found)
                {
                    id = found.Id;
                    break;
                }
            }

            if (id is null)
            {
                return IdeToolResult.Error($"No control of the app is named '{name}'. desktop_app_controls lists them.");
            }
        }

        if (id is not { } control)
        {
            return IdeToolResult.Error("Give the control: its id (control) from desktop_app_controls, or its name.");
        }

        var (value, replace, amount) = kind switch
        {
            "click" => ("", false, 0),
            "text" => (Str(args, "text") ?? throw new IdeToolException("The text to type is needed."), Bool(args, "replace") == true, 0),
            "keys" => (Str(args, "keys") is { Length: > 0 } keys ? keys : throw new IdeToolException("The keys are needed, e.g. 'Enter' or 'Ctrl+A Delete'."), false, 0),
            "scroll" => (Str(args, "direction") ?? "down", false, Math.Clamp(Int(args, "amount") ?? 0, 0, 1000)),
            _ => throw new IdeToolException($"Unknown kind '{kind}': click, text, keys or scroll."),
        };
        var done = await session.InputAsync(control, kind, value, replace, amount, cancellationToken).ConfigureAwait(true);
        return new(done + " Read the result with desktop_app_controls or desktop_app_windows.");
    }

    // ------------------------------------------------------------------ off-screen runs

    /// <summary>
    /// Before an agent's run or debug launch with <c>offScreen</c>: the display its program gets (an Xvfb of the IDE's,
    /// started now if it isn't running), or why its windows open as usual.
    /// </summary>
    private async Task<string> PrepareOffScreenAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return "Off-screen runs need Xvfb on Linux; here the app's windows open as usual (the desktop_app_* tools still never move the user's pointer or take their focus).";
        }

        if (_offScreen is not { IsRunning: true })
        {
            _offScreen?.Dispose();
            _offScreen = null;
            if (OffScreenDisplay.Locate(Environment.GetEnvironmentVariable("PATH")) is not { } xvfb)
            {
                return "Xvfb isn't installed, so the app's windows open as usual (install the xvfb package for off-screen runs).";
            }

            try
            {
                _offScreen = await OffScreenDisplay.StartAsync(xvfb, cancellationToken).ConfigureAwait(true);
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message + " The app's windows open as usual.";
            }
        }

        _offScreenLaunch = _offScreen.Display;
        return "Its windows open off-screen (Xvfb, display " + _offScreen.Display + ").";
    }

    /// <summary>
    /// The plan of a program an agent starts off-screen: its display (any thread). Taken once: <see cref="RunService.ProgramPlan"/>
    /// is called once per start, so a program the user starts next opens on screen.
    /// </summary>
    private LaunchPlan AddOffScreen(LaunchPlan plan)
    {
        if (Interlocked.Exchange(ref _offScreenLaunch, null) is not { } display)
        {
            return plan;
        }

        var environment = new Dictionary<string, string>(plan.Environment, StringComparer.Ordinal) { ["DISPLAY"] = display };
        return plan with { Environment = environment };
    }

    /// <summary>Ends the off-screen display with the IDE.</summary>
    private void StopOffScreen()
    {
        _offScreen?.Dispose();
        _offScreen = null;
    }
}
