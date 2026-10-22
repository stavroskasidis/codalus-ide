using System.IO.Compression;
using System.Reflection;
using System.Text;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The recorded template data (TestData/Templates, README there) in a temp folder: the packages zipped into
/// <c>.nupkg</c> files, and the caches pointing at them.
/// </summary>
internal sealed class TemplateTestData : IDisposable
{
    public TemplateTestData(bool bom = false)
    {
        Root = Path.Combine(Path.GetTempPath(), "codalus-template-data-" + Guid.NewGuid().ToString("N"));
        Packages = Path.Combine(Root, "packages");
        Directory.CreateDirectory(Packages);
        foreach (var folder in Directory.EnumerateDirectories(Path.Combine(Source, "Packages")))
        {
            ZipFile.CreateFromDirectory(folder, Path.Combine(Packages, Path.GetFileName(folder) + ".nupkg"));
        }

        Cache11 = WriteCache("templatecache-11.0.json", bom);
        Cache10 = WriteCache("templatecache-10.0.json", bom);
    }

    public static string Source => Path.Combine(RepoRoot, "tests", "Codalus.Workbench.Tests", "TestData", "Templates");

    /// <summary>The repository (the test assembly's metadata: the shell's tests link this file too).</summary>
    private static string RepoRoot => typeof(TemplateTestData).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "CodalusRepoRoot").Value!;

    public string Root { get; }

    public string Packages { get; }

    public string Cache11 { get; }

    public string Cache10 { get; }

    public static string Text(string name) => File.ReadAllText(Path.Combine(Source, name));

    public string Package(string name) => Path.Combine(Packages, name + ".nupkg");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string WriteCache(string name, bool bom)
    {
        var text = Text(name).Replace("{packages}", Packages.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal);
        var path = Path.Combine(Root, name);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: bom));
        return path;
    }
}

/// <summary>A stand-in <c>dotnet</c> CLI: recorded answers by command line, and every call kept.</summary>
internal sealed class RecordedCli : ITemplateCli
{
    private readonly Lock _gate = new();

    public Dictionary<string, CliResult> Answers { get; } = new(StringComparer.Ordinal);

    public List<IReadOnlyList<string>> Calls { get; } = [];

    public List<string> Installed { get; } = [];

    /// <summary>Answers for commands with no recorded answer (default: exit code 1).</summary>
    public Func<IReadOnlyList<string>, CliResult>? Fallback { get; set; }

    public string? Hive { get; set; }

    public event EventHandler? Changed;

    /// <summary>The recorded list and SDK version, as the 11.0 SDK answers them.</summary>
    public static RecordedCli Sdk11()
    {
        var cli = new RecordedCli();
        cli.Answers["new list --type project"] = new CliResult(0, TemplateTestData.Text("list-project.txt"));
        cli.Answers["new list --type project --columns-all"] = new CliResult(0, TemplateTestData.Text("list-project-columns-all.txt"));
        cli.Answers["--version"] = new CliResult(0, "11.0.100-rc.2.26504.105\n");
        cli.Answers["new blazor --language C# --help"] = new CliResult(0, TemplateTestData.Text("blazor-help.txt"));
        return cli;
    }

    public Task<CliResult> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Calls.Add([.. args]);
        }

        return Task.FromResult(Answers.TryGetValue(string.Join(' ', args), out var answer) ? answer : Fallback?.Invoke(args) ?? new CliResult(1, "No recorded answer for: " + string.Join(' ', args)));
    }

    public Task<(bool Succeeded, string Output)> InstallAsync(string package, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Installed.Add(package);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult((true, "Success: " + package + " installed."));
    }
}

/// <summary>A cache locator that finds the given file (or nothing).</summary>
internal sealed class FixedCacheLocator(string? file) : TemplateCacheLocator
{
    public override string? Find(string? hive, string? sdkVersion) => file;
}
