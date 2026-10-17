using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Codalus.Projects.Host.EntityFramework;
using Codalus.Projects.Host.Packages;
using Codalus.Projects.Protocol;
using Codalus.Rpc;

namespace Codalus.Projects.Host;

/// <summary>
/// EF Core (ADR 0036): the user's <c>dotnet ef</c> and the model agent, run as child processes in the startup project's
/// folder, only in trusted solutions.
/// </summary>
internal sealed partial class SolutionSession
{
    /// <summary>Points at another <c>codalus-efagent.dll</c> (tests); otherwise <c>efagent/</c> next to the host.</summary>
    public const string EfAgentVariable = "CODALUS_EFAGENT";

    /// <summary>EF Core provider packages, for scaffolding's provider choice and the status.</summary>
    internal static readonly string[] EfProviderPackages =
    [
        "Microsoft.EntityFrameworkCore.SqlServer",
        "Microsoft.EntityFrameworkCore.Sqlite",
        "Npgsql.EntityFrameworkCore.PostgreSQL",
        "Pomelo.EntityFrameworkCore.MySql",
        "MySql.EntityFrameworkCore",
        "Oracle.EntityFrameworkCore",
        "Microsoft.EntityFrameworkCore.Cosmos",
        "Microsoft.EntityFrameworkCore.InMemory",
    ];

    private EfTool Ef => new(_dotnet, _events.EfOutputAsync);

    public async Task<EfStatusResult> EfStatusAsync(EfStatusParams parameters, CancellationToken cancellationToken)
    {
        RequireTrusted("Entity Framework");
        var project = System.IO.Path.GetFullPath(parameters.Project);
        var startup = System.IO.Path.GetFullPath(parameters.StartupProject ?? parameters.Project);
        var directory = System.IO.Path.GetDirectoryName(startup)!;
        var (version, output) = await Ef.VersionAsync(directory, cancellationToken).ConfigureAwait(false);
        var manifest = DotnetTools.FindManifest(directory);
        var listed = manifest is not null && DotnetTools.ReadManifest(manifest).Any(t => t.Id.Equals("dotnet-ef", StringComparison.OrdinalIgnoreCase));
        var packages = PackageReferencesOf(project).Concat(PackageReferencesOf(startup)).ToList();
        var efVersion = packages.FirstOrDefault(r => r.Name.Equals("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase))?.Version
                        ?? packages.FirstOrDefault(r => r.Name.StartsWith("Microsoft.EntityFrameworkCore.", StringComparison.OrdinalIgnoreCase))?.Version;
        var providers = EfProviderPackages.Where(p => packages.Any(r => r.Name.Equals(p, StringComparison.OrdinalIgnoreCase))).ToArray();
        var design = PackageReferencesOf(startup).Any(r => r.Name is "Microsoft.EntityFrameworkCore.Design" or "Microsoft.EntityFrameworkCore.Tools");
        string? message = version is not null ? null
            : listed ? "dotnet-ef is in the tool manifest but isn't restored. Restore the .NET tools (dotnet tool restore)."
            : "dotnet-ef isn't installed. Install it as a local tool (dotnet tool install dotnet-ef) or globally.";
        if (version is null)
        {
            await _events.LogAsync("info", "dotnet ef --version: " + output).ConfigureAwait(false);
        }

        return new EfStatusResult(version, version is null ? null : listed ? "local" : "global", manifest, listed, efVersion, providers, design, message);
    }

    private IEnumerable<ReferenceInfo> PackageReferencesOf(string project) =>
        _evaluated.TryGetValue(project, out var evaluated) ? evaluated.Info.References.Where(r => r.Kind == ReferenceKinds.Package) : [];

    public async Task<EfContextsResult> EfContextsAsync(EfTarget target, CancellationToken cancellationToken)
    {
        var run = await RunEfAsync(target with { Context = null, Connection = null }, ["dbcontext", "list", "--json"], cancellationToken).ConfigureAwait(false);
        return new EfContextsResult(EfTool.Parse(run, EntityFrameworkJsonContext.Default.EfContextItemArray));
    }

    public async Task<EfContextInfoResult> EfContextInfoAsync(EfTarget target, CancellationToken cancellationToken)
    {
        var run = await RunEfAsync(target, ["dbcontext", "info", "--json"], cancellationToken, connection: true).ConfigureAwait(false);
        return EfTool.Parse(run, EntityFrameworkJsonContext.Default.EfContextInfoResult);
    }

    /// <summary>
    /// The migrations and whether each is applied. When the database can't be reached the list is read again without
    /// connecting (EF Core 7+), and the error says why.
    /// </summary>
    public async Task<EfMigrationsResult> EfMigrationsAsync(EfTarget target, CancellationToken cancellationToken)
    {
        var (directory, arguments) = EfArguments(target, connection: true, "migrations", "list", "--json");
        var run = await Ef.RunAsync(directory, arguments, cancellationToken).ConfigureAwait(false);
        string? connectionError = null;
        if (!run.Succeeded || run.Data.Trim().Length == 0)
        {
            ThrowIfToolMissing(run);
            connectionError = run.Failure;
            var (_, offline) = EfArguments(target with { Connection = null }, connection: false, "migrations", "list", "--json", "--no-connect");
            run = await Ef.RunAsync(directory, offline, cancellationToken).ConfigureAwait(false);
            Check(run);
        }
        else if (run.Output.Contains("An error occurred while accessing the database", StringComparison.Ordinal))
        {
            connectionError = run.Output.Split('\n').FirstOrDefault(l => l.Contains("An error occurred", StringComparison.Ordinal))?.Trim();
        }

        return new EfMigrationsResult(EfTool.Parse(run, EntityFrameworkJsonContext.Default.EfMigrationItemArray), connectionError);
    }

    public async Task<EfFilesResult> EfAddMigrationAsync(EfAddMigrationParams parameters, CancellationToken cancellationToken)
    {
        if (!IsIdentifier(parameters.Name))
        {
            throw new ArgumentException($"'{parameters.Name}' isn't a valid migration name: use a C# identifier.");
        }

        var command = new List<string> { "migrations", "add", parameters.Name, "--json" };
        AddOption(command, "--output-dir", parameters.OutputDir);
        AddOption(command, "--namespace", parameters.Namespace);
        var run = await RunEfAsync(parameters.Target with { Connection = null }, [.. command], cancellationToken).ConfigureAwait(false);
        return new EfFilesResult(EfTool.Files(run));
    }

    public async Task<EfFilesResult> EfRemoveMigrationAsync(EfRemoveMigrationParams parameters, CancellationToken cancellationToken)
    {
        string[] command = parameters.Force ? ["migrations", "remove", "--json", "--force"] : ["migrations", "remove", "--json"];
        var run = await RunEfAsync(parameters.Target, command, cancellationToken, connection: parameters.Force).ConfigureAwait(false);
        return new EfFilesResult(EfTool.Files(run));
    }

    /// <summary>The script, written by <c>dotnet ef</c> to a temporary file and read back.</summary>
    public async Task<EfScriptResult> EfScriptAsync(EfScriptParams parameters, CancellationToken cancellationToken)
    {
        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codalus-ef-" + Guid.NewGuid().ToString("N") + ".sql");
        var command = new List<string> { "migrations", "script" };
        if (parameters.From is { Length: > 0 } || parameters.To is { Length: > 0 })
        {
            command.Add(parameters.From is { Length: > 0 } from ? from : "0");
            if (parameters.To is { Length: > 0 } to)
            {
                command.Add(to);
            }
        }

        command.AddRange(["--output", file]);
        if (parameters.Idempotent)
        {
            command.Add("--idempotent");
        }

        if (parameters.NoTransactions)
        {
            command.Add("--no-transactions");
        }

        try
        {
            await RunEfAsync(parameters.Target with { Connection = null }, [.. command], cancellationToken).ConfigureAwait(false);
            return new EfScriptResult(File.Exists(file) ? await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false) : string.Empty);
        }
        finally
        {
            File.Delete(file);
        }
    }

    public async Task<EmptyResult> EfUpdateDatabaseAsync(EfUpdateDatabaseParams parameters, CancellationToken cancellationToken)
    {
        string[] command = parameters.Migration is { Length: > 0 } migration ? ["database", "update", migration] : ["database", "update"];
        await RunEfAsync(parameters.Target, command, cancellationToken, connection: true).ConfigureAwait(false);
        return new EmptyResult();
    }

    public async Task<EfFilesResult> EfBundleAsync(EfBundleParams parameters, CancellationToken cancellationToken)
    {
        var output = System.IO.Path.GetFullPath(parameters.Output);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!);
        var command = new List<string> { "migrations", "bundle", "--output", output };
        if (parameters.Force)
        {
            command.Add("--force");
        }

        if (parameters.SelfContained)
        {
            command.Add("--self-contained");
        }

        AddOption(command, "--target-runtime", parameters.Runtime);
        await RunEfAsync(parameters.Target with { Connection = null }, [.. command], cancellationToken).ConfigureAwait(false);
        var produced = File.Exists(output) ? output : File.Exists(output + ".exe") ? output + ".exe" : null;
        return new EfFilesResult(produced is null ? [] : [produced]);
    }

    public async Task<EfFilesResult> EfScaffoldAsync(EfScaffoldParams parameters, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(parameters.Connection) || string.IsNullOrWhiteSpace(parameters.Provider))
        {
            throw new ArgumentException("Reverse engineering needs a connection string and a provider.");
        }

        var command = new List<string> { "dbcontext", "scaffold", parameters.Connection, parameters.Provider, "--json" };
        AddOption(command, "--output-dir", parameters.OutputDir);
        AddOption(command, "--context-dir", parameters.ContextDir);
        AddOption(command, "--context", parameters.ContextName);
        AddOption(command, "--namespace", parameters.Namespace);
        AddOption(command, "--context-namespace", parameters.ContextNamespace);
        foreach (var table in parameters.Tables ?? [])
        {
            command.AddRange(["--table", table]);
        }

        foreach (var schema in parameters.Schemas ?? [])
        {
            command.AddRange(["--schema", schema]);
        }

        if (parameters.DataAnnotations)
        {
            command.Add("--data-annotations");
        }

        if (parameters.NoOnConfiguring)
        {
            command.Add("--no-onconfiguring");
        }

        if (parameters.UseDatabaseNames)
        {
            command.Add("--use-database-names");
        }

        if (parameters.NoPluralize)
        {
            command.Add("--no-pluralize");
        }

        if (parameters.Force)
        {
            command.Add("--force");
        }

        var target = new EfTarget(parameters.Project, parameters.StartupProject, NoBuild: parameters.NoBuild);
        var run = await RunEfAsync(target, [.. command], cancellationToken).ConfigureAwait(false);
        return new EfFilesResult(EfTool.Files(run));
    }

    public async Task<EfFilesResult> EfOptimizeAsync(EfOptimizeParams parameters, CancellationToken cancellationToken)
    {
        // dbcontext optimize has no --json: the files are the ones it wrote to the output folder.
        var command = new List<string> { "dbcontext", "optimize" };
        AddOption(command, "--output-dir", parameters.OutputDir);
        AddOption(command, "--namespace", parameters.Namespace);
        var project = System.IO.Path.GetFullPath(parameters.Target.Project);
        var folder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(project)!, parameters.OutputDir ?? "CompiledModels");
        var before = DateTime.UtcNow.AddSeconds(-2);
        var run = await RunEfAsync(parameters.Target with { Connection = null }, [.. command], cancellationToken).ConfigureAwait(false);
        var files = EfTool.Files(run);
        if (files.Length == 0 && Directory.Exists(folder))
        {
            // Older tools print no JSON for optimize: list what it wrote.
            files = [.. Directory.EnumerateFiles(folder, "*.cs").Where(f => File.GetLastWriteTimeUtc(f) >= before)];
        }

        return new EfFilesResult(files);
    }

    // ------------------------------------------------------------------ the model agent

    /// <summary>
    /// The design-time model, from <c>codalus-efagent</c> on the startup project's runtime (its deps.json and
    /// runtimeconfig.json), started in the startup project's folder like <c>dotnet ef</c>.
    /// </summary>
    public async Task<EfModelResult> EfModelAsync(EfTarget target, CancellationToken cancellationToken)
    {
        RequireTrusted("Entity Framework");
        var startup = System.IO.Path.GetFullPath(target.StartupProject ?? target.Project);
        var run = GetRunInfo(startup);
        if (run.TargetPath is not { } assembly || !File.Exists(assembly))
        {
            throw new InvalidOperationException($"{System.IO.Path.GetFileName(startup)} isn't built; build it first.");
        }

        var deps = System.IO.Path.ChangeExtension(assembly, ".deps.json");
        var runtimeConfig = System.IO.Path.ChangeExtension(assembly, ".runtimeconfig.json");
        if (!File.Exists(runtimeConfig))
        {
            throw new InvalidOperationException($"{System.IO.Path.GetFileName(startup)} has no runtimeconfig.json: the startup project must be an application.");
        }

        var agent = Environment.GetEnvironmentVariable(EfAgentVariable) is { Length: > 0 } fromEnvironment
            ? fromEnvironment
            : System.IO.Path.Combine(AppContext.BaseDirectory, "efagent", "codalus-efagent.dll");
        if (!File.Exists(agent))
        {
            throw new InvalidOperationException($"The model agent was not found at {agent}.");
        }

        var context = target.Context ?? (await EfContextsAsync(target, cancellationToken).ConfigureAwait(false)).Contexts.FirstOrDefault()?.FullName
            ?? throw new InvalidOperationException("The project has no DbContext.");
        var output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codalus-efmodel-" + Guid.NewGuid().ToString("N") + ".json");
        var arguments = new List<string> { "exec" };
        if (File.Exists(deps))
        {
            arguments.AddRange(["--depsfile", deps]);
        }

        arguments.AddRange(["--runtimeconfig", runtimeConfig]);
        foreach (var probing in ProbingPaths())
        {
            arguments.AddRange(["--additionalprobingpath", probing]);
        }

        arguments.AddRange([agent, "model", "--startup-assembly", assembly, "--context", context, "--context-assembly", System.IO.Path.GetFileNameWithoutExtension(target.Project), "--output", output]);
        var psi = DotnetProcess.StartInfo(_dotnet, System.IO.Path.GetDirectoryName(startup)!, arguments);
        psi.StandardErrorEncoding = Encoding.UTF8;
        psi.StandardOutputEncoding = Encoding.UTF8;
        await _events.EfOutputAsync(new EfOutputParams("info", $"> codalus-efagent model --context {context} ({System.IO.Path.GetFileName(assembly)})")).ConfigureAwait(false);
        await EfTool.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var process = DotnetProcess.Start(psi) ?? throw new InvalidOperationException("dotnet did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await DotnetProcess.WaitOrKillAsync(process, cancellationToken).ConfigureAwait(false);
            var errors = await stderr.ConfigureAwait(false);
            _ = await stdout.ConfigureAwait(false); // the app's own logging
            if (process.ExitCode != 0 || !File.Exists(output))
            {
                var first = errors.Split('\n').FirstOrDefault(l => l.StartsWith("error: ", StringComparison.Ordinal))?[7..].Trim() ?? errors.Trim();
                await _events.EfOutputAsync(new EfOutputParams("error", errors.Trim())).ConfigureAwait(false);
                throw new InvalidOperationException("The model could not be read: " + first);
            }

            var json = await File.ReadAllTextAsync(output, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize(json, EntityFrameworkJsonContext.Default.EfModelResult)
                ?? throw new InvalidOperationException("The model agent returned nothing.");
        }
        finally
        {
            EfTool.Gate.Release();
            File.Delete(output);
        }
    }

    /// <summary>The NuGet package folders an app's deps.json may point into (as dotnet-ef passes them).</summary>
    private static IEnumerable<string> ProbingPaths()
    {
        var packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured
            ? configured
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        if (Directory.Exists(packages))
        {
            yield return packages;
        }
    }

    // ------------------------------------------------------------------ plumbing

    private async Task<EfRun> RunEfAsync(EfTarget target, string[] command, CancellationToken cancellationToken, bool connection = false)
    {
        var (directory, arguments) = EfArguments(target, connection, command);
        var run = await Ef.RunAsync(directory, arguments, cancellationToken).ConfigureAwait(false);
        Check(run);
        return run;
    }

    private static void Check(EfRun run)
    {
        if (!run.Succeeded)
        {
            ThrowIfToolMissing(run);
            throw new InvalidOperationException(run.Failure);
        }
    }

    private static void ThrowIfToolMissing(EfRun run)
    {
        if (run.Output.Contains("dotnet-ef does not exist", StringComparison.Ordinal) || run.Output.Contains("dotnet tool restore", StringComparison.Ordinal)
            || run.Output.Contains("No executable found matching command \"dotnet-ef\"", StringComparison.Ordinal))
        {
            throw new JsonRpcHandlerException(ProjectErrorCodes.ToolMissing, "dotnet-ef isn't available here. Install it (or restore the .NET tools) from the Entity Framework window.");
        }
    }

    /// <summary>
    /// The arguments every command shares: the project, the startup project, the context, the configuration of the
    /// startup project in the active solution configuration, its active target framework, <c>--no-build</c>, and the
    /// connection for commands that take one.
    /// </summary>
    private (string Directory, List<string> Arguments) EfArguments(EfTarget target, bool connection, params string[] command)
    {
        RequireTrusted("Entity Framework");
        var project = System.IO.Path.GetFullPath(target.Project);
        var startup = System.IO.Path.GetFullPath(target.StartupProject ?? target.Project);
        var arguments = new List<string>(command) { "--project", project, "--startup-project", startup };
        AddOption(arguments, "--context", target.Context);
        arguments.AddRange(["--configuration", ProjectConfiguration(startup).Configuration]);
        if (_evaluated.TryGetValue(startup, out var evaluated) && evaluated.Info.TargetFrameworks.Length > 1)
        {
            arguments.AddRange(["--framework", _activeFrameworks.TryGetValue(startup, out var active) && evaluated.Info.TargetFrameworks.Contains(active, StringComparer.OrdinalIgnoreCase) ? active : evaluated.Info.TargetFrameworks[0]]);
        }

        if (target.NoBuild)
        {
            arguments.Add("--no-build");
        }

        if (connection)
        {
            AddOption(arguments, "--connection", target.Connection);
        }

        return (System.IO.Path.GetDirectoryName(startup)!, arguments);
    }

    private static void AddOption(List<string> arguments, string option, string? value)
    {
        if (value is { Length: > 0 })
        {
            arguments.AddRange([option, value]);
        }
    }

    private static bool IsIdentifier(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_');
}
