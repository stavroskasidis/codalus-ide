using System.Runtime.InteropServices;

namespace Codalus.Rpc;

/// <summary>
/// The <c>dotnet</c> a self-contained remote server runs its hosts on (M21, ADR 0050 and 0080): the first candidate,
/// in order, whose root (the muxer's folder, its links resolved) has the shared frameworks the hosts ask for, by the
/// host's own rules; else the first candidate that exists, as before, so that starting a host names what's missing.
/// The candidates are <c>DOTNET_ROOT</c>, <c>~/.dotnet</c>, the PATH's <c>dotnet</c>, then the usual install folders
/// (<c>/usr/share/dotnet</c>, <c>/usr/lib/dotnet</c>, <c>/usr/local/share/dotnet</c>, Homebrew's; <c>%ProgramFiles%\dotnet</c>
/// on Windows). Without the check, a <c>DOTNET_ROOT</c> or <c>~/.dotnet</c> with only an older .NET won over the PATH's
/// right one, and every host exited with "You must install or update .NET". Every input is a property, so tests
/// describe any machine.
/// </summary>
public sealed class DotnetMuxerLookup
{
    public static DotnetMuxerLookup Default { get; } = new();

    public bool IsWindows { get; init; } = OperatingSystem.IsWindows();

    /// <summary>This process's environment (<c>DOTNET_ROOT</c>, <c>ProgramFiles</c>, and what the frameworks' check reads).</summary>
    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;

    public string Home { get; init; } = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

    /// <summary>The PATH's <c>dotnet</c>, or null.</summary>
    public Func<string?> OnPath { get; init; } = () => null;

    public Func<string, bool> FileExists { get; init; } = File.Exists;

    /// <summary>A path with its links resolved (<c>/usr/bin/dotnet</c> → <c>/usr/lib/dotnet/dotnet</c>).</summary>
    public Func<string, string> RealPath { get; init; } = DotnetRootResolver.Default.RealPath;

    /// <summary>The versions of a framework under a root; null: <see cref="HostRuntimeCheck.InstalledVersions"/> with <see cref="Environment"/>.</summary>
    public Func<string, string, IReadOnlyList<FrameworkVersion>>? InstalledVersions { get; init; }

    private string Exe => IsWindows ? "dotnet.exe" : "dotnet";

    /// <summary>The muxers to try, in order (duplicates and the ones that don't exist included).</summary>
    public IReadOnlyList<string> Candidates()
    {
        List<string> candidates = [];
        if (Environment("DOTNET_ROOT") is { Length: > 0 } root)
        {
            candidates.Add(Path.Combine(root, Exe));
        }

        candidates.Add(Path.Combine(Home, ".dotnet", Exe));
        if (OnPath() is { Length: > 0 } onPath)
        {
            candidates.Add(onPath);
        }

        candidates.AddRange(IsWindows
            ? [Path.Combine(Environment("ProgramFiles") ?? @"C:\Program Files", "dotnet", Exe)]
            : ["/usr/share/dotnet/dotnet", "/usr/lib/dotnet/dotnet", "/usr/local/share/dotnet/dotnet", "/opt/homebrew/opt/dotnet/libexec/dotnet"]);
        return candidates;
    }

    /// <summary>
    /// The first candidate under which every one of <paramref name="frameworks"/> resolves; else the first that exists;
    /// null when none does. No frameworks to check: the first that exists.
    /// </summary>
    public string? Find(IReadOnlyList<FrameworkReference> frameworks)
    {
        var existing = Candidates().Where(FileExists).ToList();
        var installed = InstalledVersions ?? ((root, framework) => HostRuntimeCheck.InstalledVersions(root, framework, Environment));
        if (frameworks.Count > 0)
        {
            foreach (var candidate in existing)
            {
                if (Path.GetDirectoryName(RealPath(candidate)) is { Length: > 0 } root
                    && frameworks.All(f => FrameworkResolver.Resolve(f, installed(root, f.Name)) is not null))
                {
                    return candidate;
                }
            }
        }

        return existing.FirstOrDefault();
    }

    /// <summary>
    /// The <c>Microsoft.NETCore.App</c> references of the hosts in a server bundle: the <c>runtimeconfig.json</c> files in
    /// its folders (<c>buildhost/</c>, <c>lshost/</c>…), read with the environment's roll-forward settings. None found
    /// (development, a bundle without hosts): <paramref name="fallback"/>, the runtime this server was built for.
    /// </summary>
    public static IReadOnlyList<FrameworkReference> HostFrameworks(string? bundleFolder, Func<string, string?> environment, FrameworkReference? fallback)
    {
        List<FrameworkReference> references = [];
        if (bundleFolder is not null && Directory.Exists(bundleFolder))
        {
            try
            {
                foreach (var config in Directory.EnumerateDirectories(bundleFolder).Order(StringComparer.Ordinal)
                    .SelectMany(folder => Directory.EnumerateFiles(folder, "*.runtimeconfig.json")))
                {
                    if (RuntimeConfig.Read(config, environment) is { } read)
                    {
                        references.AddRange(read.Frameworks.Where(f => f.Name == FrameworkReference.NetCoreApp));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return references.Count > 0 ? references.Distinct().ToList() : fallback is null ? [] : [fallback];
    }

    /// <summary>
    /// <c>Microsoft.NETCore.App</c> at this process's runtime version, with <c>rollForward</c> Major as the hosts ask:
    /// from <see cref="RuntimeInformation.FrameworkDescription"/> (".NET 11.0.0-rc.2.26504.105", which keeps a
    /// pre-release's label), else <see cref="System.Environment.Version"/>.
    /// </summary>
    public static FrameworkReference OwnRuntime()
    {
        var description = RuntimeInformation.FrameworkDescription.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        var version = FrameworkVersion.TryParse(description, out var parsed) ? parsed : FrameworkVersion.Parse(System.Environment.Version.ToString(3));
        return FrameworkReference.Create(FrameworkReference.NetCoreApp, version, RollForwardPolicy.Major);
    }
}
