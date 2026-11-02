namespace Codalus.Debugging;

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
    /// <summary>Starts the adapter through a remote server (M21, ADR 0050) instead of here; null: this machine.</summary>
    public Codalus.Rpc.IProcessLauncher? Launcher { get; init; }

    /// <summary>A folder for netcoredbg's DAP logs (<c>--engineLogging</c>), to diagnose a session.</summary>
    public const string LogVariable = "CODALUS_NETCOREDBG_LOG";

    /// <summary>netcoredbg at <paramref name="path"/> on this machine (logging into <c>CODALUS_NETCOREDBG_LOG</c> when set).</summary>
    public static DebugAdapterStartInfo Local(string path) => Local(path, System.Environment.GetEnvironmentVariable(LogVariable));

    /// <summary>
    /// netcoredbg at <paramref name="path"/>, logging into <paramref name="logFolder"/> when it is set. The log's path is
    /// made absolute here: netcoredbg starts in its own folder, so a relative one (the smoke test's
    /// <c>artifacts/smoke-test/netcoredbg</c>) named a folder that isn't there, and netcoredbg wrote no log (Linux arm64's
    /// debug.compound, run 41374588224, kept none).
    /// </summary>
    internal static DebugAdapterStartInfo Local(string path, string? logFolder)
    {
        List<string> arguments = ["--interpreter=vscode"];
        if (logFolder is { Length: > 0 })
        {
            var folder = Directory.CreateDirectory(logFolder).FullName;
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
