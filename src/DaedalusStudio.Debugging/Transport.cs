namespace DaedalusStudio.Debugging;

/// <summary>
/// How to start a debug adapter: netcoredbg here (<see cref="Local"/>), or a pipe transport (M14c) — a program whose
/// stdin and stdout carry DAP to netcoredbg on another machine: <c>ssh host …/netcoredbg --interpreter=vscode</c>,
/// <c>docker exec -i container …</c> or <c>wsl.exe -d distribution …</c>. <see cref="Transport"/> names it in
/// messages; null for a local adapter.
/// </summary>
public sealed record DebugAdapterStartInfo(
    string Program,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? Transport = null)
{
    /// <summary>netcoredbg at <paramref name="path"/> on this machine.</summary>
    public static DebugAdapterStartInfo Local(string path) => new(path, ["--interpreter=vscode"], Path.GetDirectoryName(path));
}

/// <summary>A program to start in a terminal (DAP <c>runInTerminal</c>, or a launch in the integrated terminal).</summary>
/// <param name="Environment">Variables to set; a null value unsets one.</param>
public sealed record TerminalLaunch(string Title, string Program, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string?> Environment);

/// <summary>Starts programs in the IDE's terminal (M14c): the shell's integrated terminal, or a test's stand-in.</summary>
public interface ITerminalLauncher
{
    /// <summary>Starts <paramref name="launch"/> in a new terminal and returns the process id of the program, when known.</summary>
    Task<int?> LaunchAsync(TerminalLaunch launch, CancellationToken cancellationToken);
}
