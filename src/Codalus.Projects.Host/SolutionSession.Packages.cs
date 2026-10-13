using Codalus.Projects.Host.Packages;
using Codalus.Projects.Protocol;
using Microsoft.Build.Exceptions;

namespace Codalus.Projects.Host;

/// <summary>NuGet packages and .NET tools of the open solution (ADR 0012).</summary>
internal sealed partial class SolutionSession
{
    private readonly SemaphoreSlim _packageEdits = new(1, 1);
    private NuGetClient? _nuget;

    public NuGetClient NuGet
    {
        get
        {
            RequireTrusted("Package management");
            return _nuget ??= new NuGetClient(SolutionDirectory, (level, message) => _events.LogAsync(level, message));
        }
    }

    public DotnetTools Tools
    {
        get
        {
            RequireTrusted(".NET tools");
            return new DotnetTools(_dotnet, SolutionDirectory);
        }
    }

    private void RequireTrusted(string feature)
    {
        if (!Trusted)
        {
            throw new InvalidOperationException($"{feature} is available only in trusted solutions.");
        }
    }

    /// <summary>The project evaluated per target framework, with the active solution configuration.</summary>
    private PackageReferences.Evaluation EvaluateForPackages(string projectPath)
    {
        var path = System.IO.Path.GetFullPath(projectPath);
        var properties = SolutionProperties();
        (properties["Configuration"], properties["Platform"]) = ProjectConfiguration(path);
        return new PackageReferences.Evaluation(path, properties);
    }

    public async Task<InstalledPackagesResult> InstalledPackagesAsync(InstalledPackagesParams parameters, CancellationToken cancellationToken)
    {
        RequireTrusted("Package management");
        var projects = parameters.Project is { } one
            ? [System.IO.Path.GetFullPath(one)]
            : ProjectPaths.Where(p => _evaluated.TryGetValue(p, out var e) && e.Info.State == ProjectStates.Loaded).ToList();
        var results = new ProjectPackages?[projects.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, projects.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2), CancellationToken = cancellationToken },
            async (index, _) =>
            {
                try
                {
                    using var evaluation = EvaluateForPackages(projects[index]);
                    results[index] = PackageReferences.Read(evaluation);
                }
                catch (InvalidProjectFileException ex) when (parameters.Project is null)
                {
                    await _events.LogAsync("warning", $"Could not read the packages of {projects[index]}: {ex.Message}").ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        return new InstalledPackagesResult([.. results.OfType<ProjectPackages>()]);
    }

    public PackageWhyResult WhyPackage(PackageWhyParams parameters)
    {
        RequireTrusted("Package management");
        using var evaluation = EvaluateForPackages(parameters.Project);
        return PackageReferences.Why(evaluation, parameters.Id);
    }

    public async Task<PackageEditResult> InstallPackageAsync(PackageInstallParams parameters, CancellationToken cancellationToken)
    {
        RequireTrusted("Package management");
        if (string.IsNullOrWhiteSpace(parameters.Id) || !global::NuGet.Versioning.VersionRange.TryParse(parameters.Version, out _))
        {
            throw new ArgumentException($"'{parameters.Version}' is not a valid version for {parameters.Id}.");
        }

        return await EditPackagesAsync(
            parameters.Projects.Select(p => p.Project),
            parameters.Restore,
            (evaluation, project) => PackageReferences.Install(
                evaluation,
                parameters.Id.Trim(),
                parameters.Version.Trim(),
                parameters.Projects.First(p => System.IO.Path.GetFullPath(p.Project) == project).Framework,
                parameters.VersionOverride),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<PackageEditResult> UninstallPackageAsync(PackageUninstallParams parameters, CancellationToken cancellationToken)
    {
        RequireTrusted("Package management");
        return EditPackagesAsync(parameters.Projects, parameters.Restore, (evaluation, _) => PackageReferences.Uninstall(evaluation, parameters.Id), cancellationToken);
    }

    private async Task<PackageEditResult> EditPackagesAsync(
        IEnumerable<string> projects,
        bool restore,
        Func<PackageReferences.Evaluation, string, IReadOnlyList<string>> edit,
        CancellationToken cancellationToken)
    {
        var targets = projects.Select(System.IO.Path.GetFullPath).Distinct(s_pathComparer).ToList();
        if (targets.Count == 0)
        {
            throw new ArgumentException("No project was chosen.");
        }

        var changed = new List<string>();
        await _packageEdits.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // One at a time: under CPM several projects share Directory.Packages.props.
            foreach (var project in targets)
            {
                using var evaluation = EvaluateForPackages(project);
                changed.AddRange(edit(evaluation, project));
            }
        }
        finally
        {
            _packageEdits.Release();
        }

        var updated = changed.Distinct(s_pathComparer).ToArray();
        if (updated.Length > 0)
        {
            await _events.LogAsync("info", $"Updated {string.Join(", ", updated.Select(System.IO.Path.GetFileName))}.").ConfigureAwait(false);
            if (restore)
            {
                StartRestore(targets.Count == 1 ? targets[0] : null);
            }
        }

        return new PackageEditResult(updated);
    }
}
