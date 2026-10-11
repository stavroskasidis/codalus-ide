using DaedalusStudio.Debugging;

namespace DaedalusStudio.Workbench.Terminal;

/// <summary>
/// The debugger's terminal (M14c): programs debugged with <c>debug.console</c> set to the integrated terminal, and DAP
/// <c>runInTerminal</c>, start in a new integrated terminal. <paramref name="started"/> lets the shell show it.
/// </summary>
public sealed class TerminalLauncher(TerminalService terminals, Action<TerminalSession>? started = null) : ITerminalLauncher
{
    /// <summary>The terminal of the last launch.</summary>
    public TerminalSession? Last { get; private set; }

    public Task<int?> LaunchAsync(TerminalLaunch launch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = terminals.Run(launch);
        Last = session;
        started?.Invoke(session);
        return Task.FromResult<int?>(session.ProcessId);
    }
}
