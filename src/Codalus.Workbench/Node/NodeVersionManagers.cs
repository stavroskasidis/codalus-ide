using System.Text.Json;
using Codalus.Platform;

namespace Codalus.Workbench.Node;

/// <summary>A Node.js a version manager installed: the manager, the folder with its <c>node</c> and <c>npx</c>, and its version.</summary>
/// <param name="Manager">The manager's id (<see cref="NodeVersionManagers.Names"/>).</param>
/// <param name="Folder">The folder with <c>node</c> and <c>npx</c>.</param>
/// <param name="Version">Its version, without the <c>v</c> (<c>26.13.1</c>).</param>
public sealed record ManagedNode(string Manager, string Folder, string Version)
{
    /// <summary>The manager's name as its users write it (<c>nvm</c>, <c>Volta</c>).</summary>
    public string ManagerName => NodeVersionManagers.DisplayName(Manager);
}

/// <summary>
/// Node.js from the version managers (nvm, fnm, Volta, asdf, mise), found in their own folders on macOS and Linux. Their
/// Node.js is on the PATH only of the shells whose startup files load them: nvm set up in <c>~/.bashrc</c> while the
/// login shell is fish or zsh leaves the IDE, which reads the login shell's PATH, with the distribution's Node.js
/// without npx, or none. The Connect dialog offers what is found here (ADR 0083's D-node, as for Codalus's own); an
/// agent's entry names the manager (<c>"node": "nvm"</c>), and its Node.js is chosen again each time the agent starts.
/// Only the file system is read: nothing runs.
/// </summary>
public static class NodeVersionManagers
{
    /// <summary>The managers looked for, in the order they're offered.</summary>
    public static IReadOnlyList<string> Names { get; } = ["nvm", "fnm", "volta", "asdf", "mise"];

    /// <summary>The manager's name as its users write it.</summary>
    public static string DisplayName(string manager) => manager == "volta" ? "Volta" : manager;

    /// <summary>
    /// Each manager's Node.js for an agent that needs <paramref name="major"/> or later (null: any), with <c>node</c> and
    /// <c>npx</c>: its default when that is new enough, else its newest that is. None on Windows.
    /// </summary>
    /// <param name="environment">Reads the variables that move the managers' folders (<c>HOME</c>, <c>NVM_DIR</c>…).</param>
    public static IReadOnlyList<ManagedNode> Find(Func<string, string?> environment, int? major, PlatformKind platform) =>
        [.. Names.Select(manager => Resolve(manager, environment, major, platform)).OfType<ManagedNode>()];

    /// <summary>The Node.js <paramref name="manager"/> gives an agent that needs <paramref name="major"/> or later, or null (<see cref="Find"/>).</summary>
    public static ManagedNode? Resolve(string manager, Func<string, string?> environment, int? major, PlatformKind platform)
    {
        if (platform == PlatformKind.Windows || environment("HOME") is not { Length: > 0 } home)
        {
            return null;
        }

        var (installed, preferred) = manager switch
        {
            "nvm" => Nvm(environment, home),
            "fnm" => Fnm(environment, home, platform),
            "volta" => Volta(environment, home),
            "asdf" => Asdf(environment, home),
            "mise" => Mise(environment, home),
            _ => ([], null),
        };

        var usable = installed
            .Where(v => (major is null || v.Version.Major >= major) && IsProgram(Path.Combine(v.Folder, "node")) && IsProgram(Path.Combine(v.Folder, "npx")))
            .OrderByDescending(v => v.Version)
            .ToList();
        if (usable.Count == 0)
        {
            return null;
        }

        var chosen = preferred is { } wanted && usable.FindIndex(v => Matches(v.Version, wanted)) is var at and >= 0 ? usable[at] : usable[0];
        return new ManagedNode(manager, chosen.Folder, Text(chosen.Version));
    }

    private static readonly char[] s_blanks = [' ', '\t'];

    private readonly record struct Installed(Version Version, string Folder);

    /// <summary><c>$NVM_DIR/versions/node/v*/bin</c>; the default is <c>alias/default</c>, followed through other aliases.</summary>
    private static (IReadOnlyList<Installed>, string?) Nvm(Func<string, string?> environment, string home)
    {
        var root = environment("NVM_DIR") is { Length: > 0 } dir ? dir : Path.Combine(home, ".nvm");
        var installed = Versions(Path.Combine(root, "versions", "node"), version => Path.Combine(version, "bin"));
        string? alias = "default";
        for (var hop = 0; hop < 5 && alias is not null; hop++)
        {
            var text = ReadFirstLine(Path.Combine(root, "alias", alias));
            if (text is null or "node" or "stable")
            {
                return (installed, null); // nvm's newest
            }

            if (VersionLike(text))
            {
                return (installed, text); // v26.13.1, or 26 for the newest 26
            }

            alias = text; // lts/* → alias/lts/*, which names a version or another alias
        }

        return (installed, null);
    }

    /// <summary><c>$FNM_DIR/node-versions/v*/installation/bin</c>; the default is the folder the <c>aliases/default</c> link points to.</summary>
    private static (IReadOnlyList<Installed>, string?) Fnm(Func<string, string?> environment, string home, PlatformKind platform)
    {
        var data = environment("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".local", "share");
        var root = environment("FNM_DIR") is { Length: > 0 } dir ? dir
            : platform == PlatformKind.MacOS && Directory.Exists(Path.Combine(home, "Library", "Application Support", "fnm")) ? Path.Combine(home, "Library", "Application Support", "fnm")
            : Directory.Exists(Path.Combine(data, "fnm")) ? Path.Combine(data, "fnm")
            : Path.Combine(home, ".fnm");
        var installed = Versions(Path.Combine(root, "node-versions"), version => Path.Combine(version, "installation", "bin"));
        string? preferred = null;
        try
        {
            // aliases/default → …/node-versions/v26.13.1/installation
            if (new DirectoryInfo(Path.Combine(root, "aliases", "default")).ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                preferred = Path.GetFileName(Path.GetDirectoryName(target.FullName.TrimEnd('/')));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return (installed, preferred);
    }

    /// <summary><c>$VOLTA_HOME/tools/image/node/*/bin</c>; the default is <c>tools/user/platform.json</c>'s <c>node.runtime</c>.</summary>
    private static (IReadOnlyList<Installed>, string?) Volta(Func<string, string?> environment, string home)
    {
        var root = environment("VOLTA_HOME") is { Length: > 0 } dir ? dir : Path.Combine(home, ".volta");
        var installed = Versions(Path.Combine(root, "tools", "image", "node"), version => Path.Combine(version, "bin"));
        string? preferred = null;
        try
        {
            var platform = Path.Combine(root, "tools", "user", "platform.json");
            if (File.Exists(platform))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(platform));
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("node", out var node)
                    && node.ValueKind == JsonValueKind.Object && node.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.String)
                {
                    preferred = runtime.GetString();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return (installed, preferred);
    }

    /// <summary><c>$ASDF_DATA_DIR/installs/nodejs/*/bin</c>; the default is <c>~/.tool-versions</c>' <c>nodejs</c> line.</summary>
    private static (IReadOnlyList<Installed>, string?) Asdf(Func<string, string?> environment, string home)
    {
        var root = environment("ASDF_DATA_DIR") is { Length: > 0 } dir ? dir : Path.Combine(home, ".asdf");
        var installed = Versions(Path.Combine(root, "installs", "nodejs"), version => Path.Combine(version, "bin"));
        var preferred = ReadLines(Path.Combine(home, ".tool-versions"))
            .Select(l => l.Split(s_blanks, StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(p => p.Length > 1 && p[0] == "nodejs")?[1];
        return (installed, preferred);
    }

    /// <summary>
    /// <c>$MISE_DATA_DIR/installs/node/*/bin</c> (its own links, <c>26</c> → <c>26.13.1</c> and <c>latest</c>, left out);
    /// the default is <c>node = "…"</c> under <c>[tools]</c> in its global <c>config.toml</c>.
    /// </summary>
    private static (IReadOnlyList<Installed>, string?) Mise(Func<string, string?> environment, string home)
    {
        var data = environment("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".local", "share");
        var root = environment("MISE_DATA_DIR") is { Length: > 0 } dir ? dir : Path.Combine(data, "mise");
        var installed = Versions(Path.Combine(root, "installs", "node"), version => Path.Combine(version, "bin"));
        var config = environment("XDG_CONFIG_HOME") is { Length: > 0 } xdgConfig ? xdgConfig : Path.Combine(home, ".config");
        string? section = null, preferred = null;
        foreach (var raw in ReadLines(Path.Combine(config, "mise", "config.toml")))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                section = line.Trim('[', ']').Trim();
            }
            else if (section == "tools" && line.Split('=', 2) is [var key, var value] && key.Trim() == "node")
            {
                preferred = value.Trim().Trim('[', ']').Split(',')[0].Trim().Trim('"', '\'');
                break;
            }
        }

        return (installed, preferred);
    }

    /// <summary>The version folders in <paramref name="folder"/> (named <c>v26.13.1</c> or <c>26.13.1</c>, not links), each with its bin folder.</summary>
    private static List<Installed> Versions(string folder, Func<string, string> bin)
    {
        var found = new List<Installed>();
        try
        {
            if (!Directory.Exists(folder))
            {
                return found;
            }

            foreach (var directory in new DirectoryInfo(folder).EnumerateDirectories())
            {
                if (directory.LinkTarget is null && Parse(directory.Name) is { } version)
                {
                    found.Add(new Installed(version, bin(directory.FullName)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return found;
    }

    /// <summary><c>v26.13.1</c> or <c>26.13.1</c> as a version, or null.</summary>
    private static Version? Parse(string text)
    {
        var trimmed = text.Trim().TrimStart('v');
        return trimmed.Split('.').Length == 3 && Version.TryParse(trimmed, out var version) ? version : null;
    }

    /// <summary>A version or its first parts (<c>v26.13.1</c>, <c>26</c>), not an alias's name (<c>lts/*</c>).</summary>
    private static bool VersionLike(string text) =>
        text.TrimStart('v').Split('.') is { Length: <= 3 } parts && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit));

    /// <summary>Whether <paramref name="version"/> is the one <paramref name="wanted"/> names, in full or by its first parts (<c>26</c>, <c>v26.13</c>).</summary>
    private static bool Matches(Version version, string wanted)
    {
        var parts = wanted.Trim().TrimStart('v').Split('.');
        int[] have = [version.Major, version.Minor, version.Build];
        return parts.Length is > 0 and <= 3
            && parts.Select((p, i) => int.TryParse(p, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n == have[i]).All(b => b);
    }

    private static string Text(Version version) => $"{version.Major}.{version.Minor}.{version.Build}";

    private static string? ReadFirstLine(string file) => ReadLines(file).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

    private static string[] ReadLines(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllLines(file) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsProgram(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return File.Exists(file) && (File.GetUnixFileMode(file) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
