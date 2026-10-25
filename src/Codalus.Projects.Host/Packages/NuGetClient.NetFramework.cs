using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Codalus.Projects.Host.Packages;

/// <summary>
/// What <c>dotnet restore</c> doesn't restore for non-SDK-style projects (M43a, ADR 0075): <c>packages.config</c>
/// packages (NuGet's packages.config restore is in the classic IDE's and nuget.exe's NuGet only, not the SDK's) and the
/// .NET Framework reference assemblies packages. Both come through the SDK's own NuGet client libraries, from the
/// solution's sources with its package source mapping and credentials, as NuGet itself would install them.
/// </summary>
internal sealed partial class NuGetClient
{
    /// <summary>A package to install: into <see cref="RepositoryPath"/> (packages.config's layout), or the global packages folder when it's null.</summary>
    public sealed record Install(string Id, string Version, string? RepositoryPath);

    /// <summary>
    /// Tells the reference assemblies targets where the global packages folder is, for <paramref name="directory"/>'s
    /// NuGet settings, at start: MSBuild's project collections read the environment once, when they're created.
    /// </summary>
    public static void PublishGlobalPackagesFolder(string directory)
    {
        try
        {
            NetFramework.SetPackageRoot(SettingsUtility.GetGlobalPackagesFolder(Settings.LoadDefaultSettings(directory)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"NuGet's settings couldn't be read: {ex.Message} Non-SDK-style .NET Framework projects won't find their reference assemblies.");
        }
    }

    /// <summary>NuGet's global packages folder for the solution's settings.</summary>
    public string GlobalPackagesFolder() => SettingsUtility.GetGlobalPackagesFolder(LoadSettings());

    /// <summary>NuGet's <c>repositoryPath</c> setting (where packages.config packages go), or null when it isn't set.</summary>
    public string? ConfiguredRepositoryPath() => SettingsUtility.GetRepositoryPath(LoadSettings());

    /// <summary>
    /// Installs <paramref name="packages"/> that aren't there yet, each from the first source that has it (the global
    /// packages folder first for packages.config's layout). Writes a line per package to <paramref name="output"/>;
    /// returns the number that failed.
    /// </summary>
    public async Task<int> InstallAsync(IReadOnlyList<Install> packages, Func<string, Task> output, CancellationToken cancellationToken)
    {
        if (packages.Count == 0)
        {
            return 0;
        }

        var settings = LoadSettings();
        var mapping = PackageSourceMapping.GetPackageSourceMapping(settings);
        var sources = Sources(settings, null, offline: false);
        var globalFolder = SettingsUtility.GetGlobalPackagesFolder(settings);
        var policy = ClientPolicyContext.GetClientPolicy(settings, _logger);
        using var cache = Cache(refresh: false);
        var failed = 0;
        foreach (var package in packages)
        {
            var identity = new PackageIdentity(package.Id, NuGetVersion.Parse(package.Version));
            var allowed = mapping.IsEnabled ? mapping.GetConfiguredPackageSources(package.Id) : null;
            var candidates = sources.Where(s => allowed is null || allowed.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            if (package.RepositoryPath is not null && Directory.Exists(globalFolder))
            {
                candidates.Insert(0, new PackageSource(globalFolder, "Global packages folder"));
            }

            string? installedFrom = null;
            string? lastError = null;
            foreach (var source in candidates)
            {
                try
                {
                    var finder = await Repository(source).GetResourceAsync<FindPackageByIdResource>(cancellationToken).ConfigureAwait(false);
                    using var downloader = finder is null ? null : await finder.GetPackageDownloaderAsync(identity, cache, _logger, cancellationToken).ConfigureAwait(false);
                    if (downloader is null)
                    {
                        continue;
                    }

                    if (package.RepositoryPath is null)
                    {
                        var context = new PackageExtractionContext(PackageSaveMode.Defaultv3, XmlDocFileSaveMode.None, policy, _logger);
                        await PackageExtractor.InstallFromSourceAsync(identity, downloader, new VersionFolderPathResolver(globalFolder), context, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        var temporary = Path.Combine(Path.GetTempPath(), "codalus-nupkg-" + Guid.NewGuid().ToString("N") + ".nupkg");
                        try
                        {
                            if (!await downloader.CopyNupkgFileToAsync(temporary, cancellationToken).ConfigureAwait(false))
                            {
                                continue;
                            }

                            await using var stream = File.OpenRead(temporary);
                            var context = new PackageExtractionContext(PackageSaveMode.Defaultv2, XmlDocFileSaveMode.None, policy, _logger);
                            await PackageExtractor.ExtractPackageAsync(source.Source, stream, new PackagePathResolver(package.RepositoryPath), context, cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            File.Delete(temporary);
                        }
                    }

                    installedFrom = source.Name;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = $"{source.Name}: {Describe(ex)}";
                }
            }

            if (installedFrom is null)
            {
                failed++;
                await output($"error: {package.Id} {package.Version} was not found in the package sources ({string.Join(", ", candidates.Select(c => c.Name))})."
                             + (lastError is null ? string.Empty : " " + lastError)).ConfigureAwait(false);
            }
            else
            {
                await output($"  Installed {package.Id} {package.Version} from {installedFrom}"
                             + (package.RepositoryPath is null ? " to the global packages folder." : $" to {package.RepositoryPath}.")).ConfigureAwait(false);
            }
        }

        return failed;
    }
}
