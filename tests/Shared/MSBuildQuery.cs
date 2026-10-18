using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Codalus.Tests;

/// <summary>
/// A project's properties and items as MSBuild gives them to a fixture (<c>dotnet msbuild -getProperty -getItem</c>).
/// The answer is written to a file (<c>-getResultOutputFile</c>) and read from there, so nothing <c>dotnet</c> or MSBuild
/// prints can end up in what's parsed: with <c>-getProperty</c> MSBuild writes its warnings and errors to standard
/// error, and fixtures that parsed standard output and standard error joined failed now and then on a line after the
/// JSON ("'M' is invalid after a single JSON value"). When the command fails, or its answer can't be read, the exception
/// carries the command, its exit code, standard output, standard error and the answer.
/// </summary>
internal sealed class MSBuildQuery
{
    private readonly Dictionary<string, string> _properties;
    private readonly Dictionary<string, IReadOnlyList<string>> _items;

    private MSBuildQuery(Dictionary<string, string> properties, Dictionary<string, IReadOnlyList<string>> items)
    {
        _properties = properties;
        _items = items;
    }

    /// <summary>
    /// Evaluates <paramref name="project"/> (or builds the targets that <paramref name="arguments"/> name, <c>-t:…</c>)
    /// and reads <paramref name="properties"/> and <paramref name="items"/>; <c>dotnet</c> runs in the project's folder,
    /// so a fixture's pinned SDK applies.
    /// </summary>
    public static async Task<MSBuildQuery> RunAsync(
        string project,
        IReadOnlyList<string> properties,
        IReadOnlyList<string>? items = null,
        IReadOnlyList<string>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        items ??= [];
        var resultFile = Path.Combine(Path.GetTempPath(), "codalus-msbuild-" + Guid.NewGuid().ToString("N") + ".txt");
        List<string> command =
        [
            "msbuild", project, "-nologo", "-v:quiet", "-tl:off",
            .. arguments ?? [],
            .. properties.Select(p => "-getProperty:" + p),
            .. items.Select(i => "-getItem:" + i),
            "-getResultOutputFile:" + resultFile,
        ];
        var start = new ProcessStartInfo("dotnet", command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(project))!,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet didn't start.");
            var reading = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            var output = await reading;
            await process.WaitForExitAsync(cancellationToken);
            var answer = File.Exists(resultFile) ? await File.ReadAllTextAsync(resultFile, cancellationToken) : null;
            string Describe(string problem) =>
                $"{problem}\n> dotnet {string.Join(' ', command)}\nExit code: {process.ExitCode}\nStandard output:\n{output}\nStandard error:\n{error}\nAnswer:\n{answer ?? "(no file)"}";

            if (process.ExitCode != 0 || answer is null)
            {
                throw new InvalidOperationException(Describe("MSBuild couldn't answer."));
            }

            try
            {
                return Parse(answer, properties, items);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new InvalidOperationException(Describe("MSBuild's answer couldn't be read: " + ex.Message), ex);
            }
        }
        finally
        {
            File.Delete(resultFile);
        }
    }

    /// <summary>A property's value.</summary>
    public string Property(string name) => _properties[name];

    /// <summary>The identities of an item's elements.</summary>
    public IReadOnlyList<string> Items(string name) => _items[name];

    /// <summary>
    /// MSBuild's answer: one property alone is its value on a line, anything more a JSON object with <c>Properties</c>
    /// and <c>Items</c>, read as exactly one JSON document.
    /// </summary>
    private static MSBuildQuery Parse(string answer, IReadOnlyList<string> properties, IReadOnlyList<string> items)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lists = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        if (properties.Count == 1 && items.Count == 0)
        {
            values[properties[0]] = answer.TrimEnd('\r', '\n');
            return new MSBuildQuery(values, lists);
        }

        using var document = JsonDocument.Parse(answer);
        var root = document.RootElement;
        foreach (var property in properties)
        {
            values[property] = root.GetProperty("Properties").GetProperty(property).GetString()
                ?? throw new InvalidOperationException($"The property {property} has no value.");
        }

        foreach (var item in items)
        {
            lists[item] = [.. root.GetProperty("Items").GetProperty(item).EnumerateArray().Select(i => i.GetProperty("Identity").GetString()!)];
        }

        return new MSBuildQuery(values, lists);
    }
}
