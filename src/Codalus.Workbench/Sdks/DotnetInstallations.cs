using Codalus.Platform;
using Codalus.Workbench.Setup;

namespace Codalus.Workbench.Sdks;

/// <summary>
/// The machine whose .NET the SDK manager reads and changes (ADR 0113): this one, and later WSL and a remote machine
/// over the remote server's <c>fs/*</c> messages (<c>TODO(issue)</c>: WSL and remote machines in the SDK manager). Paths
/// are the machine's own.
/// </summary>
public interface IDotnetMachine
{
    PlatformKind Platform { get; }

    /// <summary>The user's home folder.</summary>
    string Home { get; }

    bool DirectoryExists(string path);

    /// <summary>The names of a folder's subfolders; empty when it doesn't exist.</summary>
    IReadOnlyList<string> Subfolders(string path);

    /// <summary>The names of a folder's files; empty when it doesn't exist.</summary>
    IReadOnlyList<string> Files(string path);

    /// <summary>Whether the user can write into a folder without a password.</summary>
    bool CanWrite(string path);

    /// <summary>Deletes a folder and what's in it.</summary>
    void DeleteDirectory(string path);
}

/// <summary>This machine's file system.</summary>
public sealed class LocalDotnetMachine(PlatformKind platform) : IDotnetMachine
{
    public PlatformKind Platform => platform;

    public string Home { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> Subfolders(string path)
    {
        try
        {
            return Directory.Exists(path) ? [.. Directory.EnumerateDirectories(path).Select(Path.GetFileName).OfType<string>().Where(n => n.Length > 0)] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public IReadOnlyList<string> Files(string path)
    {
        try
        {
            return Directory.Exists(path) ? [.. Directory.EnumerateFiles(path).Select(Path.GetFileName).OfType<string>().Where(n => n.Length > 0)] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public bool CanWrite(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        // Writing is the only answer every OS gives the same way (ACLs, read-only mounts, root-owned folders).
        var probe = Path.Combine(path, $".codalus-write-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);
}

/// <summary>Who changes a .NET folder (ADR 0113), which decides how the manager installs, updates and removes there.</summary>
public enum DotnetRootOwner
{
    /// <summary>The user's (<c>~/.dotnet</c>, <c>%LOCALAPPDATA%\Microsoft\dotnet</c>, a folder they can write): changed directly.</summary>
    User,

    /// <summary>A Linux distribution's packages (or Microsoft's): apt, dnf, zypper, pacman, apk.</summary>
    LinuxPackage,

    /// <summary>The <c>dotnet-sdk</c> snap.</summary>
    Snap,

    /// <summary>Microsoft's installer package on macOS (<c>/usr/local/share/dotnet</c>).</summary>
    MacPackage,

    /// <summary>Microsoft's installers or winget on Windows (<c>%ProgramFiles%\dotnet</c>).</summary>
    WindowsInstaller,

    /// <summary>A folder only an administrator can write that no package manager owns (a tarball in <c>/opt</c>): sudo, in the terminal.</summary>
    Administrator,
}

/// <summary>A shared framework's version in a .NET folder (<c>shared/Microsoft.NETCore.App/10.0.4</c>).</summary>
public sealed record DotnetRuntime(string Framework, string Version);

/// <summary>A .NET folder: who owns it, whether the IDE uses it, its SDKs and runtimes, read from its folders.</summary>
public sealed record DotnetInstallation(string Root, DotnetRootOwner Owner, bool InUse)
{
    public IReadOnlyList<string> Sdks { get; init; } = [];

    public IReadOnlyList<DotnetRuntime> Runtimes { get; init; } = [];

    /// <summary>The versions of <paramref name="framework"/> here.</summary>
    public IEnumerable<string> VersionsOf(string framework) => Runtimes.Where(r => r.Framework == framework).Select(r => r.Version);
}

/// <summary>
/// The .NET folders of a machine and what's in them (ADR 0113), from the folders themselves: <c>dotnet</c> isn't run,
/// so a folder whose <c>dotnet</c> is broken still lists, and nothing waits on a process.
/// </summary>
public static class DotnetInstallations
{
    /// <summary>The folders Linux distributions' and Microsoft's own packages put .NET in.</summary>
    public static readonly string[] LinuxPackageRoots = ["/usr/lib/dotnet", "/usr/lib64/dotnet", "/usr/share/dotnet"];

    /// <summary>
    /// What's in each of <paramref name="roots"/> that exists and has an SDK or a runtime, the one the IDE uses
    /// (<paramref name="inUse"/>) first, then in the order given. A root is listed once, by its first spelling.
    /// </summary>
    public static IReadOnlyList<DotnetInstallation> Read(IDotnetMachine machine, IEnumerable<string> roots, string? inUse, Func<string, string?>? programFiles = null)
    {
        var seen = new HashSet<string>(machine.Platform == PlatformKind.Linux ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        var list = new List<DotnetInstallation>();
        foreach (var root in (inUse is null ? roots : roots.Prepend(inUse)).Select(r => Trim(r, machine.Platform)).Where(r => r.Length > 0))
        {
            if (!seen.Add(root) || !machine.DirectoryExists(root))
            {
                continue;
            }

            var separator = machine.Platform == PlatformKind.Windows ? '\\' : '/';
            string Join(params string[] parts) => string.Join(separator, parts);
            var sdks = machine.Subfolders(Join(root, "sdk")).Where(IsVersion).Order(Comparer<string>.Create(EnvironmentCheck.CompareVersions)).ToList();
            var runtimes = DotnetFrameworks.All
                .SelectMany(f => machine.Subfolders(Join(root, "shared", f)).Where(IsVersion).Select(v => new DotnetRuntime(f, v)))
                .OrderBy(r => Array.IndexOf(DotnetFrameworks.All, r.Framework)).ThenBy(r => r.Version, Comparer<string>.Create(EnvironmentCheck.CompareVersions))
                .ToList();
            if (sdks.Count == 0 && runtimes.Count == 0)
            {
                continue;
            }

            var inUseHere = inUse is not null && SameFolder(root, inUse, machine.Platform);
            list.Add(new DotnetInstallation(root, OwnerOf(machine, root, programFiles), inUseHere) { Sdks = sdks, Runtimes = runtimes });
        }

        return list;
    }

    /// <summary>
    /// Who owns a .NET folder: its place decides for the package managers' and installers' folders; else one in the home
    /// folder, or one the user can write, is the user's; anything else needs an administrator.
    /// </summary>
    public static DotnetRootOwner OwnerOf(IDotnetMachine machine, string root, Func<string, string?>? programFiles = null)
    {
        var platform = machine.Platform;
        var home = Trim(machine.Home, platform);
        var underHome = home.Length > 0 && (SameFolder(root, home, platform) || IsUnder(root, home, platform));
        switch (platform)
        {
            case PlatformKind.Linux when root.StartsWith("/snap/", StringComparison.Ordinal):
                return DotnetRootOwner.Snap;
            case PlatformKind.Linux when LinuxPackageRoots.Any(r => SameFolder(r, root, platform)):
                return DotnetRootOwner.LinuxPackage;
            case PlatformKind.MacOS when SameFolder(root, InstallPlans.MacPackageRoot, platform) || SameFolder(root, InstallPlans.MacPackageRoot + "/x64", platform):
                return DotnetRootOwner.MacPackage;
            case PlatformKind.Windows when !underHome && ProgramFolders(programFiles ?? Environment.GetEnvironmentVariable).Any(p => IsUnder(root, p, platform)):
                return DotnetRootOwner.WindowsInstaller;
        }

        return underHome || machine.CanWrite(root) ? DotnetRootOwner.User : DotnetRootOwner.Administrator;
    }

    /// <summary>Windows' program folders: its variables, else their usual places (the machine described isn't this one).</summary>
    private static List<string> ProgramFolders(Func<string, string?> variable)
    {
        string[] names = ["ProgramFiles", "ProgramFiles(x86)", "ProgramW6432"];
        var set = names.Select(variable).OfType<string>().Where(p => p.Length > 0).ToList();
        return set.Count > 0 ? set : [@"C:\Program Files", @"C:\Program Files (x86)"];
    }

    /// <summary>A folder name that is a version (<c>10.0.104</c>, <c>11.0.100-rc.2.26504.105</c>), not <c>NuGetFallbackFolder</c>.</summary>
    public static bool IsVersion(string name) => name.Length > 0 && char.IsAsciiDigit(name[0]) && name.Contains('.', StringComparison.Ordinal);

    public static bool SameFolder(string a, string b, PlatformKind platform) =>
        string.Equals(Trim(a, platform), Trim(b, platform), platform == PlatformKind.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="path"/> is inside <paramref name="folder"/> (not the folder itself).</summary>
    public static bool IsUnder(string path, string folder, PlatformKind platform)
    {
        var a = Trim(path, platform);
        var b = Trim(folder, platform);
        var separator = platform == PlatformKind.Windows ? '\\' : '/';
        return a.Length > b.Length + 1 && a[b.Length] == separator
            && a.StartsWith(b, platform == PlatformKind.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string path, PlatformKind platform)
    {
        var normal = platform == PlatformKind.Windows ? path.Replace('/', '\\') : path;
        return normal.Length > 1 ? normal.TrimEnd('/', '\\') : normal;
    }
}
