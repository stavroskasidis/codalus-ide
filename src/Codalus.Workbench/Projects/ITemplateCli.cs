namespace Codalus.Workbench.Projects;

/// <summary>What a <c>dotnet</c> command printed (standard output, then standard error) and its exit code.</summary>
public sealed record CliResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>
    /// The last line that says something (not a blank, a usage hint or a link to the exit codes): the error New Project
    /// shows when <c>dotnet new</c> fails.
    /// </summary>
    public string LastMeaningfulLine =>
        Output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 0
                && !l.StartsWith("For details on the exit code", StringComparison.OrdinalIgnoreCase)
                && !l.StartsWith("dotnet new ", StringComparison.Ordinal)
                && !l.StartsWith("To ", StringComparison.Ordinal)
                && !l.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        ?? $"exit code {ExitCode}";
}

/// <summary>
/// The <c>dotnet</c> CLI as New Project uses it (ADR 0082): one process per command, always in the neutral state
/// folder (so no workspace's <c>global.json</c> picks the SDK, and no workspace code runs), in English, with the test
/// hive (<see cref="TemplateService.HiveVariable"/>) added to every <c>dotnet new</c> command by the implementation and
/// nobody else. <see cref="TemplateService"/> is the real one; tests stand in for it.
/// </summary>
public interface ITemplateCli
{
    /// <summary>The template hive the CLI uses (tests), or null for the user's.</summary>
    string? Hive { get; }

    /// <summary>The installed template packages changed (install, uninstall, update, export).</summary>
    event EventHandler? Changed;

    /// <summary>Runs <c>dotnet &lt;args&gt;</c>; cancelling kills it.</summary>
    Task<CliResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken);

    /// <summary>Installs a template package (<c>dotnet new install</c>) and raises <see cref="Changed"/>.</summary>
    Task<(bool Succeeded, string Output)> InstallAsync(string package, CancellationToken cancellationToken);
}
