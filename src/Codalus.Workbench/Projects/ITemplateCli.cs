namespace Codalus.Workbench.Projects;

/// <summary>What a <c>dotnet</c> command printed (standard output, then standard error) and its exit code.</summary>
public sealed record CliResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>
    /// The line that says what went wrong, the error New Project shows when <c>dotnet new</c> fails. Usage hints, the
    /// commands they suggest and the link to the exit codes don't count. An <c>Error:</c> line counts first; when it
    /// introduces a list (<c>Error: Invalid option(s):</c>), its first explanation does (<c>'--x' is not a valid option</c>).
    /// Otherwise the last line counts, after the line that introduces it when there is one
    /// (<c>Creating this template will make changes to existing files: Overwrite Program.cs</c>).
    /// </summary>
    public string LastMeaningfulLine
    {
        get
        {
            var lines = Output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !IsHint(l))
                .ToList();
            if (lines.Count == 0)
            {
                return $"exit code {ExitCode}";
            }

            var error = lines.FindIndex(l => l.StartsWith("Error:", StringComparison.OrdinalIgnoreCase));
            if (error >= 0)
            {
                if (!lines[error].EndsWith(':'))
                {
                    return lines[error];
                }

                // The option or value is on a line of its own (no sentence); its explanation follows.
                var detail = lines.Skip(error + 1).FirstOrDefault(l => l.Contains(' ', StringComparison.Ordinal) && !l.StartsWith('-'));
                return detail is null ? lines[error].TrimEnd(':') : FirstSentence(detail);
            }

            var last = string.Join(' ', lines[^1].Split(' ', StringSplitOptions.RemoveEmptyEntries));
            return lines.Count > 1 && lines[^2].EndsWith(':') ? lines[^2] + " " + last : last;
        }
    }

    /// <summary>What the CLI prints to help rather than to say what's wrong: help and retry hints, commands, links.</summary>
    private static bool IsHint(string line) =>
        line.StartsWith("For details on the exit code", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("For more information", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Rerun the command", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("dotnet new ", StringComparison.Ordinal)
        || line.StartsWith("To ", StringComparison.Ordinal)
        || line.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    /// <summary>A line up to the sentence that introduces a list (<c>… for --framework. The possible values are:</c>).</summary>
    private static string FirstSentence(string line)
    {
        var end = line.LastIndexOf(". ", StringComparison.Ordinal);
        return line.EndsWith(':') && end > 0 ? line[..(end + 1)] : line;
    }
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
