using DaedalusStudio.Debugging;
using DaedalusStudio.Workbench.Build;

namespace DaedalusStudio.Workbench.Debugging;

/// <summary>Where a launched program's console is (setting <c>debug.console</c>, M14c).</summary>
public enum DebugConsoleKind
{
    /// <summary>The Debug Console: output arrives through the debugger, and there is no input.</summary>
    Internal,

    /// <summary>
    /// The integrated terminal: a real console with input. The program starts there, waits in a startup hook, and
    /// the debugger attaches, so Detach All leaves it running.
    /// </summary>
    Terminal,

    /// <summary>
    /// A console window of its own (Windows, as a classic IDE debugs console and web apps): held and attached to like
    /// <see cref="Terminal"/>, the window stays open after the program ends until a key is pressed.
    /// </summary>
    Window,
}

/// <summary>
/// A program a debug session launches (M14c): its plan, its console, and for a program on another machine (a WSL
/// distribution) the adapter's pipe transport and the launch in that machine's terms.
/// </summary>
public sealed record DebugLaunch(string Name, LaunchPlan Plan)
{
    public DebugConsoleKind Console { get; init; } = DebugConsoleKind.Internal;

    /// <summary>Set for a program that runs on another machine (the WSL launch profile).</summary>
    public RemoteLaunch? Remote { get; init; }
}

/// <summary>
/// A launch on another machine: netcoredbg there through <see cref="Adapter"/>, the <c>launch</c> request with that
/// machine's program and paths, and the source paths between the two.
/// </summary>
public sealed record RemoteLaunch(DebugAdapterStartInfo Adapter, LaunchArguments Arguments, SourceMap Map, string Target);

/// <summary>An attach on another machine (M14c): netcoredbg there through <see cref="Adapter"/>, and the source paths between the two.</summary>
public sealed record RemoteAttach(DebugAdapterStartInfo Adapter, SourceMap Map, string Target);

/// <summary>
/// A program of the session, for the Processes window: <see cref="Kind"/> is <c>Launched</c>, <c>Attached</c> or
/// <c>Terminal</c> (launched in the integrated terminal); <see cref="Target"/> names another machine (null: this one).
/// </summary>
public sealed record DebugProcess(string Name, int? Id, string Kind, string? Target, bool Ended = false, int? ExitCode = null)
{
    public const string Launched = "Launched";
    public const string Attached = "Attached";
    public const string Terminal = "Terminal";
    public const string ConsoleWindow = "ConsoleWindow";

    /// <summary>Detach All can leave it running: it was attached to, or it has a console of its own.</summary>
    public bool CanDetach => Kind is Attached or Terminal or ConsoleWindow;
}
