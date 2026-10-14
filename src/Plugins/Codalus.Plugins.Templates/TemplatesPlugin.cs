using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Codalus.Plugins.Templates;

/// <summary>
/// New Project and New Solution from <c>dotnet new</c> templates (ADR 0003/0004: a built-in feature on the public
/// plugin API). The shell is reached only through commands: <c>solution.getInfo</c>, <c>solution.addProjectFile</c>
/// and <c>workbench.openSolution</c>.
/// </summary>
public sealed partial class TemplatesPlugin : ICodalusPlugin
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

    /// <summary>
    /// Template packages offered in New Project while none of their templates is installed: Avalonia's (M19b) and
    /// CoreWCF's services (M42). Each is the short names' prefix, the package and what the offer says.
    /// </summary>
    private static readonly (string Prefix, string Package, string Label)[] s_offeredPackages =
    [
        ("avalonia", "Avalonia.Templates", "Install the Avalonia templates…"),
        ("corewcf", "CoreWCF.Templates", "Install the CoreWCF service templates…"),
    ];

    /// <summary>The tag of an offer to install a template package.</summary>
    private sealed record InstallOffer(string Package);

    private async Task<object?> NewProjectAsync(CancellationToken cancellationToken)
    {
        _templates ??= TemplateCatalog.LoadAsync(Context.Log, CancellationToken.None);
        var templates = await _templates.ConfigureAwait(false);
        var items = templates.Select(t => new QuickPickItem(t.Name) { Description = t.ShortName, Detail = t.Languages, Tag = t }).ToList();
        // Template packages that aren't installed: dotnet new install, then the list again.
        foreach (var (prefix, package, label) in s_offeredPackages)
        {
            if (!templates.Any(t => t.ShortName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                items.Add(new QuickPickItem(label) { Description = package, Detail = "dotnet new install " + package, Tag = new InstallOffer(package) });
            }
        }

        var picked = await Context.Window.ShowQuickPickAsync(items, "Select a project template", cancellationToken).ConfigureAwait(false);
        if (picked is { Tag: InstallOffer offer })
        {
            if (!await RunDotnetAsync(["new", "install", offer.Package], cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            _templates = null;
            return await NewProjectAsync(cancellationToken).ConfigureAwait(false);
        }

        if (picked is not { Tag: ProjectTemplate template })
        {
            return null;
        }

        var solution = await SolutionPathAsync(cancellationToken).ConfigureAwait(false);
        var location = solution is not null ? Path.GetDirectoryName(solution)! : await AskLocationAsync(cancellationToken).ConfigureAwait(false);
        if (location is null || await AskNameAsync("Project name", location, template.DefaultName, cancellationToken).ConfigureAwait(false) is not { } name)
        {
            return null;
        }

        var (framework, canceled) = await AskFrameworkAsync(template, location, cancellationToken).ConfigureAwait(false);
        if (canceled)
        {
            return null;
        }

        var directory = Path.Combine(location, name);
        var arguments = new List<string> { "new", template.ShortName, "--name", name, "--output", directory };
        if (template.Languages.Contains(',', StringComparison.Ordinal))
        {
            arguments.AddRange(["--language", "C#"]);
        }

        if (framework is not null)
        {
            arguments.AddRange(["--framework", framework]);
        }

        // In the location, so a global.json there picks the SDK, as on the command line.
        if (!await RunDotnetAsync(arguments, cancellationToken, location).ConfigureAwait(false))
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

    /// <summary>
    /// The template's target framework, when it offers a choice (<c>--framework</c>): the frameworks of the SDK that
    /// the location resolves to, the template's default first. Null without a choice; canceled when dismissed.
    /// </summary>
    private async Task<(string? Framework, bool Canceled)> AskFrameworkAsync(ProjectTemplate template, string location, CancellationToken cancellationToken)
    {
        var (exitCode, help) = await Dotnet.RunAsync(["new", template.ShortName, "--help"], cancellationToken, location, english: true).ConfigureAwait(false);
        var frameworks = exitCode == 0 ? TemplateFrameworks.Parse(help) : [];
        if (frameworks.Count < 2)
        {
            return (null, false);
        }

        var items = frameworks.Select(f => new QuickPickItem(TemplateFrameworks.Display(f.Framework)) { Description = f.Framework, Detail = f.IsDefault ? "Default" : null, Tag = f.Framework }).ToList();
        return await Context.Window.ShowQuickPickAsync(items, "Target framework", cancellationToken).ConfigureAwait(false) is { Tag: string framework }
            ? (framework, false)
            : (null, true);
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

    private async Task<bool> RunDotnetAsync(List<string> arguments, CancellationToken cancellationToken, string? workingDirectory = null)
    {
        var (exitCode, output) = await Dotnet.RunAsync(arguments, cancellationToken, workingDirectory).ConfigureAwait(false);
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
    /// <param name="english">The CLI's messages in English, for output that is parsed by its words.</param>
    public static async Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string? workingDirectory = null, bool english = false)
    {
        var psi = new ProcessStartInfo(Executable())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null && Directory.Exists(workingDirectory))
        {
            psi.WorkingDirectory = workingDirectory;
        }

        if (english)
        {
            psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        }

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

/// <summary>A template's target frameworks, from <c>dotnet new &lt;template&gt; --help</c> (in English).</summary>
internal static partial class TemplateFrameworks
{
    /// <summary>
    /// The choices of <c>-f, --framework</c>: the default first, then the newest .NET first, then .NET Standard. Empty
    /// when the template has no framework option. The choices are listed under the option ("Type: choice"); a wide
    /// console also puts them in the option's placeholder (<c>&lt;net10.0|net11.0&gt;</c>), a narrow one says
    /// <c>&lt;choice&gt;</c>.
    /// </summary>
    public static IReadOnlyList<(string Framework, bool IsDefault)> Parse(string help)
    {
        var lines = help.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var option = Array.FindIndex(lines, l => FrameworkOption().IsMatch(l));
        if (option < 0)
        {
            return [];
        }

        var block = lines.Skip(option + 1).TakeWhile(l => !l.TrimStart().StartsWith('-')).ToList();
        var listed = block
            .SkipWhile(l => !l.Trim().Equals("Type: choice", StringComparison.Ordinal))
            .Skip(1)
            .TakeWhile(l => !DefaultValue().IsMatch(l))
            .Select(l => l.Trim().Split(' ', 2)[0])
            .Where(c => c.Length > 0)
            .ToList();
        var choices = FrameworkOption().Match(lines[option]).Groups[1].Value
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => c != "choice")
            .Union(listed, StringComparer.Ordinal);
        var @default = block.Select(l => DefaultValue().Match(l)).FirstOrDefault(m => m.Success)?.Groups[1].Value;
        return [.. choices
            .OrderByDescending(c => c == @default)
            .ThenBy(c => c.StartsWith("netstandard", StringComparison.Ordinal))
            .ThenByDescending(c => Version.TryParse(VersionOf(c), out var v) ? v : new Version())
            .Select(c => (c, c == @default))];
    }

    /// <summary><c>net11.0</c> → ".NET 11.0", <c>netstandard2.1</c> → ".NET Standard 2.1".</summary>
    public static string Display(string framework) =>
        framework.StartsWith("netstandard", StringComparison.Ordinal) ? ".NET Standard " + VersionOf(framework)
        : framework.StartsWith("netcoreapp", StringComparison.Ordinal) ? ".NET Core " + VersionOf(framework)
        : framework.StartsWith("net", StringComparison.Ordinal) && Version.TryParse(VersionOf(framework), out _) ? ".NET " + VersionOf(framework)
        : framework;

    private static string VersionOf(string framework) => framework.TrimStart("netstandardcorep".ToCharArray());

    [GeneratedRegex(@"--framework <([^>]+)>")]
    private static partial Regex FrameworkOption();

    [GeneratedRegex(@"^\s*Default:\s*(\S+)")]
    private static partial Regex DefaultValue();
}
