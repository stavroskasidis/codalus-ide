namespace Codalus.Plugins;

/// <summary>The integrated terminals (ADR 0014). Since API 0.2.3.</summary>
public interface ITerminals
{
    /// <summary>
    /// Opens a terminal: the user's shell (<see cref="TerminalOptions.Profile"/>), or <see cref="TerminalOptions.Program"/>
    /// run in a terminal ("run command in terminal"), which ends when the program exits.
    /// </summary>
    Task<ITerminal> CreateAsync(TerminalOptions options, CancellationToken cancellationToken);
}

/// <summary>What <see cref="ITerminals.CreateAsync"/> opens.</summary>
public sealed record TerminalOptions
{
    /// <summary>The tab's name; default: the profile's or the program's name.</summary>
    public string? Name { get; init; }

    /// <summary>A terminal profile id (<c>pwsh</c>, <c>bash</c>, <c>dotnet</c>, …); null for the user's default. Ignored with <see cref="Program"/>.</summary>
    public string? Profile { get; init; }

    /// <summary>The working directory; default: the solution's folder, else the user's home.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>A program to run instead of a shell (a path, or a name found on <c>PATH</c>).</summary>
    public string? Program { get; init; }

    /// <summary>The program's arguments.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Environment variables added to the terminal's.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Whether to show the Terminal window with it (default true).</summary>
    public bool Show { get; init; } = true;

    /// <summary>Whether <see cref="ITerminal.Output"/> is raised. Off by default: output is only shown in the terminal.</summary>
    public bool ReportOutput { get; init; }
}

/// <summary>A terminal a plugin opened.</summary>
public interface ITerminal : IAsyncDisposable
{
    /// <summary>The shell's id of the terminal.</summary>
    string Id { get; }

    /// <summary>The process running in the terminal.</summary>
    int ProcessId { get; }

    /// <summary>The program's exit code once it exited (null when it couldn't be determined).</summary>
    Task<int?> Exited { get; }

    /// <summary>Text the program wrote, escape sequences included, as it arrives (with <see cref="TerminalOptions.ReportOutput"/>).</summary>
    event EventHandler<TerminalOutputEventArgs>? Output;

    /// <summary>Sends text as if typed. Add <c>"\r"</c> to run a command line.</summary>
    Task SendTextAsync(string text, CancellationToken cancellationToken);

    /// <summary>Ends the program and closes the terminal. Disposing does the same.</summary>
    Task KillAsync(CancellationToken cancellationToken);
}

/// <summary>Arguments of <see cref="ITerminal.Output"/>.</summary>
public sealed class TerminalOutputEventArgs(string text) : EventArgs
{
    /// <summary>The text, escape sequences included.</summary>
    public string Text { get; } = text;
}
