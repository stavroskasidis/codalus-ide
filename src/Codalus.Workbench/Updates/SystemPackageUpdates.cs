using System.ComponentModel;
using Codalus.Workbench.Setup;

namespace Codalus.Workbench.Updates;

/// <summary>Why a <see cref="SystemPackageUpdate"/> is what it is: what the notice says.</summary>
public enum SystemPackageUpdateKind
{
    /// <summary>The package manager has the release: <see cref="SystemPackageUpdate.Command"/> is its own upgrade.</summary>
    Upgrade,

    /// <summary>Arch Linux: the install command run again (<see cref="InstallInfo.UpdateCommand"/>).</summary>
    InstallCommand,

    /// <summary>
    /// A nightly build. The repositories' nightly channel is rebuilt with each stable release, not with each nightly
    /// (<c>pages.yml</c>), so it can be older than the offer, and older than what's installed: the release's package.
    /// </summary>
    Nightly,

    /// <summary>A stable release, but no enabled repository of stable releases: the release's package.</summary>
    NoRepository,

    /// <summary>An older release (nightly → stable), which an upgrade doesn't install: the release's package.</summary>
    Downgrade,

    /// <summary>A nightly build on Arch Linux, which has a package of stable releases only (<c>eng/aur.sh</c>).</summary>
    NoArchNightly,

    /// <summary>In <c>/opt/codalus</c>, but no package manager put it there (an extracted .tar.gz): the release's page.</summary>
    Unknown,
}

/// <summary>How a .deb, .rpm or Arch package install gets a release, and the command that does it (null: there is none).</summary>
public sealed record SystemPackageUpdate(SystemPackageUpdateKind Kind, string? Command = null);

/// <summary>
/// How an <see cref="InstallKind.SystemPackage"/> install gets a release. <c>update.channel</c> only decides which releases
/// the IDE offers; what apt, dnf and zypper install is up to the repository the system has, so the offer is compared with
/// it, and what it doesn't have gets the command that installs the release's own package.
/// </summary>
/// <param name="releases">Where <c>v&lt;version&gt;/&lt;file&gt;</c> is downloaded from (<c>https://github.com/…/releases/download</c>).</param>
/// <param name="rid"><c>linux-x64</c> or <c>linux-arm64</c>, as in the release files' names.</param>
/// <param name="run">Runs dpkg-query, apt-get and rpm (tests use a stand-in).</param>
/// <param name="distribution"><see cref="InstallPlans.LinuxDistribution"/>.</param>
/// <param name="root">The file system's root (tests use a folder).</param>
public sealed class SystemPackageUpdates(string releases, string rid, ProcessRunner? run = null, Func<string?>? distribution = null, string root = "/")
{
    private readonly ProcessRunner _run = run ?? EnvironmentCheck.RunProcessAsync;
    private readonly Func<string?> _distribution = distribution ?? (() => InstallPlans.LinuxDistribution());

    private enum Manager
    {
        Apt,
        Dnf,
        Zypper,
    }

    public async Task<SystemPackageUpdate> PlanAsync(InstallInfo install, UpdateOffer offer, CancellationToken cancellationToken)
    {
        var nightly = InstallInfo.ChannelOf(offer.Version) == InstallInfo.Nightly;
        if (install.UpdateCommand is { } command)
        {
            // The install command installs the newest stable release, whatever the channel.
            return nightly ? new(SystemPackageUpdateKind.NoArchNightly) : new(SystemPackageUpdateKind.InstallCommand, command);
        }

        if (await ManagerAsync(cancellationToken).ConfigureAwait(false) is not { } manager)
        {
            return new(SystemPackageUpdateKind.Unknown);
        }

        if (offer.IsDowngrade)
        {
            return new(SystemPackageUpdateKind.Downgrade, InstallPackage(manager, offer.Version, downgrade: true));
        }

        if (nightly)
        {
            return new(SystemPackageUpdateKind.Nightly, InstallPackage(manager, offer.Version, downgrade: false));
        }

        var stable = manager == Manager.Apt ? await AptFollowsStableAsync(cancellationToken).ConfigureAwait(false) : RpmFollowsStable(manager);
        return stable
            ? new(SystemPackageUpdateKind.Upgrade, Upgrade(manager))
            : new(SystemPackageUpdateKind.NoRepository, InstallPackage(manager, offer.Version, downgrade: false));
    }

    /// <summary>The package manager that installed <c>codalus</c>, or null.</summary>
    private async Task<Manager?> ManagerAsync(CancellationToken cancellationToken)
    {
        // A removed package's configuration files keep it known to dpkg, so its status is read, not the exit code alone.
        var (code, status) = await TryRunAsync("dpkg-query", ["-W", "-f", "${db:Status-Status}", "codalus"], cancellationToken).ConfigureAwait(false);
        if (code == 0 && status.Trim() == "installed")
        {
            return Manager.Apt;
        }

        if ((await TryRunAsync("rpm", ["-q", "codalus"], cancellationToken).ConfigureAwait(false)).ExitCode != 0)
        {
            return null;
        }

        return _distribution()?.Contains("suse", StringComparison.OrdinalIgnoreCase) == true ? Manager.Zypper : Manager.Dnf;
    }

    /// <summary>Whether apt reads the repository's <c>stable</c> suite: apt's own list of what it's configured with, in any format.</summary>
    private async Task<bool> AptFollowsStableAsync(CancellationToken cancellationToken)
    {
        // The repository's Origin, from its Release file (eng/linux-repos.sh): a mirror's address doesn't matter.
        var (code, suites) = await TryRunAsync("apt-get", ["indextargets", "--format", "$(RELEASE)", "Created-By: Packages", "Origin: Codalus IDE"], cancellationToken).ConfigureAwait(false);
        return code == 0 && suites.Split('\n', StringSplitOptions.TrimEntries).Contains(InstallInfo.Stable, StringComparer.Ordinal);
    }

    /// <summary>Whether an enabled <c>[codalus-stable]</c> (<c>codalus-stable.repo</c>, eng/linux-repos.sh) is in dnf's or zypper's repositories.</summary>
    private bool RpmFollowsStable(Manager manager)
    {
        // dnf 5 keeps what `dnf config-manager setopt` changes in an override file, which wins.
        string[] folders = manager == Manager.Zypper ? ["etc/zypp/repos.d"] : ["etc/yum.repos.d", "etc/dnf/repos.override.d"];
        bool? enabled = null;
        try
        {
            foreach (var folder in folders.Select(f => Path.Combine(root, f)).Where(Directory.Exists))
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.repo").Order(StringComparer.Ordinal))
                {
                    enabled = StableEnabled(File.ReadLines(file), enabled);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // not known: the release's package installs either way
        }

        return enabled == true;
    }

    private static bool? StableEnabled(IEnumerable<string> lines, bool? enabled)
    {
        var inside = false;
        foreach (var line in lines.Select(l => l.Trim()))
        {
            if (line.StartsWith('['))
            {
                inside = line == $"[codalus-{InstallInfo.Stable}]";
                enabled = inside ? enabled ?? true : enabled; // enabled unless it says otherwise
            }
            else if (inside && line.Split('=', 2, StringSplitOptions.TrimEntries) is ["enabled", var value])
            {
                enabled = value.ToUpperInvariant() is "1" or "TRUE" or "YES" or "ON";
            }
        }

        return enabled;
    }

    /// <summary>The upgrade of <c>codalus</c> alone, with the repository's lists read again first: a release just out isn't in them yet.</summary>
    private static string Upgrade(Manager manager) => manager switch
    {
        Manager.Apt => "sudo apt update && sudo apt install --only-upgrade codalus",
        Manager.Zypper => "sudo zypper refresh && sudo zypper update codalus",
        _ => "sudo dnf upgrade --refresh codalus",
    };

    /// <summary>
    /// Downloads the release's package and installs it. Into a new folder only the user can write: a fixed name in
    /// <c>/tmp</c> could be another user's file, and root installs it.
    /// </summary>
    private string InstallPackage(Manager manager, string version, bool downgrade)
    {
        var file = $"codalus-{version}-{rid}.{(manager == Manager.Apt ? "deb" : "rpm")}";
        var install = (manager, downgrade) switch
        {
            (Manager.Apt, true) => "apt install --allow-downgrades",
            (Manager.Apt, false) => "apt install",
            (_, true) => "rpm -U --oldpackage",
            (Manager.Zypper, false) => "zypper install",
            _ => "dnf install",
        };
        return $"cd \"$(mktemp -d)\" && curl -fLO {releases.TrimEnd('/')}/v{version}/{file} && sudo {install} ./{file}";
    }

    private async Task<(int ExitCode, string Output)> TryRunAsync(string program, string[] arguments, CancellationToken cancellationToken)
    {
        try
        {
            return await _run(program, arguments, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return (-1, string.Empty); // not on this distribution
        }
    }
}
