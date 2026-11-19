using System.Text.Json;
using System.Text.Json.Nodes;
using Codalus.Platform;
using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Setup;

namespace Codalus.Workbench.Build;

/// <summary>A <c>dotnet</c> in a WSL distribution: its path, the folder it really runs from, and its shared frameworks.</summary>
public sealed record WslDotnetInstall(string Path, string Root, IReadOnlyDictionary<string, IReadOnlyList<FrameworkVersion>> Frameworks)
{
    /// <summary>The <c>dotnet-sdk</c> snap's, whose root is the snap's (<c>/snap/dotnet-sdk/current</c>).</summary>
    public bool IsSnap => Path.StartsWith("/snap/", StringComparison.Ordinal);

    public IReadOnlyList<FrameworkVersion> Versions(string framework) =>
        Frameworks.TryGetValue(framework, out var versions) ? versions : [];
}

/// <summary>
/// What a WSL distribution has of .NET (<see cref="WslDotnet.ProbeScript"/>): the home folder, <c>/etc/os-release</c>'s
/// <c>ID</c> and <c>ID_LIKE</c>, the <c>dotnet</c> its login shell runs (<see cref="Shell"/>, null when there's none), and
/// each <c>dotnet</c> found, the login shell's first, then <c>~/.dotnet</c>'s.
/// </summary>
public sealed record WslDotnetProbe(string Home, string? Distribution, string? Shell, IReadOnlyList<WslDotnetInstall> Installs);

/// <summary>
/// Which <c>dotnet</c> runs a program in WSL (<see cref="Dotnet"/>), or, when none has what it needs, the first one found
/// and the frameworks it's missing (<see cref="Missing"/>; empty with no <c>dotnet</c> at all).
/// </summary>
public sealed record WslDotnetChoice(WslDotnetInstall? Dotnet, WslDotnetInstall? Found, IReadOnlyList<FrameworkResolution> Missing);

/// <summary>
/// The .NET a <c>WSL2</c> launch profile's run uses in its distribution (M14c): the login shell's <c>dotnet</c>, else the
/// one dotnet-install put in <c>~/.dotnet</c>, whichever has the frameworks the app's <c>runtimeconfig.json</c> asks for;
/// and the SDK channel to install there when none has.
/// </summary>
public static class WslDotnet
{
    /// <summary>
    /// The script that describes the distribution's .NET (<see cref="Parse"/> reads it), run by a login shell so its own
    /// <c>PATH</c> applies. A <c>dotnet</c> that runs from a folder already seen (a link to it) is listed once.
    /// </summary>
    public const string ProbeScript = """
        echo "home=$HOME"
        if [ -r /etc/os-release ]; then (. /etc/os-release; echo "os=$ID $ID_LIKE"); fi
        shell=$(command -v dotnet 2>/dev/null)
        echo "shell=$shell"
        seen=:
        for d in "$shell" "$HOME/.dotnet/dotnet"; do
          case "$d" in /*) ;; *) continue ;; esac
          [ -x "$d" ] || continue
          r=$(dirname "$(readlink -f "$d")")
          case "$seen" in *":$r:"*) continue ;; esac
          seen="$seen$r:"
          echo "dotnet=$d"
          echo "root=$r"
          "$d" --list-runtimes 2>/dev/null
        done
        """;

    /// <summary><see cref="ProbeScript"/> as one command for <c>sh -c</c>: in a login shell.</summary>
    public static string ProbeCommand => "exec sh -lc " + WslLaunch.ShellQuote(ProbeScript);

    /// <summary>Reads <see cref="ProbeScript"/>'s output.</summary>
    public static WslDotnetProbe Parse(string output)
    {
        var home = string.Empty;
        string? distribution = null;
        string? shell = null;
        var installs = new List<WslDotnetInstall>();
        string? path = null;
        string? root = null;
        Dictionary<string, List<FrameworkVersion>>? frameworks = null;

        void Flush()
        {
            if (path is not null && frameworks is not null)
            {
                installs.Add(new WslDotnetInstall(path, root ?? System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "/", frameworks.ToDictionary(f => f.Key, f => (IReadOnlyList<FrameworkVersion>)f.Value, StringComparer.Ordinal)));
            }

            path = null;
            root = null;
            frameworks = null;
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("home=", StringComparison.Ordinal))
            {
                home = line["home=".Length..].TrimEnd('/');
            }
            else if (line.StartsWith("os=", StringComparison.Ordinal))
            {
                distribution = line["os=".Length..].Trim() is { Length: > 0 } ids ? ids : null;
            }
            else if (line.StartsWith("shell=", StringComparison.Ordinal))
            {
                shell = line["shell=".Length..] is ['/', ..] found ? found : null;
            }
            else if (line.StartsWith("dotnet=", StringComparison.Ordinal))
            {
                Flush();
                path = line["dotnet=".Length..];
                frameworks = new(StringComparer.Ordinal);
            }
            else if (line.StartsWith("root=", StringComparison.Ordinal) && path is not null)
            {
                root = line["root=".Length..];
            }
            else if (frameworks is not null && line.Split(' ', 3) is [var name, var text, ..] && name.Contains('.', StringComparison.Ordinal) && FrameworkVersion.TryParse(text, out var version))
            {
                // Microsoft.NETCore.App 8.0.10 [/usr/lib/dotnet/shared/Microsoft.NETCore.App]
                if (!frameworks.TryGetValue(name, out var versions))
                {
                    frameworks[name] = versions = [];
                }

                versions.Add(version);
            }
        }

        Flush();
        return new WslDotnetProbe(home, distribution, shell, installs);
    }

    /// <summary>
    /// The <c>dotnet</c> that runs an app needing <paramref name="config"/>'s frameworks: the first that has them all (by
    /// the .NET host's roll-forward rules). Without a <c>runtimeconfig.json</c> to read, the first one found.
    /// </summary>
    public static WslDotnetChoice Choose(WslDotnetProbe probe, RuntimeConfig? config)
    {
        var first = probe.Installs.Count > 0 ? probe.Installs[0] : null;
        if (config is not { IsFrameworkDependent: true })
        {
            return new WslDotnetChoice(first, first, []);
        }

        foreach (var install in probe.Installs)
        {
            if (Missing(install, config).Count == 0)
            {
                return new WslDotnetChoice(install, first, []);
            }
        }

        return new WslDotnetChoice(null, first, first is null ? [] : Missing(first, config));
    }

    private static List<FrameworkResolution> Missing(WslDotnetInstall install, RuntimeConfig config) =>
        [.. config.Frameworks
            .Select(f => new FrameworkResolution(f, FrameworkResolver.Resolve(f, install.Versions(f.Name)), install.Versions(f.Name)))
            .Where(r => r.Resolved is null)];

    /// <summary>
    /// The SDK channel to install for an app (<c>8.0</c>): the newest framework its <c>runtimeconfig.json</c> asks for, else
    /// its target framework's (<c>net8.0</c>), else the one Codalus IDE runs on.
    /// </summary>
    public static string Channel(RuntimeConfig? config, string? targetFramework)
    {
        if (config is { Frameworks.Count: > 0 })
        {
            var newest = config.Frameworks.Select(f => f.Version).Max()!;
            return $"{newest.Major}.{newest.Minor}";
        }

        if (targetFramework is { } tfm && tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase)
            && Version.TryParse(tfm[3..].Split('-')[0], out var version) && version.Major >= 5)
        {
            return $"{version.Major}.{version.Minor}";
        }

        return InstallPlans.HostChannel;
    }

    /// <summary>The <c>runtimeconfig.json</c> next to a program's <c>.dll</c> (<see cref="RunInfo.TargetPath"/>), or null.</summary>
    public static string? RuntimeConfigPath(RunInfo info) =>
        info.TargetPath is { } target && target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? target[..^".dll".Length] + ".runtimeconfig.json"
            : null;

    /// <summary>
    /// What Install .NET's options are measured against in the distribution (<see cref="InstallPlans.DotnetOptions"/>): its
    /// login shell's <c>dotnet</c> and the folder it runs from, its packages, and its home.
    /// </summary>
    public static DotnetInstallFacts Facts(WslDotnetProbe probe, string? packageCandidate)
    {
        var shell = probe.Installs.FirstOrDefault(i => i.Path == probe.Shell);
        return new DotnetInstallFacts(PlatformKind.Linux, probe.Home)
        {
            ShellDotnet = shell?.Path,
            ShellRoot = shell is null ? null : shell.IsSnap ? "/snap/dotnet-sdk/current" : shell.Root,
            IsSdkSnap = shell?.IsSnap == true,
            LinuxDistribution = probe.Distribution,
            PackageCandidate = packageCandidate,
        };
    }

    /// <summary>
    /// The command that asks the distribution's package manager what it has of the channel's SDK (its answer is
    /// <see cref="InstallPlans.PackageCandidate"/>'s), for <c>sh -c</c>; null for a distribution it can't ask.
    /// </summary>
    public static string? PackageQueryCommand(WslDotnetProbe probe, string channel) =>
        InstallPlans.PackageQuery(probe.Distribution, $"dotnet-sdk-{channel}") is (string program, string[] arguments)
            ? string.Join(' ', [program, .. arguments.Select(WslLaunch.ShellQuote)]) + " 2>/dev/null"
            : null;

    /// <summary>
    /// Install .NET's options (<see cref="InstallPlans.DotnetOptions"/>) as they run in the distribution: through
    /// <paramref name="wsl"/> (<c>wsl.exe</c>), in the integrated terminal when <c>sudo</c> asks for a password. They
    /// change nothing of the IDE's own .NET (no <see cref="InstallPlan.AddsToPath"/>).
    /// </summary>
    public static IReadOnlyList<InstallPlan> InstallOptions(ToolStatus tool, DotnetInstallFacts facts, string distribution, string display, string wsl = "wsl.exe")
    {
        List<string> prefix = distribution.Length == 0 ? [] : ["-d", distribution];
        return [.. InstallPlans.DotnetOptions(tool, facts).Select(plan => plan.Kind switch
        {
            InstallKind.Run => plan with
            {
                Program = wsl,
                Arguments = [.. prefix, "--exec", plan.Program!, .. plan.Arguments ?? []],
                AddsToPath = null,
                Wsl = distribution,
                Summary = $"{plan.Summary}, in {display}",
                Note = plan.Option == "dotnet-install" && facts.ShellRoot is null
                    ? $"{facts.Home}/.dotnet isn't on the PATH of {display}'s shells: Codalus IDE runs your apps on this SDK, but typing dotnet there won't find it."
                    : plan.Note,
            },
            InstallKind.Terminal => plan with { AddsToPath = null, Wsl = distribution, Summary = $"{plan.Summary}, in {display}" },
            _ => plan,
        })];
    }

    /// <summary>
    /// The variable that points ASP.NET Core at another static web assets manifest (<c>staticWebAssets</c>, read in the
    /// Development environment).
    /// </summary>
    public const string StaticWebAssetsVariable = "ASPNETCORE_STATICWEBASSETS";

    /// <summary>The static web assets manifest the build writes next to a web app's <c>.dll</c>, or null.</summary>
    public static string? StaticWebAssetsManifest(RunInfo info) =>
        info.TargetPath is { } target && target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? target[..^".dll".Length] + ".staticwebassets.runtime.json"
            : null;

    /// <summary>
    /// A static web assets manifest with its content roots (Windows folders: the project's <c>wwwroot</c>, packages'
    /// <c>staticwebassets</c>) as WSL sees them (<paramref name="toLinux"/>); in the distribution, ASP.NET Core's
    /// <c>PhysicalFileProvider</c> refuses a Windows path as not absolute, and the app doesn't start. Null when the
    /// manifest isn't one.
    /// </summary>
    public static string? TranslateStaticWebAssets(string json, Func<string, string> toLinux)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject manifest || manifest["ContentRoots"] is not JsonArray roots)
            {
                return null;
            }

            for (var i = 0; i < roots.Count; i++)
            {
                if (roots[i] is JsonValue value && value.TryGetValue<string>(out var root) && root.Length > 1 && (root[1] == ':' || root.StartsWith(@"\\", StringComparison.Ordinal)))
                {
                    var linux = toLinux(root);
                    roots[i] = linux.EndsWith('/') ? linux : linux + "/";
                }
            }

            return manifest.ToJsonString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// A start that stopped before its program ran, the user already told why and what to do (.NET missing in a WSL
/// distribution, with Install offered: <see cref="RunService.PrepareWsl"/>). Callers end the start without another message.
/// </summary>
public sealed class RunNotStartedException(string message) : InvalidOperationException(message);
