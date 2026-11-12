using Codalus.Platform;

namespace Codalus.Workbench.Setup;

/// <summary>How an install runs.</summary>
public enum InstallKind
{
    /// <summary>The IDE runs the command and shows its output (no password needed).</summary>
    Run,

    /// <summary>In the integrated terminal, where <c>sudo</c> or an installer asks the user itself.</summary>
    Terminal,

    /// <summary>Only a page to open: the download page, when nothing here can install it.</summary>
    OpenPage,
}

/// <summary>What installing a tool would do (M37): shown in full before anything runs.</summary>
/// <param name="Summary">One line: what it installs, and how.</param>
/// <param name="Command">The command line, as the user would type it (for <see cref="InstallKind.OpenPage"/>, the URL).</param>
/// <param name="Program">The program to start (Run), and its arguments.</param>
public sealed record InstallPlan(string ToolId, InstallKind Kind, string Summary, string Command, string? Program = null, IReadOnlyList<string>? Arguments = null)
{
    /// <summary>A folder to add to <c>PATH</c> (and <c>DOTNET_ROOT</c>) afterwards: where the SDK went.</summary>
    public string? AddsToPath { get; init; }

    /// <summary>Which of a tool's install options this is (<see cref="InstallPlans.DotnetOptions"/>): <c>pkg</c>, <c>dotnet-install</c>, <c>package</c>, <c>snap</c>, <c>winget</c> or <c>page</c>.</summary>
    public string Option { get; init; } = "default";

    /// <summary>The option's name in the list it's picked from; null: the <see cref="Summary"/>.</summary>
    public string? Title { get; init; }

    /// <summary>
    /// What the user should know before picking it: that the <c>dotnet</c> their terminal runs won't see the SDK it
    /// installs, or that it replaces one. Null when there's nothing to say.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>The option that fits this machine: the one picked unless the user picks another.</summary>
    public bool Recommended { get; init; }
}

/// <summary>
/// What decides the .NET install options (<see cref="InstallPlans.DotnetOptions"/>): the <c>dotnet</c> the user's
/// terminal runs, and the package managers.
/// </summary>
/// <param name="Home">The user's home folder.</param>
public sealed record DotnetInstallFacts(PlatformKind Platform, string Home)
{
    /// <summary>
    /// The <c>dotnet</c> the user's terminal runs: the first on the login shell's <c>PATH</c>, before the IDE put a .NET
    /// root first on its own (<c>/usr/local/bin/dotnet</c>, <c>/snap/bin/dotnet</c>); null when there's none.
    /// </summary>
    public string? ShellDotnet { get; init; }

    /// <summary>The folder <see cref="ShellDotnet"/> really runs from, its links resolved (<c>/usr/local/share/dotnet</c>).</summary>
    public string? ShellRoot { get; init; }

    public bool HasWinget { get; init; }

    /// <summary><c>/etc/os-release</c>'s <c>ID</c> and <c>ID_LIKE</c> (Linux).</summary>
    public string? LinuxDistribution { get; init; }

    /// <summary>
    /// The version apt, dnf or zypper would install of the channel's SDK package (<c>dotnet-sdk-11.0</c>), or null when
    /// it has none (<see cref="InstallPlans.PackageCandidate"/>).
    /// </summary>
    public string? PackageCandidate { get; init; }

    /// <summary>Whether the terminal's <c>dotnet</c> is the <c>dotnet-sdk</c> snap's (Linux), whose root is <c>/snap/dotnet-sdk/current</c>.</summary>
    public bool IsSdkSnap { get; init; }

    /// <summary>The IDE is an x64 process on Arm64: .NET installs as x64, the only .NET its hosts can load.</summary>
    public bool EmulatedX64 { get; init; }

    /// <summary>The machine is Arm64 (Apple silicon): the <c>.pkg</c> for <c>arm64</c>.</summary>
    public bool Arm64 { get; init; } = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;

    /// <summary>Where winget's SDK goes (<c>%ProgramFiles%\dotnet</c>).</summary>
    public string WindowsRoot { get; init; } = @"C:\Program Files\dotnet";
}

/// <summary>
/// Install plans for the missing tools on each OS (M37). Nothing here runs anything: the shell shows the plan, asks, and
/// then runs it.
/// <list type="bullet">
/// <item>Windows: winget (the .NET SDK's channel, Git for Windows); without winget, the download page.</item>
/// <item>macOS: git from the Xcode Command Line Tools (<c>xcode-select --install</c> opens Apple's installer).</item>
/// <item>Linux: git from the distribution, in the terminal, where <c>sudo</c> asks for its password.</item>
/// </list>
/// .NET has several options, the one that fits the machine first (<see cref="DotnetOptions"/>): the user picks.
/// A version <c>global.json</c> pins is installed exactly (dotnet-install's <c>--version</c>; winget installs its channel's
/// newest patch, which a <c>latestPatch</c> or later roll-forward accepts).
/// </summary>
public static class InstallPlans
{
    public const string DotnetInstallScript = "https://dot.net/v1/dotnet-install.sh";
    public const string DotnetDownloadPage = "https://dotnet.microsoft.com/download";
    public const string GitDownloadPage = "https://git-scm.com/downloads";
    public const string LfsDownloadPage = "https://git-lfs.com";
    public const string DockerPage = "https://docs.docker.com/get-docker/";
    public const string PodmanPage = "https://podman.io/docs/installation";

    /// <summary>The channel of the .NET the IDE and its hosts run on (<c>11.0</c>).</summary>
    public static string HostChannel => $"{Environment.Version.Major}.0";

    /// <summary>The SDK's version for an install: a pinned one, else the channel of the .NET the IDE runs on.</summary>
    public static string Channel(string? version, string? fallback = null) =>
        version is { } v && v.Split('.') is [var major, var minor, ..] ? $"{major}.{minor}" : fallback ?? HostChannel;

    /// <summary>
    /// winget's package of a channel's SDK: <c>Microsoft.DotNet.SDK.11</c>; the preview package when the version wanted
    /// in that channel is a preview or a release candidate (M30b, M49), which winget lists only as the preview SDK.
    /// </summary>
    /// <param name="version">
    /// The version wanted: the runtime an update or the IDE's hosts need (M49), else the one the IDE runs on. A release
    /// (11.0.0) needs the channel's package even while the IDE runs on a release candidate.
    /// </param>
    public static string WingetSdk(string channel, string version) =>
        version.Contains('-', StringComparison.Ordinal) && Channel(version, fallback: string.Empty) == channel
            ? "Microsoft.DotNet.SDK.Preview"
            : $"Microsoft.DotNet.SDK.{channel.Split('.')[0]}";

    /// <summary>
    /// The plan for <paramref name="tool"/>, or null when there's nothing to install it with here.
    /// <paramref name="hasWinget"/> and <paramref name="linuxDistribution"/> (<c>/etc/os-release</c>'s <c>ID</c> and
    /// <c>ID_LIKE</c>) describe the machine.
    /// </summary>
    /// <param name="emulatedX64">
    /// The IDE is an x64 process on Arm64 (the win-x64 build on Windows on Arm, M49): .NET installs as x64, the only
    /// .NET its hosts can load.
    /// </param>
    public static InstallPlan? For(ToolStatus tool, PlatformKind platform, string home, bool hasWinget, string? linuxDistribution, bool emulatedX64 = false)
    {
        return tool.Id switch
        {
            "dotnet" or "dotnet-runtime" => DotnetOptions(tool, new DotnetInstallFacts(platform, home) { HasWinget = hasWinget, LinuxDistribution = linuxDistribution, EmulatedX64 = emulatedX64 })[0],
            "git" => Git(platform, hasWinget, linuxDistribution),
            "git-lfs" => platform switch
            {
                PlatformKind.Windows when hasWinget => Winget("git-lfs", "GitHub.GitLFS", "Git LFS"),
                PlatformKind.Linux when Package(linuxDistribution, "git-lfs") is { } command => new("git-lfs", InstallKind.Terminal, "Git LFS from the distribution's packages", command),
                _ => new("git-lfs", InstallKind.OpenPage, "Git LFS's download page", LfsDownloadPage),
            },
            "docker" => new("docker", InstallKind.OpenPage, "Docker's installation guide", DockerPage),
            "podman" => platform == PlatformKind.Windows && hasWinget
                ? Winget("podman", "RedHat.Podman", "Podman")
                : new("podman", InstallKind.OpenPage, "Podman's installation guide", PodmanPage),
            "wsl" => platform == PlatformKind.Windows
                ? new("wsl", InstallKind.Terminal, "WSL, with Ubuntu (Windows asks for administrator rights)", "wsl --install")
                : null,
            _ => null,
        };
    }

    /// <summary>The folders Linux distributions' and Microsoft's own packages put .NET in.</summary>
    private static readonly string[] s_linuxPackageRoots = ["/usr/lib/dotnet", "/usr/lib64/dotnet", "/usr/share/dotnet"];

    /// <summary>Where Microsoft's <c>.pkg</c> puts .NET on macOS (and <c>/etc/paths.d/dotnet</c> on every shell's PATH).</summary>
    public const string MacPackageRoot = "/usr/local/share/dotnet";

    /// <summary>
    /// The ways to install the .NET SDK here, the one that fits first (<see cref="InstallPlan.Recommended"/>): the user
    /// picks. The one that fits installs where the <c>dotnet</c> their terminal runs is (<see cref="DotnetInstallFacts.ShellRoot"/>),
    /// so that the terminal sees the SDK the IDE uses; an option that doesn't says so (<see cref="InstallPlan.Note"/>).
    /// <list type="bullet">
    /// <item>macOS: Microsoft's <c>.pkg</c> into <c>/usr/local/share/dotnet</c>, with <c>sudo installer</c> in the terminal;
    /// <c>dotnet-install.sh</c> into <c>~/.dotnet</c> (no password; the terminal's own root when that's in the home folder);
    /// the download page. The <c>.pkg</c> fits unless the terminal's <c>dotnet</c> is in the home folder.</item>
    /// <item>Linux: the distribution's <c>dotnet-sdk-X.Y</c> when apt, dnf or zypper has it (not for a pinned version);
    /// the <c>dotnet-sdk</c> snap's channel when the terminal's <c>dotnet</c> is that snap; <c>dotnet-install.sh</c> into
    /// <c>~/.dotnet</c>; the download page. The package fits when the terminal's <c>dotnet</c> is a package's or there's
    /// none; else <c>~/.dotnet</c> (installing into a package manager's folder would break it).</item>
    /// <item>Windows: winget, the download page.</item>
    /// </list>
    /// A version <c>global.json</c> pins is installed exactly (dotnet-install's <c>--version</c>, the <c>.pkg</c> of that
    /// version); winget, the packages and the snap install their channel's newest.
    /// </summary>
    public static IReadOnlyList<InstallPlan> DotnetOptions(ToolStatus tool, DotnetInstallFacts facts)
    {
        var pinned = tool.State == ToolState.Mismatch ? tool.WantedVersion : null;
        var channel = DotnetChannel(tool);
        // The version wanted in the channel (M49): the runtime the hosts need, a pinned SDK, else the one the IDE runs on.
        var wanted = tool.Id == "dotnet-runtime" && tool.WantedVersion is { } runtime ? runtime : pinned ?? EnvironmentCheck.DefaultHostsRuntime();
        var withRuntime = tool.Id == "dotnet-runtime" ? ", with the runtime Codalus IDE needs," : string.Empty;
        var what = pinned is null ? $".NET SDK {channel}" : $".NET SDK {pinned}";
        var shellRoot = facts.ShellRoot;
        var page = new InstallPlan(tool.Id, InstallKind.OpenPage, "The .NET download page", DotnetDownloadPage) { Option = "page", Title = "The .NET download page" };

        // The terminal's dotnet won't see what goes to `target`: say so.
        string? NotSeen(string target) => shellRoot is not null && !SameFolder(target, shellRoot, facts.Platform)
            ? $"Your terminal's dotnet runs from {shellRoot}, so it won't see this SDK; only Codalus IDE will use it."
            : null;

        if (facts.Platform == PlatformKind.Windows)
        {
            if (!facts.HasWinget)
            {
                return [page with { Summary = "The .NET download page (winget isn't available)", Recommended = true }];
            }

            var winget = Winget(tool.Id, WingetSdk(channel, wanted), pinned is null ? what : $".NET SDK {channel} (the newest patch; global.json asks for {pinned})", facts.EmulatedX64 ? ["--architecture", "x64"] : []) with
            {
                Option = "winget",
                Title = $".NET SDK {channel} with winget",
                Note = NotSeen(facts.WindowsRoot),
                Recommended = true,
            };
            return [winget, page];
        }

        // dotnet-install: into the terminal's own root when that's the user's (it needs no password there), else ~/.dotnet.
        var home = facts.Home.TrimEnd('/');
        var folder = shellRoot is not null && shellRoot.StartsWith(home + "/", StringComparison.Ordinal) ? shellRoot : home + "/.dotnet"; // a Unix path, whatever machine computes the plan
        var version = pinned is null ? $"--channel {channel}" : $"--version {pinned}";
        var script = $"curl -sSL {DotnetInstallScript} | bash -s -- {version} --install-dir \"{folder}\"";
        var dotnetInstall = new InstallPlan(tool.Id, InstallKind.Run, $"{what}{withRuntime} into {folder}", script, "bash", ["-c", script])
        {
            AddsToPath = folder,
            Option = "dotnet-install",
            Title = $"{what} into {folder} with dotnet-install",
            Note = shellRoot is null
                ? $"{folder} isn't on your terminal's PATH: Codalus IDE will use this SDK, but typing dotnet in a terminal won't find it."
                : NotSeen(folder),
        };

        if (facts.Platform == PlatformKind.MacOS)
        {
            var architecture = facts.EmulatedX64 || !facts.Arm64 ? "x64" : "arm64";
            var root = facts.EmulatedX64 && facts.Arm64 ? MacPackageRoot + "/x64" : MacPackageRoot;
            var url = pinned is not null
                ? $"https://builds.dotnet.microsoft.com/dotnet/Sdk/{pinned}/dotnet-sdk-{pinned}-osx-{architecture}.pkg"
                : $"https://aka.ms/dotnet/{channel}/{(Prerelease(channel, wanted) ? "preview/" : string.Empty)}dotnet-sdk-osx-{architecture}.pkg";
            var command = $"pkg=\"$(mktemp -d)/dotnet-sdk.pkg\" && curl -fSL -o \"$pkg\" {url} && sudo installer -pkg \"$pkg\" -target /";
            var package = new InstallPlan(tool.Id, InstallKind.Terminal, $"{what}{withRuntime} into {root}, with Microsoft's installer package (sudo asks for your password)", command)
            {
                AddsToPath = root,
                Option = "pkg",
                Title = $"{what} into {root} with Microsoft's installer package",
                Note = NotSeen(root),
            };
            return shellRoot is not null && folder == shellRoot
                ? [dotnetInstall with { Recommended = true }, package, page]
                : [package with { Recommended = true }, dotnetInstall, page];
        }

        // Linux.
        var options = new List<InstallPlan>();
        var packageRoot = shellRoot is not null && s_linuxPackageRoots.Any(r => SameFolder(r, shellRoot, facts.Platform)) ? shellRoot : null;
        if (pinned is null && facts.PackageCandidate is { } candidate && Package(facts.LinuxDistribution, $"dotnet-sdk-{channel}") is { } install)
        {
            var sdk = $"dotnet-sdk-{channel}";
            options.Add(new InstallPlan(tool.Id, InstallKind.Terminal, $"{sdk} {candidate}{withRuntime} from the distribution's packages (sudo asks for your password)", install)
            {
                AddsToPath = packageRoot,
                Option = "package",
                Title = $"{sdk} {candidate} from the distribution's packages",
                Note = shellRoot is not null && packageRoot is null ? NotSeen("/usr/lib/dotnet") : null,
                Recommended = shellRoot is null || packageRoot is not null,
            });
        }

        if (facts.IsSdkSnap)
        {
            var command = $"sudo snap refresh dotnet-sdk --channel={channel}/stable";
            options.Insert(0, new InstallPlan(tool.Id, InstallKind.Terminal, $".NET SDK {channel}{withRuntime} from the dotnet-sdk snap (sudo asks for your password)", command)
            {
                Option = "snap",
                Title = $".NET SDK {channel} from the dotnet-sdk snap",
                Note = "The snap holds one SDK: this replaces the one it has.",
                Recommended = pinned is null,
            });
        }

        if (options.FirstOrDefault(o => o.Recommended) is { } fits)
        {
            options.Remove(fits);
            options = [fits, .. options.Select(o => o with { Recommended = false }), dotnetInstall];
        }
        else
        {
            options = [dotnetInstall with { Recommended = true }, .. options];
        }

        options.Add(page);
        return options;
    }

    /// <summary>The channel of the SDK installed for a .NET row: the one it names, a pinned version's, else the IDE's.</summary>
    public static string DotnetChannel(ToolStatus tool) =>
        tool.WantedChannel ?? Channel(tool.State == ToolState.Mismatch ? tool.WantedVersion ?? tool.Version : tool.Version);

    /// <summary>Whether the version wanted in a channel is a preview or a release candidate (M30b, M49).</summary>
    private static bool Prerelease(string channel, string version) =>
        version.Contains('-', StringComparison.Ordinal) && Channel(version, fallback: string.Empty) == channel;

    /// <summary>The same folder, whichever separator ends it; case counts on Linux only (macOS' and Windows' usual file systems ignore it).</summary>
    private static bool SameFolder(string a, string b, PlatformKind platform) =>
        string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), platform == PlatformKind.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The version in a package manager's answer about a package, or null when it has none: apt-cache policy's
    /// <c>Candidate:</c> (not <c>(none)</c>), dnf's and zypper's <c>Version :</c>.
    /// </summary>
    public static string? PackageCandidate(string output)
    {
        foreach (var line in output.Split('\n').Select(l => l.Trim()))
        {
            if (line.StartsWith("Candidate:", StringComparison.Ordinal))
            {
                var candidate = line["Candidate:".Length..].Trim();
                return candidate.Length == 0 || candidate == "(none)" ? null : candidate;
            }

            if (line.StartsWith("Version", StringComparison.Ordinal) && line.IndexOf(':', StringComparison.Ordinal) is var colon and > 0 && line[7..colon].Trim().Length == 0)
            {
                return line[(colon + 1)..].Trim() is { Length: > 0 } version ? version : null;
            }
        }

        return null;
    }

    /// <summary>
    /// The command that asks the distribution's package manager about <paramref name="package"/> without downloading
    /// anything (<see cref="PackageCandidate"/> reads its answer): apt-cache, dnf from its cache, zypper without a
    /// refresh. Null for other distributions.
    /// </summary>
    public static (string Program, string[] Arguments)? PackageQuery(string? distribution, string package)
    {
        var ids = (distribution ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return true switch
        {
            _ when ids.Any(i => i is "debian" or "ubuntu") => ("apt-cache", ["policy", package]),
            _ when ids.Any(i => i is "fedora" or "rhel" or "centos") => ("dnf", ["-C", "info", package]),
            _ when ids.Any(i => i is "opensuse" or "suse" or "opensuse-tumbleweed" or "opensuse-leap") => ("zypper", ["--non-interactive", "--no-refresh", "info", package]),
            _ => null,
        };
    }

    private static InstallPlan Git(PlatformKind platform, bool hasWinget, string? linuxDistribution) => platform switch
    {
        PlatformKind.Windows when hasWinget => Winget("git", "Git.Git", "Git for Windows"),
        PlatformKind.Windows => new("git", InstallKind.OpenPage, "Git's download page (winget isn't available)", GitDownloadPage),
        PlatformKind.MacOS => new("git", InstallKind.Run, "Git with Apple's Command Line Tools (macOS shows its installer)", "xcode-select --install", "xcode-select", ["--install"]),
        _ when Package(linuxDistribution, "git") is { } command => new("git", InstallKind.Terminal, "Git from the distribution's packages (sudo asks for your password)", command),
        _ => new("git", InstallKind.OpenPage, "Git's download page", GitDownloadPage),
    };

    private static InstallPlan Winget(string tool, string id, string what, string[]? extra = null)
    {
        string[] arguments = ["install", "--id", id, "--exact", .. extra ?? [], "--source", "winget", "--accept-package-agreements", "--accept-source-agreements"];
        return new(tool, InstallKind.Run, $"{what} with winget", "winget " + string.Join(' ', arguments), "winget", arguments);
    }

    /// <summary>The distribution's install command for a package, from <c>/etc/os-release</c>'s ID and ID_LIKE.</summary>
    public static string? Package(string? distribution, string package)
    {
        var ids = (distribution ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Is(params string[] names) => ids.Any(names.Contains);
        return true switch
        {
            _ when Is("debian", "ubuntu") => $"sudo apt-get install -y {package}",
            _ when Is("fedora", "rhel", "centos") => $"sudo dnf install -y {package}",
            _ when Is("opensuse", "suse", "opensuse-tumbleweed", "opensuse-leap") => $"sudo zypper install -y {package}",
            _ when Is("arch") => $"sudo pacman -S --noconfirm {package}",
            _ when Is("alpine") => $"sudo apk add {package}",
            _ => null,
        };
    }

    /// <summary><c>/etc/os-release</c>'s <c>ID</c> and <c>ID_LIKE</c>, space-separated (Linux), or null.</summary>
    public static string? LinuxDistribution(string osRelease = "/etc/os-release")
    {
        try
        {
            if (!File.Exists(osRelease))
            {
                return null;
            }

            var values = File.ReadAllLines(osRelease)
                .Select(l => l.Split('=', 2))
                .Where(p => p.Length == 2 && p[0] is "ID" or "ID_LIKE")
                .Select(p => p[1].Trim('"'));
            return string.Join(' ', values);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
