using Codalus.Projects.Host.Packages;
using Codalus.Projects.Protocol;

namespace Codalus.Projects.Host;

/// <summary>
/// Non-SDK-style .NET Framework projects (M43a, ADR 0075): where their packages go, what Codalus restores for them
/// beside <c>dotnet restore</c> (<c>packages.config</c> packages and the reference assemblies), and the projects that
/// can't build here (the classic IDE's own targets), which solution builds and restores leave out.
/// </summary>
internal sealed partial class SolutionSession
{
    private bool _netFrameworkConfigured;

    /// <summary>
    /// Once per session, before the first evaluation: the repository path (packages.config) and the global packages
    /// folder from the solution's NuGet settings, for the evaluator and the reference assemblies targets.
    /// </summary>
    private void ConfigureNetFramework()
    {
        if (_netFrameworkConfigured)
        {
            return;
        }

        _netFrameworkConfigured = true;
        try
        {
            var global = NuGet.GlobalPackagesFolder();
            _evaluator.GlobalPackagesFolder = global;
            _evaluator.PackagesRepository = NetFramework.RepositoryPath(NuGet.ConfiguredRepositoryPath(), SolutionDirectory);
            NetFramework.SetPackageRoot(global);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // A broken NuGet.Config: non-SDK-style projects then load without their packages, saying so under Dependencies.
            _evaluator.PackagesRepository = NetFramework.RepositoryPath(null, SolutionDirectory);
            _ = _events.LogAsync("warning", $"NuGet's settings couldn't be read for non-SDK-style projects: {ex.Message}");
        }
    }

    /// <summary>Why a loaded project can't build here (the classic IDE's targets), or null.</summary>
    private string? CannotBuildReason(string project) =>
        _evaluated.TryGetValue(project, out var evaluated) ? evaluated.Info.CannotBuildReason : null;

    private bool CannotBuild(string project) => CannotBuildReason(project) is not null;

    /// <summary>
    /// Installs what the non-SDK-style projects among <paramref name="projects"/> lack: their <c>packages.config</c>
    /// packages and their framework's reference assemblies. Returns the number of packages that failed and the projects
    /// whose packages changed (to evaluate and design-time build again).
    /// </summary>
    private async Task<(int Failed, IReadOnlyList<string> Changed)> RestoreNetFrameworkAsync(IEnumerable<string> projects, Func<string, Task> output, CancellationToken cancellationToken)
    {
        var installs = new List<NuGetClient.Install>();
        var changed = new List<string>();
        foreach (var project in projects)
        {
            if (!_evaluated.TryGetValue(project, out var evaluated) || evaluated.NetFrameworkRestore is not { IsComplete: false } restore)
            {
                continue;
            }

            changed.Add(project);
            installs.AddRange(restore.MissingPackages.Select(p => new NuGetClient.Install(p.Id, p.Version, restore.RepositoryPath)));
            if (restore.MissingReferenceAssemblies && restore.ReferenceAssemblies is { } moniker)
            {
                installs.Add(new NuGetClient.Install(NetFramework.ReferenceAssembliesPackageOf(moniker), NetFramework.ReferenceAssembliesVersion, null));
            }
        }

        installs = [.. installs.DistinctBy(i => (i.Id.ToLowerInvariant(), i.Version, i.RepositoryPath))];
        if (installs.Count == 0)
        {
            return (0, []);
        }

        await output($"Restoring {installs.Count} package(s) of non-SDK-style projects (packages.config and the .NET Framework reference assemblies)…").ConfigureAwait(false);
        var failed = await NuGet.InstallAsync(installs, output, cancellationToken).ConfigureAwait(false);
        return (failed, changed);
    }

    /// <summary>Before a build that restores: the non-SDK-style projects' packages, then those projects evaluated again.</summary>
    private async Task RestoreNetFrameworkForBuildAsync(IBuildEvents events, CancellationToken cancellationToken)
    {
        var (failed, changed) = await RestoreNetFrameworkAsync([.. ProjectPaths.Where(p => !IsUnloaded(p) && !CannotBuild(p))], events.BuildOutputAsync, cancellationToken).ConfigureAwait(false);
        if (failed > 0)
        {
            await events.BuildOutputAsync($"warning: {failed} package(s) of non-SDK-style projects couldn't be restored; their builds may fail.").ConfigureAwait(false);
        }

        if (changed.Count > 0)
        {
            await EvaluateAsync(changed, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A project build of a project that can't build here says why (an error on the project, in the Error List too) and
    /// fails; a solution build says which projects it leaves out (a warning on each). True when the build should go on.
    /// </summary>
    private async Task<bool> ReportProjectsThatCannotBuildAsync(string? project, IBuildEvents events)
    {
        if (project is not null)
        {
            if (CannotBuildReason(project) is { } reason)
            {
                await ReportAsync(events, BuildSeverities.Error, project, reason).ConfigureAwait(false);
                return false;
            }

            return true;
        }

        foreach (var path in ProjectPaths.Where(p => !IsUnloaded(p) && CannotBuild(p)))
        {
            await ReportAsync(events, BuildSeverities.Warning, path, $"{System.IO.Path.GetFileNameWithoutExtension(path)} is left out of the build. {CannotBuildReason(path)}").ConfigureAwait(false);
        }

        return true;
    }

    private static async Task ReportAsync(IBuildEvents events, string severity, string project, string message)
    {
        await events.BuildOutputAsync($"{project}: {severity}: {message}").ConfigureAwait(false);
        await events.BuildDiagnosticAsync(new BuildDiagnosticInfo(severity, null, message, project, 0, 0, 0, 0, project, null)).ConfigureAwait(false);
    }

    /// <summary>A project's .NET Framework moniker for its run information, when it has no <c>TargetFramework</c> (non-SDK-style).</summary>
    private string? NonSdkFramework(string project) =>
        _evaluated.TryGetValue(project, out var evaluated) && evaluated.Info.IsNonSdkStyle ? evaluated.Info.TargetFrameworks.FirstOrDefault() : null;
}
