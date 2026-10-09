using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Workbench.Projects;

namespace DaedalusStudio.Workbench.Packages;

/// <summary>
/// NuGet packages and .NET tools of the open solution, through the build host (ADR 0012). Feeds are only contacted
/// from the host. <see cref="PackagesChanged"/> fires after an install or uninstall and after every restore, so
/// package views refresh.
/// </summary>
public sealed class PackageService
{
    public const string OfflineSetting = "nuget.offline";

    private static readonly TimeSpan s_networkTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_toolTimeout = TimeSpan.FromMinutes(5);

    private readonly SolutionService _solutions;

    public PackageService(SolutionService solutions)
    {
        _solutions = solutions;
        solutions.RestoreStateChanged += (_, e) =>
        {
            if (e.State != "running")
            {
                PackagesChanged?.Invoke(this, EventArgs.Empty);
            }
        };

        // A project re-evaluated after an edit: what an earlier read saw (mid-evaluation) may be stale.
        solutions.ProjectChanged += (_, _) => PackagesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Only local sources (folders, the global packages folder) are used.</summary>
    public bool Offline { get; set; }

    /// <summary>Installed packages may have changed (an edit, or a restore finished). Raised on any thread.</summary>
    public event EventHandler? PackagesChanged;

    /// <summary>Package sources or mappings were saved.</summary>
    public event EventHandler? SourcesChanged;

    private static PackageJsonContext Json => PackageJsonContext.Default;

    public Task<PackageSearchResult> SearchAsync(string query, bool prerelease, int skip, int take, string? source, CancellationToken cancellationToken, bool refresh = false) =>
        _solutions.RequestAsync(PackageMethods.Search, new PackageSearchParams(query, prerelease, skip, take, source, Offline, refresh), Json.PackageSearchParams, Json.PackageSearchResult, cancellationToken, s_networkTimeout);

    public Task<PackageDetails> DetailsAsync(string id, bool prerelease, string? version, string? source, CancellationToken cancellationToken) =>
        _solutions.RequestAsync(PackageMethods.Details, new PackageDetailsParams(id, prerelease, version, source, Offline), Json.PackageDetailsParams, Json.PackageDetails, cancellationToken, s_networkTimeout);

    public Task<PackageStatusResult> StatusAsync(IReadOnlyList<PackageStatusQuery> packages, bool prerelease, CancellationToken cancellationToken, bool refresh = false) =>
        _solutions.RequestAsync(PackageMethods.Status, new PackageStatusParams([.. packages], prerelease, Offline, refresh), Json.PackageStatusParams, Json.PackageStatusResult, cancellationToken, s_networkTimeout);

    /// <summary>Installed packages of one project, or of every loaded project when <paramref name="project"/> is null.</summary>
    public async Task<IReadOnlyList<ProjectPackages>> InstalledAsync(string? project, CancellationToken cancellationToken) =>
        (await _solutions.RequestAsync(PackageMethods.Installed, new InstalledPackagesParams(project), Json.InstalledPackagesParams, Json.InstalledPackagesResult, cancellationToken).ConfigureAwait(false)).Projects;

    public async Task<IReadOnlyList<PackageWhyPath>> WhyAsync(string project, string id, CancellationToken cancellationToken) =>
        (await _solutions.RequestAsync(PackageMethods.Why, new PackageWhyParams(project, id), Json.PackageWhyParams, Json.PackageWhyResult, cancellationToken).ConfigureAwait(false)).Paths;

    public async Task<IReadOnlyList<string>> InstallAsync(IReadOnlyList<PackageEditTarget> projects, string id, string version, bool versionOverride, CancellationToken cancellationToken)
    {
        var result = await _solutions.RequestAsync(PackageMethods.Install, new PackageInstallParams([.. projects], id, version, versionOverride), Json.PackageInstallParams, Json.PackageEditResult, cancellationToken).ConfigureAwait(false);
        PackagesChanged?.Invoke(this, EventArgs.Empty);
        return result.UpdatedFiles;
    }

    public async Task<IReadOnlyList<string>> UninstallAsync(IReadOnlyList<string> projects, string id, CancellationToken cancellationToken)
    {
        var result = await _solutions.RequestAsync(PackageMethods.Uninstall, new PackageUninstallParams([.. projects], id), Json.PackageUninstallParams, Json.PackageEditResult, cancellationToken).ConfigureAwait(false);
        PackagesChanged?.Invoke(this, EventArgs.Empty);
        return result.UpdatedFiles;
    }

    public Task<PackageSourcesResult> SourcesAsync(CancellationToken cancellationToken) =>
        _solutions.RequestAsync(PackageMethods.Sources, new EmptyParams(), Json.EmptyParams, Json.PackageSourcesResult, cancellationToken);

    public async Task SetSourcesAsync(IReadOnlyList<PackageSourceInfo> sources, IReadOnlyList<PackageSourceMapping> mappings, CancellationToken cancellationToken)
    {
        await _solutions.RequestAsync(PackageMethods.SetSources, new SetPackageSourcesParams([.. sources], [.. mappings]), Json.SetPackageSourcesParams, Json.EmptyResult, cancellationToken).ConfigureAwait(false);
        SourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<ToolsResult> ToolsAsync(CancellationToken cancellationToken) =>
        _solutions.RequestAsync(PackageMethods.Tools, new EmptyParams(), Json.EmptyParams, Json.ToolsResult, cancellationToken, s_toolTimeout);

    public Task<ToolCommandResult> RunToolCommandAsync(ToolCommandParams command, CancellationToken cancellationToken) =>
        _solutions.RequestAsync(PackageMethods.ToolCommand, command, Json.ToolCommandParams, Json.ToolCommandResult, cancellationToken, s_toolTimeout);
}
