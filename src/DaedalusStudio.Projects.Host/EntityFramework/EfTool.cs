using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DaedalusStudio.Projects.Host.Packages;
using DaedalusStudio.Projects.Protocol;

namespace DaedalusStudio.Projects.Host.EntityFramework;

/// <summary>The outcome of one <c>dotnet ef</c> run: its exit code, its <c>data:</c> output and its error lines.</summary>
internal sealed record EfRun(int ExitCode, string Data, IReadOnlyList<string> Errors, string Output)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>The first error message, or the output's tail when there is none.</summary>
    public string Failure => Errors.Count > 0
        ? string.Join(Environment.NewLine, Errors)
        : Output.Length > 2000 ? Output[^2000..] : Output;
}

/// <summary>
/// Runs the user's <c>dotnet ef</c> (ADR 0036) in a folder, with <c>--prefix-output</c> so its machine-readable output
/// (<c>data:</c>, from <c>--json</c> or a script) is kept apart from its messages, which stream to <paramref name="output"/>.
/// The process tree is killed when the request is cancelled.
/// </summary>
internal sealed class EfTool(string dotnet, Func<EfOutputParams, Task> output)
{
    private const int PrefixWidth = 9; // "data:    ", "info:    ", "error:   ", "verbose: "

    /// <summary>
    /// One EF process at a time per host (one solution): dotnet-ef copies its build host into the project's output on
    /// every run, and two runs at once collide on those files.
    /// </summary>
    public static SemaphoreSlim Gate { get; } = new(1, 1);

    public async Task<EfRun> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunCoreAsync(workingDirectory, arguments, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<EfRun> RunCoreAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var all = new List<string> { "ef" };
        all.AddRange(arguments);
        all.Add("--prefix-output");
        var psi = DotnetProcess.StartInfo(dotnet, workingDirectory, all);
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        await output(new EfOutputParams("info", "> dotnet " + string.Join(' ', Redact(all)))).ConfigureAwait(false);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("dotnet did not start.");
        var data = new StringBuilder();
        var errors = new List<string>();
        var text = new StringBuilder();
        var stdout = PumpAsync(process.StandardOutput, data, errors, text);
        var stderr = PumpAsync(process.StandardError, data, errors, text);
        await DotnetProcess.WaitOrKillAsync(process, cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return new EfRun(process.ExitCode, data.ToString(), errors, text.ToString());
    }

    /// <summary>
    /// Connection strings (after <c>--connection</c>, and <c>scaffold</c>'s first argument) may hold a password: never echoed.
    /// </summary>
    internal static IEnumerable<string> Redact(IReadOnlyList<string> arguments)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            yield return i > 0 && arguments[i - 1] is "--connection" or "scaffold" ? "\"…\""
                : argument.Contains(' ', StringComparison.Ordinal) ? "\"" + argument + "\"" : argument;
        }
    }

    private async Task PumpAsync(StreamReader reader, StringBuilder data, List<string> errors, StringBuilder text)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            var (level, content) = Split(line);
            lock (text)
            {
                text.AppendLine(content);
                if (level == "data")
                {
                    data.AppendLine(content);
                }
                else if (level == "error")
                {
                    errors.Add(content);
                }
            }

            if (level != "data")
            {
                await output(new EfOutputParams(level, content)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Splits a prefixed line (<c>info:    Build started...</c>) into its level and text; unprefixed lines are info.</summary>
    internal static (string Level, string Text) Split(string line)
    {
        foreach (var level in (string[])["data", "info", "warn", "error", "verbose"])
        {
            if (line.StartsWith(level + ":", StringComparison.Ordinal))
            {
                return (level, line.Length > PrefixWidth ? line[PrefixWidth..] : string.Empty);
            }
        }

        return ("info", line);
    }

    // ------------------------------------------------------------------ the tool itself

    /// <summary>
    /// <c>dotnet ef --version</c> in <paramref name="directory"/>: the version, or null when the command isn't
    /// available (no local or global dotnet-ef, or a manifest that wasn't restored).
    /// </summary>
    public async Task<(string? Version, string Output)> VersionAsync(string directory, CancellationToken cancellationToken)
    {
        var psi = DotnetProcess.StartInfo(dotnet, directory, ["ef", "--version"]);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("dotnet did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await DotnetProcess.WaitOrKillAsync(process, cancellationToken).ConfigureAwait(false);
        var all = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
        var version = process.ExitCode == 0
            ? all.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault(l => l.Length > 0 && char.IsDigit(l[0]))
            : null;
        return (version, all.Trim());
    }

    // ------------------------------------------------------------------ parsing data

    public static T Parse<T>(EfRun run, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        try
        {
            return JsonSerializer.Deserialize(run.Data, info) ?? throw new InvalidOperationException("dotnet ef returned no result.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"dotnet ef returned output that isn't JSON: {ex.Message}");
        }
    }

    /// <summary>The string values of a JSON result's file properties (<c>migrationFile</c>, <c>contextFile</c>, <c>entityTypeFiles</c>…).</summary>
    public static string[] Files(EfRun run)
    {
        if (run.Data.Trim().Length == 0)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(run.Data);
            var files = new List<string>();
            void Collect(JsonElement element)
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.String when element.GetString() is { Length: > 0 } value && (value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".vb", StringComparison.OrdinalIgnoreCase)):
                        files.Add(value);
                        break;
                    case JsonValueKind.Array:
                        foreach (var item in element.EnumerateArray())
                        {
                            Collect(item);
                        }

                        break;
                    case JsonValueKind.Object:
                        foreach (var property in element.EnumerateObject())
                        {
                            Collect(property.Value);
                        }

                        break;
                }
            }

            Collect(document.RootElement);
            return [.. files.Distinct(StringComparer.OrdinalIgnoreCase)];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
