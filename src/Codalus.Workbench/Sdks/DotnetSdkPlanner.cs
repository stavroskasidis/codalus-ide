using System.Globalization;
using Codalus.Platform;
using Codalus.Workbench.Projects;
using Codalus.Workbench.Setup;

namespace Codalus.Workbench.Sdks;

/// <summary>What the SDK manager's lists and plans are made from (ADR 0113). Nothing in it runs anything.</summary>
public sealed record DotnetSdkFacts(IDotnetMachine Machine, IReadOnlyList<DotnetInstallation> Installations, DateOnly Today)
{
    /// <summary>The channels of <c>releases-index.json</c>; empty when it couldn't be read (the lists say less, nothing breaks).</summary>
    public IReadOnlyList<DotnetChannel> Channels { get; init; } = [];

    /// <summary>The channels' <c>releases.json</c> that were read, by channel.</summary>
    public IReadOnlyDictionary<string, DotnetChannelReleases> Releases { get; init; } = new Dictionary<string, DotnetChannelReleases>();

    /// <summary>What decides how to install here: winget, the distribution, the architecture.</summary>
    public DotnetInstallFacts Install { get; init; } = new(PlatformKind.Linux, string.Empty);

    /// <summary>The SDK the open solution builds with: its folder (<c>…/sdk/10.0.104</c>); the build host has it loaded.</summary>
    public string? SolutionSdk { get; init; }

    /// <summary>The SDK versions a recently opened solution's <c>global.json</c> pins exactly.</summary>
    public IReadOnlySet<string> Pinned { get; init; } = new HashSet<string>();

    /// <summary>
    /// Whether the IDE's own hosts would still have their runtime in a .NET folder without some of its runtimes; null:
    /// they would (tests that aren't about it).
    /// </summary>
    public Func<string, IReadOnlyCollection<DotnetRuntime>, bool>? HostsStillRun { get; init; }

    /// <summary>The installation the IDE uses, where new installs go; null when there's none yet.</summary>
    public DotnetInstallation? Target => Installations.FirstOrDefault(i => i.InUse) ?? (Installations.Count > 0 ? Installations[0] : null);

    public DotnetChannel? ChannelInfo(string channel) => Channels.FirstOrDefault(c => c.Version == channel);

    public DotnetChannelReleases? ReleasesOf(string channel) => Releases.TryGetValue(channel, out var releases) ? releases : null;
}

/// <summary>An installed SDK, as the manager lists it.</summary>
public sealed record InstalledSdk(DotnetInstallation Installation, string Version)
{
    public string Channel => DotnetSdkPlanner.ChannelOf(Version);

    public string Band => DotnetSdkPlanner.BandOf(Version);

    /// <summary>The newest SDK of the same feature band, when it's newer.</summary>
    public string? Update { get; init; }

    /// <summary>A release since this one, up to <see cref="Update"/>, fixes security issues.</summary>
    public bool SecurityUpdate { get; init; }

    public string Support { get; init; } = string.Empty;

    public bool OutOfSupport { get; init; }

    /// <summary>The open solution builds with it.</summary>
    public bool UsedBySolution { get; init; }
}

/// <summary>An installed runtime, as the manager lists it.</summary>
public sealed record InstalledRuntime(DotnetInstallation Installation, DotnetRuntime Runtime)
{
    public string Channel => DotnetSdkPlanner.ChannelOf(Runtime.Version);

    /// <summary>The newest patch of its channel, when it's newer.</summary>
    public string? Update { get; init; }

    public bool SecurityUpdate { get; init; }

    public string Support { get; init; } = string.Empty;

    public bool OutOfSupport { get; init; }
}

/// <summary>What can be installed: an SDK (<see cref="Framework"/> <c>sdk</c>) or a runtime alone, of a channel.</summary>
public sealed record AvailableDotnet(string Channel, string Framework, string Version)
{
    public const string Sdk = "sdk";

    public bool IsSdk => Framework == Sdk;

    public string Title => IsSdk ? $".NET SDK {Version}" : $"{DotnetFrameworks.Title(Framework)} {Version}";

    public string Support { get; init; } = string.Empty;

    public bool Preview { get; init; }

    public bool OutOfSupport { get; init; }
}

/// <summary>
/// What removing an SDK or a runtime does (ADR 0113): the folders deleted (a folder of the user's), or the command that
/// removes it (a package manager's); what stays, and why; or why it's refused.
/// </summary>
public sealed record DotnetRemoval(string What)
{
    /// <summary>Folders to delete, each inside the .NET folder (the user's folders).</summary>
    public IReadOnlyList<string> Folders { get; init; } = [];

    /// <summary>The command, for a folder a package manager or an administrator owns.</summary>
    public InstallPlan? Command { get; init; }

    /// <summary>The runtimes that stay, each with its reason, for the confirmation.</summary>
    public IReadOnlyList<string> Kept { get; init; } = [];

    /// <summary>Why it can't be removed; null when it can.</summary>
    public string? Refused { get; init; }

    /// <summary>The runtimes it removes.</summary>
    public IReadOnlyList<DotnetRuntime> Runtimes { get; init; } = [];
}

/// <summary>
/// The SDK manager's logic (ADR 0113): the installed SDKs and runtimes with their updates, what's available, and the
/// plans that install, update and remove. Pure: the shell shows a plan, asks, and runs it.
/// </summary>
public static class DotnetSdkPlanner
{
    private static readonly Comparer<string?> s_versions = Comparer<string?>.Create((a, b) => a is null ? (b is null ? 0 : -1) : b is null ? 1 : EnvironmentCheck.CompareVersions(a, b));

    /// <summary>A version's channel: <c>10.0.104</c> → <c>10.0</c>.</summary>
    public static string ChannelOf(string version) => InstallPlans.Channel(version, fallback: version);

    /// <summary>An SDK's feature band: <c>10.0.104</c> → <c>10.0.1xx</c>; <c>11.0.100-rc.2…</c> → <c>11.0.1xx</c>.</summary>
    public static string BandOf(string sdk) =>
        sdk.Split('-', 2)[0].Split('.') is [var major, var minor, var patch, ..] && int.TryParse(patch, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? $"{major}.{minor}.{number / 100}xx"
            : sdk;

    /// <summary>The band's folder name, as workloads use it: <c>10.0.104</c> → <c>10.0.100</c>.</summary>
    public static string BandFolderOf(string sdk) =>
        sdk.Split('-', 2)[0].Split('.') is [var major, var minor, var patch, ..] && int.TryParse(patch, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? $"{major}.{minor}.{number / 100 * 100}"
            : sdk;

    private static bool IsPrerelease(string version) => version.Contains('-', StringComparison.Ordinal);

    /// <summary>The installed SDKs of every folder, each with its update and support.</summary>
    public static IReadOnlyList<InstalledSdk> InstalledSdks(DotnetSdkFacts facts) =>
        [.. facts.Installations.SelectMany(i => i.Sdks.OrderByDescending(v => v, s_versions).Select(v => DescribeSdk(facts, i, v)))];

    private static InstalledSdk DescribeSdk(DotnetSdkFacts facts, DotnetInstallation installation, string version)
    {
        var (support, outOfSupport) = Support(facts, ChannelOf(version), IsPrerelease(version));
        var update = NewestInBand(facts, version);
        return new InstalledSdk(installation, version)
        {
            Update = update,
            SecurityUpdate = update is not null && SecuritySince(facts, ChannelOf(version), r => r.Sdks.Any(s => BandOf(s) == BandOf(version) && s_versions.Compare(s, version) > 0 && s_versions.Compare(s, update) <= 0)),
            Support = support,
            OutOfSupport = outOfSupport,
            UsedBySolution = facts.SolutionSdk is { } used && DotnetInstallations.SameFolder(used, Join(facts, installation.Root, "sdk", version), facts.Machine.Platform),
        };
    }

    /// <summary>The installed runtimes of every folder, each with its update and support.</summary>
    public static IReadOnlyList<InstalledRuntime> InstalledRuntimes(DotnetSdkFacts facts) =>
        [.. facts.Installations.SelectMany(i => i.Runtimes.OrderBy(r => Array.IndexOf(DotnetFrameworks.All, r.Framework)).ThenByDescending(r => r.Version, s_versions).Select(r =>
        {
            var channel = ChannelOf(r.Version);
            var (support, outOfSupport) = Support(facts, channel, IsPrerelease(r.Version));
            var newest = facts.ReleasesOf(channel)?.Releases.Select(x => x.VersionOf(r.Framework)).OfType<string>()
                .Where(v => IsPrerelease(r.Version) || !IsPrerelease(v)).DefaultIfEmpty().Max(s_versions);
            var update = newest is not null && s_versions.Compare(newest, r.Version) > 0 && !i.VersionsOf(r.Framework).Contains(newest) ? newest : null;
            return new InstalledRuntime(i, r)
            {
                Update = update,
                SecurityUpdate = update is not null && SecuritySince(facts, channel, x => x.VersionOf(r.Framework) is { } v && s_versions.Compare(v, r.Version) > 0 && s_versions.Compare(v, update) <= 0),
                Support = support,
                OutOfSupport = outOfSupport,
            };
        }))];

    /// <summary>
    /// The newest SDK of <paramref name="sdk"/>'s feature band, when it's newer than <paramref name="sdk"/> and not
    /// installed beside it already; null otherwise. A preview updates to a later preview or the release; a release only to a release.
    /// </summary>
    public static string? NewestInBand(DotnetSdkFacts facts, string sdk)
    {
        if (facts.ReleasesOf(ChannelOf(sdk)) is not { } releases)
        {
            return null;
        }

        var newest = releases.AllSdks.Where(s => BandOf(s) == BandOf(sdk) && (IsPrerelease(sdk) || !IsPrerelease(s))).DefaultIfEmpty().Max(s_versions);
        return newest is not null && s_versions.Compare(newest, sdk) > 0 && !facts.Installations.Any(i => i.Sdks.Contains(newest, StringComparer.OrdinalIgnoreCase)) ? newest : null;
    }

    private static bool SecuritySince(DotnetSdkFacts facts, string channel, Func<DotnetRelease, bool> since) =>
        facts.ReleasesOf(channel)?.Releases.Any(r => r.Security && since(r)) == true;

    /// <summary>A channel's support, as the rows say it: from the index, else the pinned dates (<see cref="DotnetSupport"/>).</summary>
    public static (string Text, bool OutOfSupport) Support(DotnetSdkFacts facts, string channel, bool preview)
    {
        if (facts.ChannelInfo(channel) is not { } info)
        {
            return DotnetSupport.Status(preview ? channel + ".0-preview" : channel + ".0", facts.Today);
        }

        var kind = info.ReleaseType?.ToUpperInvariant() is { Length: > 0 } type ? $" ({type})" : string.Empty;
        var date = info.EndOfLife?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (preview || info.IsPreview)
        {
            return ($"Preview{kind}", false);
        }

        if (info.IsOutOfSupport(facts.Today))
        {
            return (date is null ? "Out of support" : $"Out of support since {date}", true);
        }

        // Short enough for a table's column: the channel is in the version beside it.
        return (date is null ? $"Supported{kind}" : $"Until {date}{kind}", false);
    }

    /// <summary>
    /// What can be installed into the folder the IDE uses: for each channel shown, its newest SDK when none of the channel
    /// is there, else each newer feature band of its newest release; and its runtimes alone (.NET, ASP.NET Core; Windows
    /// Desktop on Windows) when the folder has none of that channel. Previews and channels out of support only when asked.
    /// </summary>
    public static IReadOnlyList<AvailableDotnet> Available(DotnetSdkFacts facts, bool showPreviews, bool showOutOfSupport)
    {
        var target = facts.Target;
        var installedSdks = facts.Installations.SelectMany(i => i.Sdks).ToList();
        var list = new List<AvailableDotnet>();
        foreach (var channel in facts.Channels)
        {
            var preview = channel.IsPreview;
            var outOfSupport = !preview && channel.IsOutOfSupport(facts.Today);
            if (preview && !showPreviews || outOfSupport && !showOutOfSupport)
            {
                continue;
            }

            var support = Support(facts, channel.Version, preview).Text;
            AvailableDotnet Row(string framework, string version) => new(channel.Version, framework, version) { Support = support, Preview = preview, OutOfSupport = outOfSupport };
            var ofChannel = installedSdks.Where(s => ChannelOf(s) == channel.Version).ToList();
            if (ofChannel.Count == 0)
            {
                if (channel.LatestSdk is { } latest)
                {
                    list.Add(Row(AvailableDotnet.Sdk, latest));
                }
            }
            else if (facts.ReleasesOf(channel.Version)?.Releases.FirstOrDefault(r => preview || !IsPrerelease(r.Version)) is { } newest)
            {
                var newestBand = ofChannel.Select(BandOf).Max(StringComparer.Ordinal)!;
                list.AddRange(newest.Sdks.Where(s => string.CompareOrdinal(BandOf(s), newestBand) > 0 && !installedSdks.Contains(s, StringComparer.OrdinalIgnoreCase))
                    .Order(s_versions).Select(s => Row(AvailableDotnet.Sdk, s)));
            }

            var latestRelease = facts.ReleasesOf(channel.Version)?.Releases.FirstOrDefault(r => preview || !IsPrerelease(r.Version));
            foreach (var framework in DotnetFrameworks.All)
            {
                if (framework == DotnetFrameworks.WindowsDesktop && facts.Machine.Platform != PlatformKind.Windows)
                {
                    continue;
                }

                if (target?.VersionsOf(framework).Any(v => ChannelOf(v) == channel.Version) == true)
                {
                    continue;
                }

                // The index names the .NET runtime only; ASP.NET Core's and Windows Desktop's have its number since .NET 5.
                if ((latestRelease?.VersionOf(framework) ?? channel.LatestRuntime) is { } version)
                {
                    list.Add(Row(framework, version));
                }
            }
        }

        return list;
    }

    /// <summary>
    /// The ways to install <paramref name="framework"/> (<see cref="AvailableDotnet.Sdk"/> or a runtime) at
    /// <paramref name="version"/> into <paramref name="into"/> (the folder the IDE uses when null), the one that fits
    /// first: that folder's own way, and <c>dotnet-install</c> into <c>~/.dotnet</c> when the folder's way can't install
    /// that exact version (a distribution's package, the snap).
    /// </summary>
    public static IReadOnlyList<InstallPlan> InstallOptions(DotnetSdkFacts facts, string framework, string version, DotnetInstallation? into = null)
    {
        var target = into ?? facts.Target;
        var platform = facts.Machine.Platform;
        var what = framework == AvailableDotnet.Sdk ? $".NET SDK {version}" : $"{DotnetFrameworks.Title(framework)} {version}";
        var options = new List<InstallPlan>();
        var userRoot = UserRoot(facts);
        switch (target?.Owner)
        {
            // No .NET yet: the system's own way first, as Check Environment recommends it (M37), then dotnet-install below.
            case null when platform == PlatformKind.Linux && DistributionPackage(framework, ChannelOf(version)) is { } firstPackage && InstallPlans.Package(facts.Install.LinuxDistribution, firstPackage) is { } firstCommand:
                options.Add(new InstallPlan("dotnet", InstallKind.Terminal, $"{firstPackage} from the distribution's packages (sudo asks for your password)", firstCommand)
                {
                    Option = "package",
                    Title = $"{firstPackage} from the distribution's packages",
                    Note = $"The distribution's newest {firstPackage}, which may not be {version}.",
                });
                break;
            case null when platform == PlatformKind.MacOS && (framework == AvailableDotnet.Sdk || framework == DotnetFrameworks.NetCore):
                options.Add(MacPackage(facts, framework, version, what, InstallPlans.MacPackageRoot));
                break;
            case null when platform == PlatformKind.Windows && facts.Install.HasWinget:
            case DotnetRootOwner.WindowsInstaller when facts.Install.HasWinget:
                options.Add(Winget(facts, framework, version, what, target?.Root ?? facts.Install.WindowsRoot));
                break;
            case DotnetRootOwner.WindowsInstaller:
                options.Add(new InstallPlan("dotnet", InstallKind.OpenPage, $"{what}: the .NET download page (winget isn't available)", InstallPlans.DotnetDownloadPage) { Option = "page", Title = "The .NET download page" });
                break;
            case DotnetRootOwner.User:
                options.Add(DotnetInstall(facts, framework, version, what, target!.Root, sudo: false));
                break;
            case DotnetRootOwner.Administrator:
                options.Add(DotnetInstall(facts, framework, version, what, target!.Root, sudo: true));
                break;
            case DotnetRootOwner.MacPackage when framework == AvailableDotnet.Sdk || framework == DotnetFrameworks.NetCore:
                options.Add(MacPackage(facts, framework, version, what, target!.Root));
                break;
            case DotnetRootOwner.MacPackage:
                options.Add(DotnetInstall(facts, framework, version, what, target!.Root, sudo: true));
                break;
            case DotnetRootOwner.LinuxPackage when DistributionPackage(framework, ChannelOf(version)) is { } package && InstallPlans.Package(facts.Install.LinuxDistribution, package) is { } command:
                options.Add(new InstallPlan("dotnet", InstallKind.Terminal, $"{package} from the distribution's packages (sudo asks for your password)", command)
                {
                    Option = "package",
                    Title = $"{package} from the distribution's packages",
                    Note = $"The distribution's newest {package}, which may not be {version}.",
                    AddsToPath = target!.Root,
                });
                break;
        }

        // The user's own folder: always possible without a password, and the only way to an exact version from a package's folder.
        if (platform != PlatformKind.Windows && (options.Count == 0 || target is null || target.Owner is DotnetRootOwner.LinuxPackage) && target?.Owner is not DotnetRootOwner.User)
        {
            options.Add(DotnetInstall(facts, framework, version, what, userRoot, sudo: false) with
            {
                Note = target is null ? null : $"Codalus IDE uses the .NET in {target.Root}; this goes into {userRoot} beside it, which Codalus IDE then uses.",
            });
        }
        else if (platform == PlatformKind.Windows && options.Count == 0)
        {
            options.Add(DotnetInstall(facts, framework, version, what, userRoot, sudo: false));
        }

        return [.. options.Select((o, i) => o with { Recommended = i == 0 })];
    }

    /// <summary>
    /// How to update an installed SDK or runtime to <paramref name="newVersion"/> in its own folder: the folder's own way
    /// (<see cref="InstallOptions"/>); a distribution's package upgrades the package that holds it
    /// (<paramref name="ownerPackage"/>, <see cref="PackageOwnerQuery"/>), and the snap refreshes.
    /// </summary>
    public static IReadOnlyList<InstallPlan> UpdateOptions(DotnetSdkFacts facts, DotnetInstallation installation, string framework, string version, string newVersion, string? ownerPackage)
    {
        var what = framework == AvailableDotnet.Sdk ? $".NET SDK {version} to {newVersion}" : $"{DotnetFrameworks.Title(framework)} {version} to {newVersion}";
        switch (installation.Owner)
        {
            case DotnetRootOwner.LinuxPackage when ownerPackage is not null && UpgradeCommand(facts.Install.LinuxDistribution, ownerPackage) is { } upgrade:
                return [new InstallPlan("dotnet", InstallKind.Terminal, $"{ownerPackage} from the distribution's packages (sudo asks for your password)", upgrade)
                {
                    Option = "package",
                    Title = $"Upgrade {ownerPackage}",
                    Note = $"The distribution's newest {ownerPackage}, which may not be {newVersion}.",
                    Recommended = true,
                    AddsToPath = installation.Root,
                }];
            case DotnetRootOwner.Snap:
                return [new InstallPlan("dotnet", InstallKind.Terminal, "The dotnet-sdk snap, refreshed (sudo asks for your password)", "sudo snap refresh dotnet-sdk")
                {
                    Option = "snap",
                    Title = "Refresh the dotnet-sdk snap",
                    Recommended = true,
                }];
            default:
                return [.. InstallOptions(facts, framework, newVersion, installation).Take(1).Select(p => p with { Summary = $"{what}: {p.Summary}" })];
        }
    }

    /// <summary>
    /// Removing an SDK (ADR 0113): <c>sdk/&lt;version&gt;</c>, and the runtimes only it brought with their packs and
    /// templates. A runtime stays when another SDK left here brought it, when it's the newest of its channel here, or when
    /// the IDE's hosts would lose their runtime. Refused for the open solution's SDK and the last SDK of the IDE's folder.
    /// </summary>
    /// <param name="ownerPackage">The distribution's package that holds it (<see cref="PackageOwnerQuery"/>), for a package's folder.</param>
    public static DotnetRemoval RemoveSdk(DotnetSdkFacts facts, DotnetInstallation installation, string sdk, string? ownerPackage = null)
    {
        var removal = RemoveSdkCore(facts, installation, sdk, [], []);
        return removal.Refused is null ? WithCommand(facts, installation, removal, AvailableDotnet.Sdk, sdk, ownerPackage) : removal;
    }

    /// <summary>
    /// Remove Old Patches (ADR 0113): several SDKs of one folder at once, under the same guards, as one removal: one list
    /// of folders, or one command. The ones refused are named in <see cref="DotnetRemoval.Kept"/>; all refused, it's refused.
    /// </summary>
    public static DotnetRemoval RemoveSdks(DotnetSdkFacts facts, DotnetInstallation installation, IReadOnlyList<string> sdks)
    {
        var what = sdks.Count == 1 ? $".NET SDK {sdks[0]}" : $".NET SDKs {string.Join(", ", sdks)}";
        var refused = sdks.Select(s => (Sdk: s, Removal: RemoveSdkCore(facts, installation, s, [], []))).Where(r => r.Removal.Refused is not null).ToList();
        var going = sdks.Except(refused.Select(r => r.Sdk), StringComparer.OrdinalIgnoreCase).ToList();
        if (going.Count == 0)
        {
            return new(what) { Refused = string.Join(" ", refused.Select(r => r.Removal.Refused)) };
        }

        if (installation.InUse && installation.Sdks.All(s => going.Contains(s, StringComparer.OrdinalIgnoreCase)))
        {
            return new(what) { Refused = $"These are the last SDKs in {installation.Root}, the .NET Codalus IDE uses: install another one first." };
        }

        var folders = new List<string>();
        var kept = new List<string>(refused.Select(r => r.Removal.Refused!));
        var runtimes = new List<DotnetRuntime>();
        foreach (var sdk in going)
        {
            var one = RemoveSdkCore(facts, installation, sdk, going, runtimes);
            if (one.Refused is { } why)
            {
                kept.Add(why);
                continue;
            }

            folders.AddRange(one.Folders);
            kept.AddRange(one.Kept);
            runtimes.AddRange(one.Runtimes);
        }

        var combined = new DotnetRemoval(going.Count == 1 ? $".NET SDK {going[0]}" : $".NET SDKs {string.Join(", ", going)}")
        {
            Folders = [.. folders.Distinct(StringComparer.Ordinal)],
            Kept = [.. kept.Distinct(StringComparer.Ordinal)],
            Runtimes = runtimes,
        };
        return installation.Owner is DotnetRootOwner.User or DotnetRootOwner.Administrator or DotnetRootOwner.MacPackage
            ? WithCommand(facts, installation, combined, AvailableDotnet.Sdk, going[0], null)
            : combined with { Refused = $"{combined.What} came with a package manager: remove them one at a time." };
    }

    private static DotnetRemoval RemoveSdkCore(DotnetSdkFacts facts, DotnetInstallation installation, string sdk, IReadOnlyCollection<string> alsoRemoving, IReadOnlyCollection<DotnetRuntime> runtimesRemoved)
    {
        var what = $".NET SDK {sdk}";
        var sdkFolder = Join(facts, installation.Root, "sdk", sdk);
        if (facts.SolutionSdk is { } used && DotnetInstallations.SameFolder(used, sdkFolder, facts.Machine.Platform))
        {
            return new(what) { Refused = $"The open solution builds with {what}: close the solution, or pin another SDK in its global.json first." };
        }

        var gone = new HashSet<string>(alsoRemoving, StringComparer.OrdinalIgnoreCase) { sdk };
        var left = installation.Sdks.Where(s => !gone.Contains(s)).ToList();
        if (installation.InUse && left.Count == 0)
        {
            return new(what) { Refused = $"{what} is the last SDK in {installation.Root}, the .NET Codalus IDE uses: install another one first." };
        }

        var kept = new List<string>();
        var removed = new List<DotnetRuntime>(runtimesRemoved);
        var newRuntimes = new List<DotnetRuntime>();
        var folders = new List<string> { sdkFolder };
        if (facts.ReleasesOf(ChannelOf(sdk))?.ReleaseOfSdk(sdk) is { } release)
        {
            foreach (var framework in DotnetFrameworks.All)
            {
                if (release.VersionOf(framework) is not { } version || !installation.VersionsOf(framework).Contains(version, StringComparer.OrdinalIgnoreCase)
                    || removed.Contains(new DotnetRuntime(framework, version)))
                {
                    continue;
                }

                var title = $"{DotnetFrameworks.Title(framework)} {version}";
                var runtime = new DotnetRuntime(framework, version);
                if (left.Any(s => RuntimeOfSdk(facts, s, framework) == version))
                {
                    kept.Add($"{title} stays: another SDK here brought it too.");
                }
                else if (installation.VersionsOf(framework).Where(v => ChannelOf(v) == ChannelOf(version) && !removed.Contains(new DotnetRuntime(framework, v))).Max(s_versions) == version)
                {
                    kept.Add($"{title} stays: it's the newest .NET {ChannelOf(version)} runtime here, which its projects run on. Remove it from the runtimes if you don't need it.");
                }
                else if (!HostsStillRun(facts, installation, [.. removed, runtime]))
                {
                    kept.Add($"{title} stays: Codalus IDE's helper processes run on it.");
                }
                else
                {
                    removed.Add(runtime);
                    newRuntimes.Add(runtime);
                    folders.AddRange(RuntimeFolders(facts, installation, runtime, withPacks: true));
                }
            }
        }
        else
        {
            kept.Add($"The runtimes stay: the .NET release metadata doesn't list {what}, so which ones it brought isn't known.");
        }

        return new DotnetRemoval(what) { Folders = [.. folders.Where(facts.Machine.DirectoryExists)], Kept = kept, Runtimes = newRuntimes };
    }

    /// <summary>
    /// Removing a runtime from the runtimes list: refused when the IDE's hosts run on it, or when an SDK left here needs
    /// it (an SDK runs on its own channel's runtime, at least the one it brought). Its packs and templates go only when no
    /// SDK here brought it.
    /// </summary>
    public static DotnetRemoval RemoveRuntime(DotnetSdkFacts facts, DotnetInstallation installation, DotnetRuntime runtime, string? ownerPackage = null)
    {
        var what = $"{DotnetFrameworks.Title(runtime.Framework)} {runtime.Version}";
        if (!HostsStillRun(facts, installation, [runtime]))
        {
            return new(what) { Refused = $"Codalus IDE's helper processes run on the {what}." };
        }

        if (runtime.Framework == DotnetFrameworks.NetCore)
        {
            var others = installation.VersionsOf(runtime.Framework).Where(v => v != runtime.Version).ToList();
            var needs = installation.Sdks.FirstOrDefault(s => RuntimeOfSdk(facts, s, runtime.Framework) is { } own && ChannelOf(own) == ChannelOf(runtime.Version)
                && !others.Any(v => ChannelOf(v) == ChannelOf(own) && s_versions.Compare(v, own) >= 0));
            if (needs is not null)
            {
                return new(what) { Refused = $"The .NET SDK {needs} runs on the {what}: remove that SDK first." };
            }
        }

        var brought = installation.Sdks.Any(s => RuntimeOfSdk(facts, s, runtime.Framework) == runtime.Version);
        var folders = RuntimeFolders(facts, installation, runtime, withPacks: !brought).Where(facts.Machine.DirectoryExists).ToList();
        return WithCommand(facts, installation, new DotnetRemoval(what) { Folders = folders, Runtimes = [runtime] }, runtime.Framework, runtime.Version, ownerPackage);
    }

    /// <summary>Remove Old Patches: the SDKs of a channel in a folder that have a newer one of their band there.</summary>
    public static IReadOnlyList<string> OldPatches(DotnetInstallation installation, string channel) =>
        [.. installation.Sdks.Where(s => ChannelOf(s) == channel && installation.Sdks.Any(o => BandOf(o) == BandOf(s) && s_versions.Compare(o, s) > 0)).Order(s_versions)];

    /// <summary>
    /// The workloads to offer for a newly installed SDK's band (ADR 0113): none when its band has some already; else those
    /// of the newest older band of the same channel that has any (<c>metadata/workloads/&lt;band&gt;/InstalledWorkloads</c>).
    /// </summary>
    public static IReadOnlyList<string> WorkloadsToCarry(IDotnetMachine machine, DotnetInstallation installation, string newSdk)
    {
        string Installed(string band) => string.Join(machine.Platform == PlatformKind.Windows ? '\\' : '/', installation.Root, "metadata", "workloads", band, "InstalledWorkloads");
        var band = BandFolderOf(newSdk);
        if (machine.DirectoryExists(Installed(band)) && Files(machine, Installed(band)).Count > 0)
        {
            return [];
        }

        var older = machine.Subfolders(string.Join(machine.Platform == PlatformKind.Windows ? '\\' : '/', installation.Root, "metadata", "workloads"))
            .Where(b => DotnetInstallations.IsVersion(b) && ChannelOf(b) == ChannelOf(newSdk) && s_versions.Compare(b, band) < 0)
            .OrderByDescending(b => b, s_versions);
        foreach (var candidate in older)
        {
            if (Files(machine, Installed(candidate)) is { Count: > 0 } workloads)
            {
                return workloads;
            }
        }

        return [];
    }

    /// <summary>A band's workload records: a file named by each workload.</summary>
    private static IReadOnlyList<string> Files(IDotnetMachine machine, string folder) => [.. machine.Files(folder).Order(StringComparer.Ordinal)];

    /// <summary>
    /// The command that asks the distribution which package holds a path (<see cref="PackageOwner"/> reads its answer):
    /// <c>dpkg -S</c>, <c>rpm -qf</c>, <c>pacman -Qoq</c>. Null for other distributions.
    /// </summary>
    public static (string Program, string[] Arguments)? PackageOwnerQuery(string? distribution, string path)
    {
        var ids = (distribution ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return true switch
        {
            _ when ids.Any(i => i is "debian" or "ubuntu") => ("dpkg", ["-S", path]),
            _ when ids.Any(i => i is "fedora" or "rhel" or "centos" or "opensuse" or "suse" or "opensuse-tumbleweed" or "opensuse-leap") => ("rpm", ["-qf", "--queryformat", "%{NAME}\n", path]),
            _ when ids.Any(i => i is "arch") => ("pacman", ["-Qoq", path]),
            _ => null,
        };
    }

    /// <summary>The package's name in a <see cref="PackageOwnerQuery"/> answer (dpkg's <c>pkg: path</c>, rpm's and pacman's name), or null.</summary>
    public static string? PackageOwner(string output)
    {
        var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line is null || line.Contains("not owned", StringComparison.OrdinalIgnoreCase) || line.Contains("no path found", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("error", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = line.Contains(": ", StringComparison.Ordinal) ? line[..line.IndexOf(": ", StringComparison.Ordinal)].Split(',')[0].Trim() : line;
        return name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '+' or '_' or ':') ? name : null;
    }

    /// <summary>The distribution's command that upgrades a package, from <c>/etc/os-release</c>'s ID and ID_LIKE.</summary>
    public static string? UpgradeCommand(string? distribution, string package) => PackageManagerCommand(distribution, package, upgrade: true);

    /// <summary>The distribution's command that removes a package.</summary>
    public static string? RemoveCommand(string? distribution, string package) => PackageManagerCommand(distribution, package, upgrade: false);

    private static string? PackageManagerCommand(string? distribution, string package, bool upgrade)
    {
        var ids = (distribution ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Is(params string[] names) => ids.Any(names.Contains);
        return true switch
        {
            _ when Is("debian", "ubuntu") => upgrade ? $"sudo apt-get install --only-upgrade -y {package}" : $"sudo apt-get remove -y {package}",
            _ when Is("fedora", "rhel", "centos") => upgrade ? $"sudo dnf upgrade -y {package}" : $"sudo dnf remove -y {package}",
            _ when Is("opensuse", "suse", "opensuse-tumbleweed", "opensuse-leap") => upgrade ? $"sudo zypper update -y {package}" : $"sudo zypper remove -y {package}",
            // Arch upgrades the whole system or nothing (partial upgrades are unsupported); pacman lists what it would do first.
            _ when Is("arch") => upgrade ? "sudo pacman -Syu" : $"sudo pacman -R {package}",
            _ when Is("alpine") => upgrade ? $"sudo apk upgrade {package}" : $"sudo apk del {package}",
            _ => null,
        };
    }

    /// <summary>The distribution's package of a channel's SDK or runtime: <c>dotnet-sdk-10.0</c>, <c>dotnet-runtime-10.0</c>, <c>aspnetcore-runtime-10.0</c>.</summary>
    private static string? DistributionPackage(string framework, string channel) => framework switch
    {
        AvailableDotnet.Sdk => $"dotnet-sdk-{channel}",
        DotnetFrameworks.NetCore => $"dotnet-runtime-{channel}",
        DotnetFrameworks.AspNetCore => $"aspnetcore-runtime-{channel}",
        _ => null,
    };

    /// <summary>The runtime an SDK brought, from its release; null when the metadata doesn't list it.</summary>
    public static string? RuntimeOfSdk(DotnetSdkFacts facts, string sdk, string framework) =>
        facts.ReleasesOf(ChannelOf(sdk))?.ReleaseOfSdk(sdk)?.VersionOf(framework);

    private static bool HostsStillRun(DotnetSdkFacts facts, DotnetInstallation installation, IReadOnlyCollection<DotnetRuntime> removed) =>
        facts.HostsStillRun?.Invoke(installation.Root, removed) ?? true;

    /// <summary>A runtime's folders: <c>shared/&lt;framework&gt;/&lt;v&gt;</c>, and with its packs its reference and app host packs and templates.</summary>
    private static IEnumerable<string> RuntimeFolders(DotnetSdkFacts facts, DotnetInstallation installation, DotnetRuntime runtime, bool withPacks)
    {
        yield return Join(facts, installation.Root, "shared", runtime.Framework, runtime.Version);
        if (!withPacks)
        {
            yield break;
        }

        yield return Join(facts, installation.Root, "packs", runtime.Framework + ".Ref", runtime.Version);
        if (runtime.Framework == DotnetFrameworks.NetCore)
        {
            foreach (var host in facts.Machine.Subfolders(Join(facts, installation.Root, "packs")).Where(p => p.StartsWith("Microsoft.NETCore.App.Host.", StringComparison.Ordinal)))
            {
                yield return Join(facts, installation.Root, "packs", host, runtime.Version);
            }

            yield return Join(facts, installation.Root, "templates", runtime.Version);
        }
    }

    /// <summary>For a folder that isn't the user's, the command that removes it instead of deleting its folders.</summary>
    private static DotnetRemoval WithCommand(DotnetSdkFacts facts, DotnetInstallation installation, DotnetRemoval removal, string framework, string version, string? ownerPackage)
    {
        switch (installation.Owner)
        {
            case DotnetRootOwner.User:
                return removal;
            case DotnetRootOwner.LinuxPackage when ownerPackage is not null && RemoveCommand(facts.Install.LinuxDistribution, ownerPackage) is { } remove:
                return removal with
                {
                    Command = new InstallPlan("dotnet", InstallKind.Terminal, $"{ownerPackage} removed with the distribution's package manager (sudo asks for your password)", remove) { Option = "package" },
                    Kept = [$"The package manager removes what {ownerPackage} installed, which may be more or less than the folders listed."],
                };
            case DotnetRootOwner.LinuxPackage:
                return removal with { Refused = $"{removal.What} came with a package of your distribution, but which one couldn't be told: remove it with your package manager." };
            case DotnetRootOwner.Snap:
                return removal with
                {
                    Command = new InstallPlan("dotnet", InstallKind.Terminal, "The dotnet-sdk snap removed (sudo asks for your password)", "sudo snap remove dotnet-sdk") { Option = "snap" },
                    Kept = ["The snap holds one SDK and its runtimes: removing it removes all of them."],
                };
            case DotnetRootOwner.WindowsInstaller when facts.Install.HasWinget:
                var id = WingetId(framework, version);
                string[] arguments = ["uninstall", "--id", id, "--exact", "--version", version, "--source", "winget"];
                return removal with
                {
                    Command = new InstallPlan("dotnet", InstallKind.Run, $"{removal.What} uninstalled with winget (Windows asks for administrator rights)", "winget " + string.Join(' ', arguments), "winget", arguments) { Option = "winget" },
                    Kept = ["Its installer removes the runtimes it installed when nothing else uses them."],
                };
            case DotnetRootOwner.WindowsInstaller:
                return removal with { Refused = $"{removal.What} came with Microsoft's installer: remove it in Settings → Apps (winget isn't available)." };
            default:
                // An administrator's folder, or Microsoft's .pkg (which has no uninstaller): the same folders, deleted with sudo.
                var quoted = string.Join(' ', removal.Folders.Select(f => "'" + f.Replace("'", "'\\''", StringComparison.Ordinal) + "'"));
                return removal with
                {
                    Command = removal.Folders.Count == 0 ? null : new InstallPlan("dotnet", InstallKind.Terminal, $"{removal.What} deleted with sudo (sudo asks for your password)", "sudo rm -rf " + quoted) { Option = "sudo" },
                };
        }
    }

    /// <summary>The user's own .NET folder: <c>~/.dotnet</c>, or <c>%LOCALAPPDATA%\Microsoft\dotnet</c> (dotnet-install.ps1's).</summary>
    public static string UserRoot(DotnetSdkFacts facts)
    {
        var home = facts.Machine.Home.TrimEnd('/', '\\');
        return facts.Machine.Platform == PlatformKind.Windows ? home + @"\AppData\Local\Microsoft\dotnet" : home + "/.dotnet";
    }

    private static InstallPlan DotnetInstall(DotnetSdkFacts facts, string framework, string version, string what, string root, bool sudo)
    {
        var runtime = framework == AvailableDotnet.Sdk ? null : DotnetFrameworks.InstallScriptName(framework);
        var architecture = facts.Install.EmulatedX64 ? " --architecture x64" : string.Empty;
        if (facts.Machine.Platform == PlatformKind.Windows)
        {
            var ps = $"& ([scriptblock]::Create((Invoke-WebRequest -UseBasicParsing https://dot.net/v1/dotnet-install.ps1))) -Version {version}{(runtime is null ? string.Empty : " -Runtime " + runtime)}{(facts.Install.EmulatedX64 ? " -Architecture x64" : string.Empty)} -InstallDir '{root.Replace("'", "''", StringComparison.Ordinal)}'";
            string[] psArguments = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", ps];
            return new InstallPlan("dotnet", InstallKind.Run, $"{what} into {root}", "powershell " + string.Join(' ', psArguments[..^1]) + " \"" + ps + "\"", "powershell", psArguments)
            {
                AddsToPath = root,
                Option = "dotnet-install",
                Title = $"{what} into {root} with dotnet-install",
            };
        }

        var script = $"curl -sSL {InstallPlans.DotnetInstallScript} | {(sudo ? "sudo " : string.Empty)}bash -s -- --version {version}{(runtime is null ? string.Empty : " --runtime " + runtime)}{architecture} --install-dir \"{root}\"";
        return sudo
            ? new InstallPlan("dotnet", InstallKind.Terminal, $"{what} into {root} (sudo asks for your password)", script) { AddsToPath = root, Option = "sudo", Title = $"{what} into {root} with dotnet-install and sudo" }
            : new InstallPlan("dotnet", InstallKind.Run, $"{what} into {root}", script, "bash", ["-c", script]) { AddsToPath = root, Option = "dotnet-install", Title = $"{what} into {root} with dotnet-install" };
    }

    private static InstallPlan MacPackage(DotnetSdkFacts facts, string framework, string version, string what, string root)
    {
        var architecture = facts.Install.EmulatedX64 || !facts.Install.Arm64 ? "x64" : "arm64";
        var url = framework == AvailableDotnet.Sdk
            ? $"https://builds.dotnet.microsoft.com/dotnet/Sdk/{version}/dotnet-sdk-{version}-osx-{architecture}.pkg"
            : $"https://builds.dotnet.microsoft.com/dotnet/Runtime/{version}/dotnet-runtime-{version}-osx-{architecture}.pkg";
        var command = $"pkg=\"$(mktemp -d)/dotnet.pkg\" && curl -fSL -o \"$pkg\" {url} && sudo installer -pkg \"$pkg\" -target /";
        return new InstallPlan("dotnet", InstallKind.Terminal, $"{what} into {root}, with Microsoft's installer package (sudo asks for your password)", command)
        {
            AddsToPath = root,
            Option = "pkg",
            Title = $"{what} with Microsoft's installer package",
        };
    }

    private static InstallPlan Winget(DotnetSdkFacts facts, string framework, string version, string what, string root)
    {
        string[] arguments = ["install", "--id", WingetId(framework, version), "--exact", "--version", version, .. facts.Install.EmulatedX64 ? new[] { "--architecture", "x64" } : [], "--source", "winget", "--accept-package-agreements", "--accept-source-agreements"];
        return new InstallPlan("dotnet", InstallKind.Run, $"{what} into {root} with winget (Windows asks for administrator rights)", "winget " + string.Join(' ', arguments), "winget", arguments)
        {
            Option = "winget",
            Title = $"{what} with winget",
        };
    }

    /// <summary>winget's package of an SDK or runtime: <c>Microsoft.DotNet.SDK.10</c>, <c>Microsoft.DotNet.AspNetCore.10</c>; a preview's <c>.Preview</c>.</summary>
    public static string WingetId(string framework, string version)
    {
        var kind = framework switch
        {
            AvailableDotnet.Sdk => "SDK",
            DotnetFrameworks.AspNetCore => "AspNetCore",
            DotnetFrameworks.WindowsDesktop => "DesktopRuntime",
            _ => "Runtime",
        };
        return $"Microsoft.DotNet.{kind}.{(IsPrerelease(version) ? "Preview" : version.Split('.')[0])}";
    }

    private static string Join(DotnetSdkFacts facts, params string[] parts) => string.Join(facts.Machine.Platform == PlatformKind.Windows ? '\\' : '/', parts);
}
