using DaedalusStudio.Platform;

namespace DaedalusStudio.Workbench.Setup;

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
    /// <summary>A folder to add to <c>PATH</c> (and <c>DOTNET_ROOT</c>) afterwards: a user-local SDK.</summary>
    public string? AddsToPath { get; init; }
}

/// <summary>
/// Install plans for the missing tools on each OS (M37). Nothing here runs anything: the shell shows the plan, asks, and
/// then runs it.
/// <list type="bullet">
/// <item>Windows: winget (the .NET SDK's channel, Git for Windows); without winget, the download page.</item>
/// <item>macOS: <c>dotnet-install.sh</c> into <c>~/.dotnet</c> (no administrator password); git from the Xcode Command
/// Line Tools (<c>xcode-select --install</c> opens Apple's installer).</item>
/// <item>Linux: <c>dotnet-install.sh</c> into <c>~/.dotnet</c> (no root); git from the distribution, in the terminal,
/// where <c>sudo</c> asks for its password.</item>
/// </list>
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

    /// <summary>The SDK's version for an install: a pinned one, else the channel of the SDK the IDE runs on.</summary>
    public static string Channel(string? version, string fallback = "10.0") =>
        version is { } v && v.Split('.') is [var major, var minor, ..] ? $"{major}.{minor}" : fallback;

    /// <summary>
    /// The plan for <paramref name="tool"/>, or null when there's nothing to install it with here.
    /// <paramref name="hasWinget"/> and <paramref name="linuxDistribution"/> (<c>/etc/os-release</c>'s <c>ID</c> and
    /// <c>ID_LIKE</c>) describe the machine.
    /// </summary>
    public static InstallPlan? For(ToolStatus tool, PlatformKind platform, string home, bool hasWinget, string? linuxDistribution)
    {
        return tool.Id switch
        {
            "dotnet" => Dotnet(tool, platform, home, hasWinget),
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

    private static InstallPlan Dotnet(ToolStatus tool, PlatformKind platform, string home, bool hasWinget)
    {
        var pinned = tool.State == ToolState.Mismatch ? tool.WantedVersion : null;
        var channel = Channel(pinned ?? tool.Version);
        if (platform == PlatformKind.Windows)
        {
            return hasWinget
                ? Winget("dotnet", $"Microsoft.DotNet.SDK.{channel.Split('.')[0]}", pinned is null ? $".NET SDK {channel}" : $".NET SDK {channel} (the newest patch; global.json asks for {pinned})")
                : new("dotnet", InstallKind.OpenPage, "The .NET download page (winget isn't available)", DotnetDownloadPage);
        }

        // macOS and Linux: Microsoft's dotnet-install script, into the user's ~/.dotnet: no administrator password.
        var folder = home.TrimEnd('/') + "/.dotnet"; // a Unix path, whatever machine computes the plan
        var version = pinned is null ? $"--channel {channel}" : $"--version {pinned}";
        var command = $"curl -sSL {DotnetInstallScript} | bash -s -- {version} --install-dir \"{folder}\"";
        return new("dotnet", InstallKind.Run, pinned is null ? $".NET SDK {channel} into {folder}" : $".NET SDK {pinned} into {folder}", command, "bash", ["-c", command])
        {
            AddsToPath = folder,
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

    private static InstallPlan Winget(string tool, string id, string what)
    {
        string[] arguments = ["install", "--id", id, "--exact", "--source", "winget", "--accept-package-agreements", "--accept-source-agreements"];
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
