using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DaedalusStudio.Plugins.Templates;

/// <summary>
/// New Project and New Solution from <c>dotnet new</c> templates (ADR 0003/0004: a built-in feature on the public
/// plugin API). The shell is reached only through commands: <c>solution.getInfo</c>, <c>solution.addProjectFile</c>
/// and <c>workbench.openSolution</c>.
/// </summary>
public sealed partial class TemplatesPlugin : IDaedalusPlugin
{
    private IPluginContext? _context;
    private Task<IReadOnlyList<ProjectTemplate>>? _templates;

    private IPluginContext Context => _context!;

    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        context.Subscriptions.Add(context.Commands.Register("templates.newProject", async (_, ct) => await NewProjectAsync(ct).ConfigureAwait(false)));
        context.Subscriptions.Add(context.Commands.Register("templates.newSolution", async (_, ct) => await NewSolutionAsync(ct).ConfigureAwait(false)));
        return Task.CompletedTask;
    }

    private async Task<object?> NewProjectAsync(CancellationToken cancellationToken)
    {
        _templates ??= TemplateCatalog.LoadAsync(Context.Log, CancellationToken.None);
        var templates = await _templates.ConfigureAwait(false);
        var items = templates.Select(t => new QuickPickItem(t.Name) { Description = t.ShortName, Detail = t.Languages, Tag = t }).ToList();
        if (await Context.Window.ShowQuickPickAsync(items, "Select a project template", cancellationToken).ConfigureAwait(false) is not { Tag: ProjectTemplate template })
        {
            return null;
        }

        var solution = await SolutionPathAsync(cancellationToken).ConfigureAwait(false);
        var location = solution is not null ? Path.GetDirectoryName(solution)! : await AskLocationAsync(cancellationToken).ConfigureAwait(false);
        if (location is null || await AskNameAsync("Project name", location, template.DefaultName, cancellationToken).ConfigureAwait(false) is not { } name)
        {
            return null;
        }

        var directory = Path.Combine(location, name);
        var arguments = new List<string> { "new", template.ShortName, "--name", name, "--output", directory };
        if (template.Languages.Contains(',', StringComparison.Ordinal))
        {
            arguments.AddRange(["--language", "C#"]);
        }

        if (!await RunDotnetAsync(arguments, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var project = Directory.EnumerateFiles(directory, "*.*proj").FirstOrDefault();
        if (project is null)
        {
            await Context.Window.ShowNotificationAsync(NotificationSeverity.Warning, $"dotnet new created {directory} but no project file.", cancellationToken: cancellationToken).ConfigureAwait(false);
            return null;
        }

        await Context.Commands.ExecuteAsync(solution is null ? "workbench.openSolution" : "solution.addProjectFile", cancellationToken, project).ConfigureAwait(false);
        return project;
    }

    private async Task<object?> NewSolutionAsync(CancellationToken cancellationToken)
    {
        if (await AskLocationAsync(cancellationToken).ConfigureAwait(false) is not { } location
            || await AskNameAsync("Solution name", location, "Solution1", cancellationToken).ConfigureAwait(false) is not { } name)
        {
            return null;
        }

        var directory = Path.Combine(location, name);
        if (!await RunDotnetAsync(["new", "sln", "--name", name, "--output", directory], cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var solution = Directory.EnumerateFiles(directory, "*.slnx").Concat(Directory.EnumerateFiles(directory, "*.sln")).First();
        await Context.Commands.ExecuteAsync("workbench.openSolution", cancellationToken, solution).ConfigureAwait(false);
        return solution;
    }

    private async Task<string?> SolutionPathAsync(CancellationToken cancellationToken)
    {
        var info = await Context.Commands.ExecuteAsync("solution.getInfo", cancellationToken).ConfigureAwait(false);
        return info is { ValueKind: JsonValueKind.Object } solution && solution.TryGetProperty("path", out var path) && path.GetString() is { Length: > 0 } text
            && solution.TryGetProperty("format", out var format) && format.GetString() != "project"
            ? text
            : null;
    }

    private async Task<string?> AskLocationAsync(CancellationToken cancellationToken)
    {
        var configured = Context.Settings.GetValue("templates.defaultLocation", string.Empty);
        var initial = configured.Length > 0 ? configured : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos");
        var location = await Context.Window.ShowInputBoxAsync(new InputBoxOptions("Location") { Prompt = "The folder to create it in.", Value = initial }, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        location = Path.GetFullPath(Environment.ExpandEnvironmentVariables(location.Trim()));
        Directory.CreateDirectory(location);
        return location;
    }

    /// <summary>Asks for a name until it is valid (or the user cancels).</summary>
    private async Task<string?> AskNameAsync(string title, string location, string suggestion, CancellationToken cancellationToken)
    {
        var value = Unique(location, suggestion);
        string? problem = null;
        while (true)
        {
            var name = await Context.Window.ShowInputBoxAsync(new InputBoxOptions(title) { Prompt = problem ?? $"In {location}", Value = value }, cancellationToken).ConfigureAwait(false);
            if (name is null)
            {
                return null;
            }

            name = name.Trim();
            problem = !ValidName().IsMatch(name) ? "Use letters, digits, '.' and '_', starting with a letter."
                : Directory.Exists(Path.Combine(location, name)) ? $"'{name}' already exists in {location}."
                : null;
            if (problem is null)
            {
                return name;
            }

            value = name;
        }
    }

    private static string Unique(string location, string suggestion)
    {
        var stem = suggestion.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        var name = suggestion;
        for (var i = 2; Directory.Exists(Path.Combine(location, name)); i++)
        {
            name = stem + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return name;
    }

    private async Task<bool> RunDotnetAsync(List<string> arguments, CancellationToken cancellationToken)
    {
        var (exitCode, output) = await Dotnet.RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        Context.Log.Info($"dotnet {string.Join(' ', arguments)} → {exitCode}\n{output}");
        if (exitCode == 0)
        {
            return true;
        }

        await Context.Window.ShowNotificationAsync(NotificationSeverity.Error, $"dotnet {arguments[0]} {arguments[1]} failed: {output.Trim().Split('\n').LastOrDefault()}", cancellationToken: cancellationToken).ConfigureAwait(false);
        return false;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.]*$")]
    private static partial Regex ValidName();
}

/// <summary>A project template from <c>dotnet new list</c>.</summary>
internal sealed record ProjectTemplate(string Name, string ShortName, string Languages)
{
    public string DefaultName => ShortName switch
    {
        "console" => "ConsoleApp1",
        "classlib" => "ClassLibrary1",
        "xunit" or "nunit" or "mstest" => "TestProject1",
        "web" or "webapi" or "mvc" or "webapp" or "blazor" or "grpc" => "WebApplication1",
        "worker" => "WorkerService1",
        _ => "Project1",
    };
}

internal static class TemplateCatalog
{
    /// <summary>Listed first, with readable names (<c>dotnet new list</c> truncates long names).</summary>
    private static readonly (string ShortName, string Name)[] s_known =
    [
        ("console", "Console App"),
        ("classlib", "Class Library"),
        ("xunit", "xUnit Test Project"),
        ("nunit", "NUnit Test Project"),
        ("mstest", "MSTest Test Project"),
        ("worker", "Worker Service"),
        ("web", "ASP.NET Core Empty"),
        ("webapi", "ASP.NET Core Web API"),
        ("webapp", "ASP.NET Core Web App (Razor Pages)"),
        ("mvc", "ASP.NET Core Web App (Model-View-Controller)"),
        ("blazor", "Blazor Web App"),
        ("grpc", "ASP.NET Core gRPC Service"),
        ("razorclasslib", "Razor Class Library"),
        ("wpf", "WPF Application"),
        ("winforms", "Windows Forms App"),
    ];

    public static async Task<IReadOnlyList<ProjectTemplate>> LoadAsync(IPluginLog log, CancellationToken cancellationToken)
    {
        var (exitCode, output) = await Dotnet.RunAsync(["new", "list", "--type", "project", "--language", "C#"], cancellationToken).ConfigureAwait(false);
        var templates = exitCode == 0 ? Parse(output) : [];
        if (templates.Count == 0)
        {
            log.Warning($"dotnet new list failed (exit code {exitCode}); offering the built-in templates only.\n{output}");
            templates = [new("Console App", "console", "C#"), new("Class Library", "classlib", "C#")];
        }

        return templates;
    }

    /// <summary>Parses the table of <c>dotnet new list</c>: column positions come from the dashed header rule, so the parse works in any UI language.</summary>
    internal static IReadOnlyList<ProjectTemplate> Parse(string output)
    {
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var rule = Array.FindIndex(lines, l => l.Length > 0 && l.Trim().Length > 0 && l.Trim().All(c => c is '-' or ' ') && l.Contains("--", StringComparison.Ordinal));
        if (rule < 0)
        {
            return [];
        }

        var columns = new List<(int Start, int End)>();
        for (var i = 0; i < lines[rule].Length; i++)
        {
            if (lines[rule][i] == '-' && (i == 0 || lines[rule][i - 1] == ' '))
            {
                var end = lines[rule].IndexOf(' ', i);
                columns.Add((i, end < 0 ? int.MaxValue : end));
            }
        }

        if (columns.Count < 3)
        {
            return [];
        }

        string Cell(string line, int column)
        {
            var (start, end) = columns[column];
            var next = column + 1 < columns.Count ? columns[column + 1].Start : line.Length;
            return start >= line.Length ? string.Empty : line[start..Math.Min(Math.Max(end, next), line.Length)].Trim();
        }

        var parsed = new List<ProjectTemplate>();
        foreach (var line in lines.Skip(rule + 1).Where(l => l.Trim().Length > 0))
        {
            var shortName = Cell(line, 1).Split(',')[0].Trim();
            if (shortName.Length == 0)
            {
                continue;
            }

            var known = s_known.FirstOrDefault(k => k.ShortName == shortName).Name;
            parsed.Add(new ProjectTemplate(known ?? Cell(line, 0).TrimEnd('.'), shortName, Cell(line, 2).Replace("[", string.Empty, StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal)));
        }

        return [.. parsed
            .DistinctBy(t => t.ShortName)
            .OrderBy(t => Array.FindIndex(s_known, k => k.ShortName == t.ShortName) is var i and >= 0 ? i : int.MaxValue)
            .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];
    }
}

/// <summary>Runs the <c>dotnet</c> CLI of the runtime this host runs on.</summary>
internal static class Dotnet
{
    public static async Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(Executable())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        try
        {
            using var process = Process.Start(psi)!;
            var output = new StringBuilder();
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            output.Append(await stdout.ConfigureAwait(false)).Append(await stderr.ConfigureAwait(false));
            return (process.ExitCode, output.ToString());
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary><c>&lt;root&gt;/shared/Microsoft.NETCore.App/&lt;version&gt;/</c> → <c>&lt;root&gt;/dotnet</c>; else <c>dotnet</c> on PATH.</summary>
    private static string Executable()
    {
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var root = runtime is null ? null : Directory.GetParent(runtime)?.Parent?.Parent?.FullName;
        return root is not null && File.Exists(Path.Combine(root, exe)) ? Path.Combine(root, exe) : exe;
    }
}
