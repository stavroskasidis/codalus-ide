using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Codalus.Workbench.Projects;

/// <summary>An installed template package (<c>dotnet new uninstall</c> lists them): a NuGet package or a folder.</summary>
public sealed record TemplatePackage(string Id, string? Version, string? Author, string? Source, IReadOnlyList<string> Templates);

/// <summary>A template package with a newer version (<c>dotnet new update --check-only</c>).</summary>
public sealed record TemplatePackageUpdate(string Id, string Current, string Latest);

/// <summary>
/// Export Template (ADR 0033): a project (<see cref="Kind"/> <c>project</c>, <see cref="Source"/> its project file) or items
/// (<c>item</c>, <see cref="Source"/> the first file, <see cref="ItemFiles"/> all of them) made into a <c>dotnet new</c>
/// template in <see cref="Destination"/>.
/// </summary>
public sealed record TemplateExportRequest(
    string Kind,
    string Source,
    IReadOnlyList<string> ItemFiles,
    string Name,
    string ShortName,
    string Description,
    string Author,
    string Language,
    string Destination);

/// <summary>
/// <c>dotnet new</c>'s templates from the workbench (ADR 0033): the item templates for Add New Item, the installed template
/// packages and their updates, install and uninstall, and Export Template. It runs the <c>dotnet</c> CLI in a neutral
/// folder: listing and installing run no workspace code. Instantiating a template into a project is the build host's
/// (<c>templates/instantiate</c>). With a hive (<c>CODALUS_TEMPLATE_HIVE</c>, tests) the CLI uses its own settings folder.
/// It is also New Project's CLI (<see cref="ITemplateCli"/>, ADR 0082): listing templates and creating projects run here,
/// in the same neutral folder, so both use the same SDK.
/// </summary>
public sealed partial class TemplateService : ITemplateCli
{
    public const string HiveVariable = "CODALUS_TEMPLATE_HIVE";

    private static readonly string[] s_skippedNames = ["bin", "obj", ".vs", ".codalus", ".git", ".idea", "node_modules", "TestResults"];

    private readonly string? _hive;
    private readonly string _workingDirectory;
    private readonly Func<string> _dotnet;
    private Task<IReadOnlyList<ItemTemplate>>? _itemTemplates;

    public TemplateService(string? hive = null, string? workingDirectory = null, Func<string>? dotnet = null)
    {
        _hive = hive ?? (Environment.GetEnvironmentVariable(HiveVariable) is { Length: > 0 } fromEnv ? fromEnv : null);
        _workingDirectory = workingDirectory ?? Path.GetTempPath();
        _dotnet = dotnet ?? (() => BuildHostRuntime.DotnetExecutable() ?? "dotnet");
    }

    /// <summary>The installed template packages changed (install, uninstall, update, export).</summary>
    public event EventHandler? Changed;

    /// <inheritdoc/>
    public string? Hive => _hive;

    /// <summary>The folder every command runs in (the shell's state folder): no workspace's <c>global.json</c> applies there.</summary>
    public string WorkingDirectory => Directory.Exists(_workingDirectory) ? _workingDirectory : Path.GetTempPath();

    /// <inheritdoc/>
    async Task<CliResult> ITemplateCli.RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var (exitCode, output) = await RunAsync(args, cancellationToken).ConfigureAwait(false);
        return new CliResult(exitCode, output);
    }

    // ------------------------------------------------------------------ item templates

    /// <summary>The <c>dotnet new</c> item templates, listed once (until a package is installed or removed).</summary>
    public Task<IReadOnlyList<ItemTemplate>> ItemTemplatesAsync(CancellationToken cancellationToken)
    {
        var templates = _itemTemplates;
        if (templates is null || templates.IsFaulted || templates.IsCanceled)
        {
            _itemTemplates = templates = LoadItemTemplatesAsync(cancellationToken);
        }

        return templates;
    }

    private async Task<IReadOnlyList<ItemTemplate>> LoadItemTemplatesAsync(CancellationToken cancellationToken)
    {
        var (exitCode, output) = await RunAsync(["new", "list", "--type", "item", "--ignore-constraints"], cancellationToken).ConfigureAwait(false);
        return exitCode == 0 ? ParseList(output) : [];
    }

    /// <summary>
    /// The table of <c>dotnet new list</c>: columns from the dashed header rule (so any width works). Each short name
    /// (the first of a comma-separated list) becomes a template; the languages lose their default marker brackets.
    /// </summary>
    public static IReadOnlyList<ItemTemplate> ParseList(string output)
    {
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var rule = Array.FindIndex(lines, l => l.Trim().Length > 0 && l.Trim().All(c => c is '-' or ' ') && l.Contains("--", StringComparison.Ordinal));
        if (rule < 0)
        {
            return [];
        }

        var columns = new List<int>();
        for (var i = 0; i < lines[rule].Length; i++)
        {
            if (lines[rule][i] == '-' && (i == 0 || lines[rule][i - 1] == ' '))
            {
                columns.Add(i);
            }
        }

        if (columns.Count < 4)
        {
            return [];
        }

        string Cell(string line, int column)
        {
            var start = columns[column];
            var end = column + 1 < columns.Count ? columns[column + 1] : line.Length;
            return start >= line.Length ? string.Empty : line[start..Math.Min(end, line.Length)].Trim();
        }

        var templates = new List<ItemTemplate>();
        foreach (var line in lines.Skip(rule + 1).Where(l => l.Trim().Length > 0))
        {
            var shortName = Cell(line, 1).Split(',')[0].Trim();
            if (shortName.Length == 0)
            {
                continue;
            }

            var name = Cell(line, 0);
            var languages = Cell(line, 2).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Trim('[', ']')).ToArray();
            var tags = Cell(line, 3).Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            templates.Add(new ItemTemplate(name, shortName, Category(tags), name, DefaultName(shortName, name), string.Empty)
            {
                Source = ItemTemplateSource.DotnetNew,
                Languages = languages,
                Tags = tags,
            });
        }

        return [.. templates.DistinctBy(t => t.ShortName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>A category for Add New Item's list from <c>dotnet new</c>'s tags.</summary>
    private static string Category(string[] tags) =>
        tags.FirstOrDefault(t => t is "Web" or "Config" or "Test" or "MSBuild" or "MAUI" or "Common") switch
        {
            "Common" => "Code",
            "Config" or "MSBuild" => "Configuration",
            { } known => known,
            null => tags.Length > 0 ? tags[0] : "General",
        };

    private static string DefaultName(string shortName, string name)
    {
        var words = DefaultNameWords().Matches(name).Select(m => m.Value);
        var stem = string.Concat(words.Select(w => w.Length > 2 && w.All(char.IsUpper) ? w[0] + w[1..].ToLowerInvariant() : char.ToUpperInvariant(w[0]) + w[1..]));
        return stem.Length == 0 ? shortName : stem + "1";
    }

    [GeneratedRegex("[A-Za-z][A-Za-z0-9]*")]
    private static partial Regex DefaultNameWords();

    // ------------------------------------------------------------------ packages

    public async Task<IReadOnlyList<TemplatePackage>> PackagesAsync(CancellationToken cancellationToken)
    {
        var (_, output) = await RunAsync(["new", "uninstall"], cancellationToken).ConfigureAwait(false);
        return ParsePackages(output);
    }

    /// <summary>
    /// <c>dotnet new uninstall</c>'s list: a package per line indented by three spaces, its fields (<c>Version:</c>,
    /// <c>Details:</c> with <c>Author:</c> and <c>NuGetSource:</c>, <c>Templates:</c>) below it.
    /// </summary>
    public static IReadOnlyList<TemplatePackage> ParsePackages(string output)
    {
        var packages = new List<TemplatePackage>();
        string? id = null, version = null, author = null, source = null;
        var templates = new List<string>();
        var section = string.Empty;
        void Flush()
        {
            if (id is not null)
            {
                packages.Add(new TemplatePackage(id, version, author, source, [.. templates]));
            }

            (id, version, author, source, section) = (null, null, null, null, string.Empty);
            templates = [];
        }

        foreach (var raw in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var indent = raw.Length - raw.TrimStart(' ').Length;
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (indent is > 0 and <= 4)
            {
                Flush();
                id = line;
                continue;
            }

            if (id is null)
            {
                continue;
            }

            if (indent <= 7 && line.EndsWith(':'))
            {
                section = line[..^1];
            }
            else if (indent <= 7 && line.StartsWith("Version:", StringComparison.Ordinal))
            {
                version = line["Version:".Length..].Trim();
            }
            else if (section == "Details" && line.StartsWith("Author:", StringComparison.Ordinal))
            {
                author = line["Author:".Length..].Trim();
            }
            else if (section == "Details" && line.StartsWith("NuGetSource:", StringComparison.Ordinal))
            {
                source = line["NuGetSource:".Length..].Trim();
            }
            else if (section == "Templates")
            {
                templates.Add(line);
            }
        }

        Flush();
        return packages;
    }

    /// <summary>Installs a package (<c>id</c>, <c>id::version</c>, a <c>.nupkg</c> or a template folder).</summary>
    public Task<(bool Succeeded, string Output)> InstallAsync(string package, CancellationToken cancellationToken) =>
        ChangeAsync(["new", "install", package.Trim(), "--force"], cancellationToken);

    public Task<(bool Succeeded, string Output)> UninstallAsync(string package, CancellationToken cancellationToken) =>
        ChangeAsync(["new", "uninstall", package.Trim()], cancellationToken);

    /// <summary>Updates every installed package that has a newer version.</summary>
    public Task<(bool Succeeded, string Output)> UpdateAllAsync(CancellationToken cancellationToken) =>
        ChangeAsync(["new", "update"], cancellationToken);

    public async Task<IReadOnlyList<TemplatePackageUpdate>> CheckUpdatesAsync(CancellationToken cancellationToken)
    {
        var (_, output) = await RunAsync(["new", "update", "--check-only"], cancellationToken).ConfigureAwait(false);
        return ParseUpdates(output);
    }

    /// <summary>
    /// <c>dotnet new update --check-only</c>: a <c>Package Current Latest</c> table under a dashed rule (empty when every
    /// package is up to date).
    /// </summary>
    public static IReadOnlyList<TemplatePackageUpdate> ParseUpdates(string output)
    {
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var updates = new List<TemplatePackageUpdate>();
        for (var i = 1; i < lines.Length; i++)
        {
            var rule = lines[i].Trim();
            if (rule.Length == 0 || !rule.All(c => c is '-' or ' ') || !lines[i - 1].TrimStart().StartsWith("Package", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var row in lines.Skip(i + 1).TakeWhile(l => l.Trim().Length > 0))
            {
                if (row.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var id, var current, var latest, ..])
                {
                    updates.Add(new TemplatePackageUpdate(id, current, latest));
                }
            }
        }

        return updates;
    }

    private async Task<(bool Succeeded, string Output)> ChangeAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var (exitCode, output) = await RunAsync(args, cancellationToken).ConfigureAwait(false);
        _itemTemplates = null;
        Changed?.Invoke(this, EventArgs.Empty);
        return (exitCode == 0, output);
    }

    // ------------------------------------------------------------------ export

    /// <summary>
    /// Export Template: copies the project (without build outputs, IDE state and user files) or the items into
    /// <see cref="TemplateExportRequest.Destination"/>, writes <c>.template.config/template.json</c>, and installs it.
    /// </summary>
    public async Task<(bool Succeeded, string Output)> ExportAsync(TemplateExportRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || !ShortNamePattern().IsMatch(request.ShortName))
        {
            throw new ArgumentException("A template needs a name and a short name of letters, digits, '.', '-' and '_'.");
        }

        if (Directory.Exists(request.Destination))
        {
            Directory.Delete(request.Destination, recursive: true);
        }

        Directory.CreateDirectory(request.Destination);
        var template = WriteTemplate(request);
        var configuration = Path.Combine(request.Destination, ".template.config");
        Directory.CreateDirectory(configuration);
        await File.WriteAllTextAsync(Path.Combine(configuration, "template.json"), template.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", cancellationToken).ConfigureAwait(false);
        return await InstallAsync(request.Destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Copies the sources into the destination and returns <c>template.json</c>.</summary>
    internal static JsonObject WriteTemplate(TemplateExportRequest request)
    {
        var isProject = request.Kind == "project";
        string sourceName;
        var symbols = new JsonObject();
        if (isProject)
        {
            sourceName = Path.GetFileNameWithoutExtension(request.Source);
            CopyFolder(Path.GetDirectoryName(request.Source)!, request.Destination);
        }
        else
        {
            sourceName = Path.GetFileNameWithoutExtension(request.Source);
            string? ns = null;
            foreach (var file in request.ItemFiles.Count > 0 ? request.ItemFiles : [request.Source])
            {
                var target = Path.Combine(request.Destination, Path.GetFileName(file));
                File.Copy(file, target, overwrite: true);
                ns ??= NamespaceDeclaration().Match(File.ReadAllText(file)) is { Success: true } match ? match.Groups["ns"].Value : null;
            }

            if (ns is not null)
            {
                // The namespace of the project the item is added to (with --project), else the original one.
                symbols["namespace"] = new JsonObject
                {
                    ["type"] = "bind",
                    ["binding"] = "msbuild:RootNamespace",
                    ["replaces"] = ns,
                    ["defaultValue"] = ns,
                };
            }
        }

        var template = new JsonObject
        {
            ["$schema"] = "http://json.schemastore.org/template",
            ["author"] = request.Author,
            ["classifications"] = new JsonArray("Custom", "Exported"),
            ["identity"] = "Codalus.Exported." + request.ShortName,
            ["name"] = request.Name,
            ["shortName"] = request.ShortName,
            ["description"] = request.Description,
            ["sourceName"] = sourceName,
            ["tags"] = new JsonObject { ["language"] = request.Language, ["type"] = isProject ? "project" : "item" },
        };
        if (isProject)
        {
            template["preferNameDirectory"] = true;
        }

        if (symbols.Count > 0)
        {
            template["symbols"] = symbols;
        }

        return template;
    }

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source).Where(f => !f.EndsWith(".user", StringComparison.OrdinalIgnoreCase)))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source).Where(d => !s_skippedNames.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)))
        {
            CopyFolder(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    [GeneratedRegex(@"^\s*namespace\s+(?<ns>[A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.Multiline)]
    private static partial Regex NamespaceDeclaration();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]*$")]
    private static partial Regex ShortNamePattern();

    // ------------------------------------------------------------------ the CLI

    /// <summary>
    /// Runs <c>dotnet &lt;args&gt;</c> in <see cref="WorkingDirectory"/>, in English. The hive goes on <c>dotnet new</c>
    /// commands only, once: this is its only owner (callers never pass <c>--debug:custom-hive</c>).
    /// </summary>
    private async Task<(int ExitCode, string Output)> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_dotnet())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = WorkingDirectory,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        if (_hive is not null && args.Count > 0 && args[0] == "new")
        {
            start.ArgumentList.Add("--debug:custom-hive");
            start.ArgumentList.Add(_hive);
        }

        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            return (process.ExitCode, (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false)));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
