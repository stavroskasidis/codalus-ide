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
    /// <summary>A folder for netcoredbg's DAP logs (<c>--engineLogging</c>), to diagnose a session.</summary>
    public const string LogVariable = "DAEDALUS_NETCOREDBG_LOG";

    /// <summary>netcoredbg at <paramref name="path"/> on this machine (logging into <c>DAEDALUS_NETCOREDBG_LOG</c> when set).</summary>
    public static DebugAdapterStartInfo Local(string path)
    {
        List<string> arguments = ["--interpreter=vscode"];
        if (System.Environment.GetEnvironmentVariable(LogVariable) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            arguments.Add("--engineLogging=" + Path.Combine(folder, $"netcoredbg-{DateTime.Now:HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.log"));
        }

        return new(path, arguments, Path.GetDirectoryName(path));
    }
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
