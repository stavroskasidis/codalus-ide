using System.Collections.Concurrent;
using DaedalusStudio.Projects.Protocol;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Credentials;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using WirePackageDependency = DaedalusStudio.Projects.Protocol.PackageDependency;
using WirePackageDependencyGroup = DaedalusStudio.Projects.Protocol.PackageDependencyGroup;
using WirePackageSourceMapping = DaedalusStudio.Projects.Protocol.PackageSourceMapping;

namespace DaedalusStudio.Projects.Host.Packages;

/// <summary>
/// NuGet feeds through the SDK's NuGet client libraries (ADR 0012): search, metadata, READMEs, the latest versions and
/// the package sources of the solution's effective <c>NuGet.Config</c>. Credentials come from NuGet's cross-platform
/// credential-provider plugins, non-interactively.
/// </summary>
internal sealed class NuGetClient(string root, Func<string, string, Task> log)
{
    private const string ReadmeTemplateType = "ReadmeUriTemplate/6.13.0";
    private static int s_credentialsConfigured;

    private readonly ConcurrentDictionary<string, SourceRepository> _repositories = new(StringComparer.OrdinalIgnoreCase);
    private readonly NuGetLogger _logger = new(log);

    /// <summary>The folder whose <c>NuGet.Config</c> hierarchy applies (the solution's).</summary>
    public string Root { get; } = root;

    public ISettings LoadSettings() => Settings.LoadDefaultSettings(Root);

    private static void EnsureCredentials(ILogger logger)
    {
        if (Interlocked.Exchange(ref s_credentialsConfigured, 1) == 0)
        {
            DefaultCredentialServiceUtility.SetupDefaultCredentialService(logger, nonInteractive: true);
        }
    }

    private SourceRepository Repository(PackageSource source) =>
        _repositories.GetOrAdd(source.Name + "|" + source.Source, _ => NuGet.Protocol.Core.Types.Repository.Factory.GetCoreV3(source));

    /// <summary>
    /// Enabled sources in priority order (one, when <paramref name="name"/> is given). Offline: only local ones, plus the
    /// global packages folder.
    /// </summary>
    private List<PackageSource> Sources(ISettings settings, string? name, bool offline)
    {
        var sources = new PackageSourceProvider(settings).LoadPackageSources().Where(s => s.IsEnabled).ToList();
        if (name is not null)
        {
            sources = [.. sources.Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))];
            if (sources.Count == 0)
            {
                throw new InvalidOperationException($"There is no enabled package source named '{name}'.");
            }
        }

        if (offline)
        {
            sources = [.. sources.Where(s => s.IsLocal)];
            if (name is null)
            {
                sources.Add(new PackageSource(SettingsUtility.GetGlobalPackagesFolder(settings), "Global packages folder"));
            }
        }

        EnsureCredentials(_logger);
        return sources;
    }

    private static SourceCacheContext Cache(bool refresh) => new() { NoCache = refresh, DirectDownload = false };

    // ------------------------------------------------------------------ search

    public async Task<PackageSearchResult> SearchAsync(PackageSearchParams parameters, CancellationToken cancellationToken)
    {
        var settings = LoadSettings();
        var sources = Sources(settings, parameters.Source, parameters.Offline);
        using var cache = Cache(parameters.Refresh);
        var take = Math.Clamp(parameters.Take, 1, 100);
        var filter = new SearchFilter(parameters.Prerelease) { IncludeDelisted = false };
        var perSource = await Task.WhenAll(sources.Select(async source =>
        {
            try
            {
                var search = await Repository(source).GetResourceAsync<PackageSearchResource>(cancellationToken).ConfigureAwait(false)
                             ?? throw new InvalidOperationException("The source does not support search.");
                var results = await search.SearchAsync(parameters.Query, filter, Math.Max(0, parameters.Skip), take, _logger, cancellationToken).ConfigureAwait(false);
                return (Source: source, Results: results.ToList(), Error: (string?)null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (Source: source, Results: new List<IPackageSearchMetadata>(), Error: Describe(ex));
            }
        })).ConfigureAwait(false);

        // Merged by id, in the order of the highest-priority source that has the package; the highest version wins.
        var merged = new Dictionary<string, (int Order, IPackageSearchMetadata Metadata, string Source)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, results, _) in perSource)
        {
            foreach (var result in results)
            {
                if (!merged.TryGetValue(result.Identity.Id, out var existing))
                {
                    merged[result.Identity.Id] = (merged.Count, result, source.Name);
                }
                else if (result.Identity.Version > existing.Metadata.Identity.Version)
                {
                    merged[result.Identity.Id] = (existing.Order, result, source.Name);
                }
            }
        }

        var packages = new List<PackageSummary>();
        foreach (var (_, metadata, source) in merged.Values.OrderBy(m => m.Order))
        {
            var deprecation = await SafeDeprecationAsync(metadata).ConfigureAwait(false);
            packages.Add(new PackageSummary(
                metadata.Identity.Id,
                metadata.Identity.Version.ToNormalizedString(),
                Summary(metadata),
                NullIfEmpty(metadata.Authors),
                WebUrl(metadata.IconUrl),
                metadata.DownloadCount,
                deprecation is not null,
                Severity(metadata.Vulnerabilities),
                source));
        }

        return new PackageSearchResult(
            [.. packages],
            perSource.Any(s => s.Results.Count >= take),
            [.. perSource.Where(s => s.Error is not null).Select(s => new PackageSourceError(s.Source.Name, s.Error!))]);
    }

    // ------------------------------------------------------------------ details

    public async Task<PackageDetails> DetailsAsync(PackageDetailsParams parameters, CancellationToken cancellationToken)
    {
        var settings = LoadSettings();
        var sources = Sources(settings, parameters.Source, parameters.Offline);
        NuGetVersion? requested = parameters.Version is null ? null : NuGetVersion.Parse(parameters.Version);
        var prerelease = parameters.Prerelease || requested?.IsPrerelease == true;
        using var cache = Cache(refresh: false);

        // Every version from every source; the details come from the first source (by priority) that has the chosen one.
        var all = new List<(PackageSource Source, IPackageSearchMetadata Metadata)>();
        var errors = new List<string>();
        foreach (var source in sources)
        {
            try
            {
                var resource = await Repository(source).GetResourceAsync<PackageMetadataResource>(cancellationToken).ConfigureAwait(false);
                if (resource is null)
                {
                    continue;
                }

                var versions = await resource.GetMetadataAsync(parameters.Id, prerelease, includeUnlisted: false, cache, _logger, cancellationToken).ConfigureAwait(false);
                all.AddRange(versions.Select(v => (source, v)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{source.Name}: {Describe(ex)}");
            }
        }

        if (all.Count == 0)
        {
            throw new InvalidOperationException(errors.Count > 0
                ? $"Could not read {parameters.Id}: {string.Join("; ", errors)}"
                : $"No package source has {parameters.Id}.");
        }

        // A project's frameworks (M33): each version says whether it supports them, and the newest one that does is
        // offered first.
        var frameworks = FrameworkCompatibility.Parse(parameters.TargetFrameworks);
        var newestFirst = all.OrderByDescending(v => v.Metadata.Identity.Version).ToList();
        var chosen = requested is not null
            ? all.FirstOrDefault(v => v.Metadata.Identity.Version == requested)
            : newestFirst.FirstOrDefault(v => frameworks.Count > 0 && FrameworkCompatibility.Supports(v.Metadata.DependencySets.Select(s => s.TargetFramework), frameworks) == true
                                              && (parameters.Prerelease || !v.Metadata.Identity.Version.IsPrerelease))
              is { Metadata: not null } compatible ? compatible : newestFirst[0];
        if (chosen.Metadata is null)
        {
            throw new InvalidOperationException($"No package source has {parameters.Id} {parameters.Version}.");
        }

        var versionInfos = new List<PackageVersionInfo>();
        foreach (var group in all.GroupBy(v => v.Metadata.Identity.Version).OrderByDescending(g => g.Key))
        {
            var metadata = group.First().Metadata;
            versionInfos.Add(new PackageVersionInfo(
                group.Key.ToNormalizedString(),
                metadata.DownloadCount,
                await SafeDeprecationAsync(metadata).ConfigureAwait(false) is not null,
                Severity(metadata.Vulnerabilities),
                frameworks.Count == 0 ? null : FrameworkCompatibility.Supports(metadata.DependencySets.Select(s => s.TargetFramework), frameworks)));
        }

        var m = chosen.Metadata;
        var deprecation = await SafeDeprecationAsync(m).ConfigureAwait(false);
        var readme = await ReadmeAsync(settings, chosen.Source, m, cache, cancellationToken).ConfigureAwait(false);
        return new PackageDetails(
            m.Identity.Id,
            m.Identity.Version.ToNormalizedString(),
            [.. versionInfos],
            NullIfEmpty(m.Title),
            NullIfEmpty(m.Description),
            NullIfEmpty(m.Authors),
            m.LicenseMetadata?.Type == LicenseType.Expression ? m.LicenseMetadata.License : null,
            WebUrl(m.LicenseUrl),
            WebUrl(m.ProjectUrl),
            readme,
            m.RequireLicenseAcceptance,
            m.Published?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            m.DownloadCount,
            NullIfEmpty(m.Tags),
            [.. m.DependencySets.Select(set => new WirePackageDependencyGroup(
                set.TargetFramework.IsAny ? "any" : set.TargetFramework.GetShortFolderName(),
                [.. set.Packages.Select(d => new WirePackageDependency(d.Id, d.VersionRange.PrettyPrint()))]))],
            deprecation is null
                ? null
                : new PackageDeprecation(
                    [.. deprecation.Reasons ?? []],
                    NullIfEmpty(deprecation.Message),
                    deprecation.AlternatePackage?.PackageId,
                    deprecation.AlternatePackage?.Range?.PrettyPrint()),
            [.. (m.Vulnerabilities ?? []).Select(v => new PackageVulnerability(v.Severity, v.AdvisoryUrl.AbsoluteUri))],
            chosen.Source.Name);
    }

    /// <summary>
    /// The README: from the global packages folder when the package is there, else from the feed (the metadata's
    /// <c>readmeUrl</c> or the source's <c>ReadmeUriTemplate</c>), else from the <c>.nupkg</c> of a local source.
    /// </summary>
    private async Task<string?> ReadmeAsync(ISettings settings, PackageSource source, IPackageSearchMetadata metadata, SourceCacheContext cache, CancellationToken cancellationToken)
    {
        var identity = metadata.Identity;
        try
        {
            var resolver = new VersionFolderPathResolver(SettingsUtility.GetGlobalPackagesFolder(settings));
            var installed = resolver.GetInstallPath(identity.Id, identity.Version);
            var nuspec = resolver.GetManifestFilePath(identity.Id, identity.Version);
            if (File.Exists(nuspec) && new NuspecReader(nuspec).GetReadme() is { Length: > 0 } readmeFile
                && Path.Combine(installed, readmeFile.Replace('\\', Path.DirectorySeparatorChar)) is var path && File.Exists(path))
            {
                return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            }

            var repository = Repository(source);
            if (source.IsLocal)
            {
                var local = await repository.GetResourceAsync<FindLocalPackagesResource>(cancellationToken).ConfigureAwait(false);
                if (local?.GetPackage(identity, _logger, cancellationToken) is { } package && package.Nuspec.GetReadme() is { Length: > 0 } entry)
                {
                    using var reader = new PackageArchiveReader(package.Path);
                    using var stream = reader.GetStream(entry.Replace('\\', '/'));
                    using var text = new StreamReader(stream);
                    return await text.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                }

                return null;
            }

            var url = metadata.ReadmeUrl?.AbsoluteUri;
            if (url is null && await repository.GetResourceAsync<ServiceIndexResourceV3>(cancellationToken).ConfigureAwait(false) is { } index
                && index.GetServiceEntryUri(ReadmeTemplateType) is { } template)
            {
                url = template.OriginalString
                    .Replace("{lower_id}", identity.Id.ToLowerInvariant(), StringComparison.Ordinal)
                    .Replace("{lower_version}", identity.Version.ToNormalizedString().ToLowerInvariant(), StringComparison.Ordinal);
            }

            return url is null ? null : await GetTextAsync(repository, url, $"readme_{identity.Id}_{identity.Version.ToNormalizedString()}".ToLowerInvariant(), cache, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await log("debug", $"No README for {identity}: {ex.Message}").ConfigureAwait(false);
            return null;
        }
    }

    private async Task<string?> GetTextAsync(SourceRepository repository, string url, string cacheKey, SourceCacheContext cache, CancellationToken cancellationToken)
    {
        var http = await repository.GetResourceAsync<HttpSourceResource>(cancellationToken).ConfigureAwait(false);
        if (http?.HttpSource is not { } source)
        {
            return null;
        }

        var request = new HttpSourceCachedRequest(url, cacheKey, HttpSourceCacheContext.Create(cache, 0))
        {
            IgnoreNotFounds = true,
            MaxTries = 1,
        };
        return await source.GetAsync(
            request,
            async result =>
            {
                if (result.Status == HttpSourceResultStatus.NotFound || result.Stream is null)
                {
                    return null;
                }

                using var reader = new StreamReader(result.Stream);
                return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            },
            _logger,
            cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ status

    /// <summary>The latest version of each package across the sources, and the badges of the asked-for version.</summary>
    public async Task<PackageStatusResult> StatusAsync(PackageStatusParams parameters, CancellationToken cancellationToken)
    {
        var settings = LoadSettings();
        var sources = Sources(settings, null, parameters.Offline);
        using var cache = Cache(parameters.Refresh);
        var errors = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var results = new PackageStatus[parameters.Packages.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, parameters.Packages.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                var query = parameters.Packages[index];
                var version = NuGetVersion.TryParse(query.Version, out var parsed) ? parsed : null;
                var prerelease = parameters.Prerelease || version?.IsPrerelease == true;
                IPackageSearchMetadata? latest = null;
                IPackageSearchMetadata? current = null;
                foreach (var source in sources)
                {
                    try
                    {
                        var resource = await Repository(source).GetResourceAsync<PackageMetadataResource>(token).ConfigureAwait(false);
                        if (resource is null)
                        {
                            continue;
                        }

                        foreach (var metadata in await resource.GetMetadataAsync(query.Id, prerelease, includeUnlisted: false, cache, _logger, token).ConfigureAwait(false))
                        {
                            if (latest is null || metadata.Identity.Version > latest.Identity.Version)
                            {
                                latest = metadata;
                            }

                            if (version is not null && current is null && metadata.Identity.Version == version)
                            {
                                current = metadata;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        errors.TryAdd(source.Name, Describe(ex));
                    }
                }

                results[index] = new PackageStatus(
                    query.Id,
                    latest?.Identity.Version.ToNormalizedString(),
                    current is not null && await SafeDeprecationAsync(current).ConfigureAwait(false) is not null,
                    current is null ? -1 : Severity(current.Vulnerabilities),
                    WebUrl((latest ?? current)?.IconUrl));
            }).ConfigureAwait(false);
        return new PackageStatusResult(results, [.. errors.Select(e => new PackageSourceError(e.Key, e.Value))]);
    }

    // ------------------------------------------------------------------ sources

    public PackageSourcesResult GetSources()
    {
        var settings = LoadSettings();
        var configFiles = settings.GetConfigFilePaths().ToArray();
        var definedIn = DefiningFiles(configFiles);
        var sources = new PackageSourceProvider(settings).LoadPackageSources()
            .Select(s => new PackageSourceInfo(s.Name, s.Source, s.IsEnabled, s.IsMachineWide, definedIn.GetValueOrDefault(s.Name)))
            .ToArray();
        var mappings = new PackageSourceMappingProvider(settings).GetPackageSourceMappingItems()
            .Select(m => new WirePackageSourceMapping(m.Key, [.. m.Patterns.Select(p => p.Pattern)]))
            .ToArray();
        return new PackageSourcesResult(sources, mappings, configFiles, SettingsUtility.GetGlobalPackagesFolder(settings));
    }

    /// <summary>
    /// The config file that defines each source: the most specific one with an <c>add</c> for it, unless a more specific
    /// file <c>clear</c>s the list after it. <paramref name="configFiles"/> is NuGet's order, most specific first.
    /// </summary>
    private static Dictionary<string, string> DefiningFiles(IReadOnlyList<string> configFiles)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in configFiles)
        {
            try
            {
                var sources = System.Xml.Linq.XDocument.Load(file).Root?.Element("packageSources");
                if (sources is null)
                {
                    continue;
                }

                foreach (var add in sources.Elements("add"))
                {
                    if (add.Attribute("key")?.Value is { Length: > 0 } key)
                    {
                        result.TryAdd(key, file);
                    }
                }

                if (sources.Elements("clear").Any())
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
            {
            }
        }

        return result;
    }

    /// <summary>Saves the sources (order, enabled state, additions and removals) and the source mapping.</summary>
    public void SetSources(SetPackageSourcesParams parameters)
    {
        foreach (var source in parameters.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Name) || string.IsNullOrWhiteSpace(source.Url))
            {
                throw new ArgumentException("Every package source needs a name and a URL or folder.");
            }
        }

        if (parameters.Sources.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            throw new ArgumentException($"There are two package sources named '{duplicate.Key}'.");
        }

        var current = GetSources();
        var readOnly = current.Sources.Where(s => s.IsMachineWide && s.ConfigFile is not null).Select(s => s.ConfigFile!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wanted = parameters.Sources.Select(s => s with { Name = s.Name.Trim(), Url = s.Url.Trim() }).ToList();
        NuGetConfigEditor.Save(current.ConfigFiles, Root, readOnly, current.Sources, current.Mappings, wanted, parameters.Mappings);
        _repositories.Clear();
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<PackageDeprecationMetadata?> SafeDeprecationAsync(IPackageSearchMetadata metadata)
    {
        try
        {
            return await metadata.GetDeprecationMetadataAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static int Severity(IEnumerable<PackageVulnerabilityMetadata>? vulnerabilities) =>
        vulnerabilities?.Select(v => v.Severity).DefaultIfEmpty(-1).Max() ?? -1;

    private static string? Summary(IPackageSearchMetadata metadata)
    {
        var text = NullIfEmpty(metadata.Summary) ?? NullIfEmpty(metadata.Description);
        return text is { Length: > 400 } ? text[..400].TrimEnd() + "…" : text;
    }

    /// <summary>Only http(s) links reach the UI (local metadata can carry file and embedded-icon URIs).</summary>
    private static string? WebUrl(Uri? uri) =>
        uri is { IsAbsoluteUri: true } && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? uri.AbsoluteUri : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>NuGet wraps the useful message (401, DNS failure…) in a FatalProtocolException.</summary>
    internal static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null && inner is FatalProtocolException or AggregateException)
        {
            inner = inner.InnerException;
        }

        return inner == ex ? ex.Message : $"{ex.Message} {inner.Message}";
    }

    /// <summary>NuGet's log, forwarded to the host log (warnings and errors only; the rest is noise).</summary>
    private sealed class NuGetLogger(Func<string, string, Task> log) : LoggerBase
    {
        public override void Log(ILogMessage message)
        {
            if (message.Level >= LogLevel.Warning)
            {
                _ = log(message.Level == LogLevel.Warning ? "warning" : "error", "NuGet: " + message.Message);
            }
        }

        public override Task LogAsync(ILogMessage message)
        {
            Log(message);
            return Task.CompletedTask;
        }
    }
}
