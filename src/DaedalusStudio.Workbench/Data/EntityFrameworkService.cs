using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Output;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Workbench.Data;

/// <summary>A build that EF commands needed failed: its errors are in the Build output and the Error List.</summary>
public sealed class EfBuildFailedException(string project) : Exception($"The build of {Path.GetFileNameWithoutExtension(project)} failed; see the Error List.");

/// <summary>A DbContext the window shows: from Roslyn (instantly) and/or <c>dotnet ef dbcontext list</c>.</summary>
public sealed record EfContextEntry(string Project, string Name, string FullName, string? Provider, string? ConnectionName, IReadOnlyList<string> Entities, string? Path, int Line)
{
    public string Label => $"{Name} ({System.IO.Path.GetFileNameWithoutExtension(Project)})";
}

/// <summary>
/// EF Core (ADR 0036) through the build host: it runs the user's <c>dotnet ef</c> and the model agent. Commands that
/// load the project's assembly build the startup project first through <see cref="BuildService"/> (output and errors
/// where builds put them) and pass <c>--no-build</c>; a build is skipped while nothing changed since the last one.
/// UI-agnostic; events arrive on any thread.
/// </summary>
public sealed class EntityFrameworkService : IDisposable
{
    public const string OutputChannelId = "daedalus.ef";

    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(10);
    private static EntityFrameworkJsonContext Json => EntityFrameworkJsonContext.Default;

    private readonly SolutionService _solutions;
    private readonly BuildService _builds;
    private readonly OutputService _output;
    private readonly Func<string?> _startupProject;
    private readonly Func<CancellationToken, Task<EfDiscoveredContextsResult?>> _discover;
    private readonly HashSet<string> _built = new(SolutionService.PathComparer);
    private readonly SemaphoreSlim _buildGate = new(1, 1);

    public EntityFrameworkService(
        SolutionService solutions,
        BuildService builds,
        OutputService output,
        Func<string?> startupProject,
        Func<CancellationToken, Task<EfDiscoveredContextsResult?>> discover)
    {
        _solutions = solutions;
        _builds = builds;
        _output = output;
        _startupProject = startupProject;
        _discover = discover;
        solutions.EfOutput += (_, o) => Channel.AppendLine(o.Level is "info" or "data" ? o.Line : $"{o.Level}: {o.Line}");
        solutions.SolutionChanged += (_, _) => Invalidate();
        builds.Started += (_, _) => Invalidate();
    }

    public OutputChannel Channel => _output.GetOrCreate(OutputChannelId, "Entity Framework");

    /// <summary>Migrations or files changed (a migration added, removed or applied, a model scaffolded).</summary>
    public event EventHandler? Changed;

    /// <summary>The EF Core provider packages and EF Core itself, by package id prefix.</summary>
    public static bool ReferencesEfCore(ProjectInfo project) =>
        project.References.Any(r => r.Kind == ReferenceKinds.Package
            && (r.Name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)
                || r.Name.EndsWith(".EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)
                || r.Name.Contains(".EntityFrameworkCore.", StringComparison.OrdinalIgnoreCase)));

    /// <summary>The solution's projects that reference EF Core, by name.</summary>
    public IReadOnlyList<ProjectInfo> Projects =>
        [.. _solutions.Projects.Values.Where(ReferencesEfCore).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The startup project for EF commands on <paramref name="project"/>: the solution's startup project when it is the
    /// project or references it, otherwise the project itself.
    /// </summary>
    public string StartupFor(string project)
    {
        if (_startupProject() is { } startup && _solutions.Projects.TryGetValue(startup, out var info)
            && (SolutionService.PathComparer.Equals(startup, project) || info.References.Any(r => r.Kind == ReferenceKinds.Project && r.Path is { } path && SolutionService.PathComparer.Equals(path, project))))
        {
            return startup;
        }

        return project;
    }

    /// <summary>Anything a build would pick up changed: the next command builds again.</summary>
    public void Invalidate()
    {
        lock (_built)
        {
            _built.Clear();
        }
    }

    // ------------------------------------------------------------------ discovery

    /// <summary>
    /// The contexts of the EF projects: Roslyn's (provider and connection name included) merged with
    /// <c>dotnet ef dbcontext list</c> when <paramref name="fromTool"/> (which builds).
    /// </summary>
    public async Task<IReadOnlyList<EfContextEntry>> ContextsAsync(bool fromTool, CancellationToken cancellationToken)
    {
        var entries = new List<EfContextEntry>();
        if (await _discover(cancellationToken).ConfigureAwait(false) is { } discovered)
        {
            entries.AddRange(discovered.Contexts.Select(c => new EfContextEntry(c.Project, c.Name, c.FullName, c.Provider, c.ConnectionName, c.Entities,
                DaedalusStudio.LanguageServices.Protocol.DocumentUris.ToPath(c.Location.Uri), c.Location.Range.Start.Line)));
        }

        if (fromTool)
        {
            foreach (var project in Projects.Select(p => p.Path).Where(p => entries.Count == 0 || entries.Any(e => SolutionService.PathComparer.Equals(e.Project, p))))
            {
                try
                {
                    var listed = await ToolContextsAsync(project, cancellationToken).ConfigureAwait(false);
                    foreach (var context in listed.Where(c => !entries.Any(e => e.FullName == c.FullName)))
                    {
                        entries.Add(new EfContextEntry(project, context.Name, context.FullName, null, null, [], null, 0));
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Rpc.JsonRpcRemoteException or EfBuildFailedException)
                {
                    Channel.AppendLine($"Could not list the contexts of {Path.GetFileName(project)}: {ex.Message}");
                }
            }
        }

        return [.. entries.OrderBy(e => e.Name, StringComparer.Ordinal)];
    }

    public async Task<IReadOnlyList<EfContextItem>> ToolContextsAsync(string project, CancellationToken cancellationToken)
    {
        var target = await PrepareAsync(new EfTarget(project, StartupFor(project)), cancellationToken).ConfigureAwait(false);
        return (await RequestAsync(EfMethods.Contexts, target, Json.EfTarget, Json.EfContextsResult, cancellationToken).ConfigureAwait(false)).Contexts;
    }

    public Task<EfStatusResult> StatusAsync(string project, CancellationToken cancellationToken) =>
        RequestAsync(EfMethods.Status, new EfStatusParams(project, StartupFor(project)), Json.EfStatusParams, Json.EfStatusResult, cancellationToken);

    // ------------------------------------------------------------------ commands

    /// <summary>The target of a context: its project, the startup project, and the connection (null: the app's configuration).</summary>
    public EfTarget Target(EfContextEntry context, string? connection = null) => new(context.Project, StartupFor(context.Project), context.FullName, connection);

    public async Task<EfContextInfoResult> ContextInfoAsync(EfTarget target, CancellationToken cancellationToken) =>
        await RequestAsync(EfMethods.ContextInfo, await PrepareAsync(target, cancellationToken).ConfigureAwait(false), Json.EfTarget, Json.EfContextInfoResult, cancellationToken).ConfigureAwait(false);

    public async Task<EfMigrationsResult> MigrationsAsync(EfTarget target, CancellationToken cancellationToken) =>
        await RequestAsync(EfMethods.Migrations, await PrepareAsync(target, cancellationToken).ConfigureAwait(false), Json.EfTarget, Json.EfMigrationsResult, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> AddMigrationAsync(EfTarget target, string name, string? outputDir, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(target, cancellationToken).ConfigureAwait(false);
        var result = await RequestAsync(EfMethods.AddMigration, new EfAddMigrationParams(prepared, name, outputDir), Json.EfAddMigrationParams, Json.EfFilesResult, cancellationToken).ConfigureAwait(false);
        Invalidate();
        Changed?.Invoke(this, EventArgs.Empty);
        return result.Files;
    }

    public async Task<IReadOnlyList<string>> RemoveMigrationAsync(EfTarget target, bool force, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(target, cancellationToken).ConfigureAwait(false);
        var result = await RequestAsync(EfMethods.RemoveMigration, new EfRemoveMigrationParams(prepared, force), Json.EfRemoveMigrationParams, Json.EfFilesResult, cancellationToken).ConfigureAwait(false);
        Invalidate();
        Changed?.Invoke(this, EventArgs.Empty);
        return result.Files;
    }

    public async Task<string> ScriptAsync(EfTarget target, string? from, string? to, bool idempotent, bool noTransactions, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(target, cancellationToken).ConfigureAwait(false);
        return (await RequestAsync(EfMethods.Script, new EfScriptParams(prepared, from, to, idempotent, noTransactions), Json.EfScriptParams, Json.EfScriptResult, cancellationToken).ConfigureAwait(false)).Sql;
    }

    /// <summary>
    /// The SQL that updating the database to <paramref name="migration"/> (null: the last one; <c>0</c>: none) would run,
    /// given the applied migrations: the Up scripts of the pending ones, or the Down scripts of the ones to revert.
    /// Empty when the database is already there.
    /// </summary>
    public Task<string> UpdateScriptAsync(EfTarget target, IReadOnlyList<EfMigrationItem> migrations, string? migration, CancellationToken cancellationToken)
    {
        var (from, to) = UpdateRange(migrations, migration);
        return from == to ? Task.FromResult(string.Empty) : ScriptAsync(target, from, to, idempotent: false, noTransactions: false, cancellationToken);
    }

    /// <summary>The script's range for an update: from the last applied migration (or 0) to the target (the last migration by default).</summary>
    public static (string From, string To) UpdateRange(IReadOnlyList<EfMigrationItem> migrations, string? migration)
    {
        var applied = migrations.LastOrDefault(m => m.Applied == true)?.Id ?? "0";
        var to = migration is { Length: > 0 } target ? (migrations.FirstOrDefault(m => m.Id == target || m.Name == target)?.Id ?? target) : migrations.Count > 0 ? migrations[^1].Id : "0";
        return (applied, to);
    }

    public async Task UpdateDatabaseAsync(EfTarget target, string? migration, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(target, cancellationToken).ConfigureAwait(false);
        await RequestAsync(EfMethods.UpdateDatabase, new EfUpdateDatabaseParams(prepared, migration), Json.EfUpdateDatabaseParams, Json.EmptyResult, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<string?> BundleAsync(EfTarget target, string output, bool selfContained, string? runtime, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(target, cancellationToken).ConfigureAwait(false);
        var result = await RequestAsync(EfMethods.Bundle, new EfBundleParams(prepared, output, selfContained, runtime), Json.EfBundleParams, Json.EfFilesResult, cancellationToken).ConfigureAwait(false);
        return result.Files.FirstOrDefault();
    }

    public async Task<IReadOnlyList<string>> ScaffoldAsync(EfScaffoldParams parameters, CancellationToken cancellationToken)
    {
        var startup = parameters.StartupProject ?? StartupFor(parameters.Project);
        await EnsureBuiltAsync(startup, cancellationToken).ConfigureAwait(false);
        var result = await RequestAsync(EfMethods.Scaffold, parameters with { StartupProject = startup, NoBuild = true }, Json.EfScaffoldParams, Json.EfFilesResult, cancellationToken).ConfigureAwait(false);
        Invalidate();
        Changed?.Invoke(this, EventArgs.Empty);
        return result.Files;
    }

    public async Task<IReadOnlyList<string>> OptimizeAsync(EfTarget target, string? outputDir, string? @namespace, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(target, cancellationToken).ConfigureAwait(false);
        var result = await RequestAsync(EfMethods.Optimize, new EfOptimizeParams(prepared, outputDir, @namespace), Json.EfOptimizeParams, Json.EfFilesResult, cancellationToken).ConfigureAwait(false);
        Invalidate();
        return result.Files;
    }

    public async Task<EfModelResult> ModelAsync(EfTarget target, CancellationToken cancellationToken) =>
        await RequestAsync(EfMethods.Model, await PrepareAsync(target, cancellationToken).ConfigureAwait(false), Json.EfTarget, Json.EfModelResult, cancellationToken).ConfigureAwait(false);

    // ------------------------------------------------------------------ building

    /// <summary>Builds the startup project (unless it was built since the last change) and returns the target with <c>--no-build</c>.</summary>
    private async Task<EfTarget> PrepareAsync(EfTarget target, CancellationToken cancellationToken)
    {
        var startup = target.StartupProject ?? StartupFor(target.Project);
        await EnsureBuiltAsync(startup, cancellationToken).ConfigureAwait(false);
        return target with { StartupProject = startup, NoBuild = true };
    }

    /// <summary>Builds <paramref name="startup"/> through the build service, waiting for a build in progress first.</summary>
    public async Task EnsureBuiltAsync(string startup, CancellationToken cancellationToken)
    {
        await _buildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_built)
            {
                if (_built.Contains(startup))
                {
                    return;
                }
            }

            while (_builds.IsBuilding)
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }

            Channel.AppendLine($"Building {Path.GetFileNameWithoutExtension(startup)}…");
            BuildResultInfo result;
            try
            {
                result = await _builds.BuildAsync(BuildTargets.Build, startup, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Another build started in between: wait for it, then build.
                while (_builds.IsBuilding)
                {
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                }

                result = await _builds.BuildAsync(BuildTargets.Build, startup, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Succeeded)
            {
                throw new EfBuildFailedException(startup);
            }

            lock (_built)
            {
                _built.Add(startup);
            }
        }
        finally
        {
            _buildGate.Release();
        }
    }

    public void Dispose() => _buildGate.Dispose();

    private Task<TResult> RequestAsync<TParams, TResult>(string method, TParams parameters, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TParams> parametersInfo, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultInfo, CancellationToken cancellationToken) =>
        _solutions.RequestAsync(method, parameters, parametersInfo, resultInfo, cancellationToken, s_timeout);
}
