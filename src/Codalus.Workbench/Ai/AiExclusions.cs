using System.Collections.Concurrent;
using System.Text;
using Codalus.Workbench.FileSystem;

namespace Codalus.Workbench.Ai;

/// <summary>
/// Which files may go to a language model (ADR 0062): none that <c>.gitignore</c> (the repository's, nested ones and
/// <c>.git/info/exclude</c>) or a <c>.codalusignore</c> excludes, none that the <c>ai.exclude</c> globs name, and none that
/// hold secrets by name. Ignore files are read again when they change, through <see cref="FileSystems"/>, so a remote
/// window's are read on its machine.
/// </summary>
public sealed class AiExclusions
{
    /// <summary>Files that hold secrets by their name (globs on the file name).</summary>
    public static readonly IReadOnlyList<string> SecretFiles =
    [
        ".env", ".env.*", "*.env", "*.pfx", "*.p12", "*.key", "*.pem", "id_rsa", "id_rsa.*", "id_dsa", "id_ecdsa", "id_ed25519",
        "secrets.json", "*.snk", "*.publishsettings", ".npmrc", ".netrc", "_netrc", ".git-credentials", "*.keystore", "*.jks",
    ];

    private static readonly IgnoreRules s_secretRules = IgnoreRules.Parse(SecretFiles);
    private static readonly string[] s_ignoreFiles = [".gitignore", ".codalusignore"];

    private readonly ConcurrentDictionary<string, (DateTime Stamp, IgnoreRules Rules)> _files = new(StringComparer.Ordinal);
    private readonly Func<IReadOnlyList<string>> _excludeSetting;

    /// <param name="excludeSetting">The <c>ai.exclude</c> setting's globs (relative to the workspace root).</param>
    public AiExclusions(Func<IReadOnlyList<string>>? excludeSetting = null) => _excludeSetting = excludeSetting ?? (() => []);

    /// <summary>
    /// Why <paramref name="path"/> must not be sent, or null when it may be. <paramref name="workspaceRoot"/> is the
    /// solution's or folder's directory; a file outside it is checked only by name.
    /// </summary>
    public async Task<string?> ExclusionReasonAsync(string path, string? workspaceRoot, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var name = Path.GetFileName(fullPath);
        if (s_secretRules.Decide(name, isDirectory: false) == true)
        {
            return "holds secrets";
        }

        if (workspaceRoot is not { Length: > 0 })
        {
            return null;
        }

        var root = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var relativeToRoot = Relative(root, fullPath);
        if (relativeToRoot is null)
        {
            return null;
        }

        var settingRules = IgnoreRules.Parse(_excludeSetting());
        if (!settingRules.IsEmpty && settingRules.IsIgnored(relativeToRoot))
        {
            return "ai.exclude";
        }

        // Ignore files from the repository's root (or the workspace's, outside a repository) down to the file's folder.
        var top = await RepositoryRootAsync(root, cancellationToken).ConfigureAwait(false) ?? root;
        var relative = Relative(top, fullPath)!;
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var sources = new List<(string Base, IgnoreRules Rules, string Kind)>();
        var exclude = await RulesAsync(Path.Combine(top, ".git", "info", "exclude"), cancellationToken).ConfigureAwait(false);
        if (!exclude.IsEmpty)
        {
            sources.Add(("", exclude, ".git/info/exclude"));
        }

        var folder = top;
        for (var i = 0; i < parts.Length; i++)
        {
            var baseRelative = string.Join('/', parts, 0, i);
            foreach (var file in s_ignoreFiles)
            {
                var rules = await RulesAsync(Path.Combine(folder, file), cancellationToken).ConfigureAwait(false);
                if (!rules.IsEmpty)
                {
                    sources.Add((baseRelative, rules, file));
                }
            }

            if (i < parts.Length - 1)
            {
                folder = Path.Combine(folder, parts[i]);
            }
        }

        // Each folder above the file, then the file: the last rule that matches decides (deeper files win), and an
        // ignored folder ignores everything in it.
        for (var depth = 1; depth <= parts.Length; depth++)
        {
            var target = string.Join('/', parts, 0, depth);
            var isDirectory = depth < parts.Length;
            bool? decision = null;
            string? kind = null;
            foreach (var (baseRelative, rules, sourceKind) in sources)
            {
                if (baseRelative.Length > 0 && !target.StartsWith(baseRelative + "/", StringComparison.Ordinal))
                {
                    continue;
                }

                var rel = baseRelative.Length == 0 ? target : target[(baseRelative.Length + 1)..];
                if (rules.Decide(rel, isDirectory) is { } d)
                {
                    decision = d;
                    kind = sourceKind;
                }
            }

            if (decision == true)
            {
                return kind;
            }
        }

        return null;
    }

    public async Task<bool> IsExcludedAsync(string path, string? workspaceRoot, CancellationToken cancellationToken) =>
        await ExclusionReasonAsync(path, workspaceRoot, cancellationToken).ConfigureAwait(false) is not null;

    private async Task<IgnoreRules> RulesAsync(string file, CancellationToken cancellationToken)
    {
        var fileSystem = FileSystems.For(file);
        FileEntryInfo info;
        try
        {
            info = await fileSystem.StatAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return IgnoreRules.Empty;
        }

        if (!info.Exists || info.IsDirectory)
        {
            _files.TryRemove(file, out _);
            return IgnoreRules.Empty;
        }

        if (_files.TryGetValue(file, out var cached) && cached.Stamp == info.ModifiedUtc)
        {
            return cached.Rules;
        }

        IgnoreRules rules;
        try
        {
            rules = IgnoreRules.Parse(Encoding.UTF8.GetString(await fileSystem.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            rules = IgnoreRules.Empty;
        }

        _files[file] = (info.ModifiedUtc, rules);
        return rules;
    }

    private static async Task<string?> RepositoryRootAsync(string folder, CancellationToken cancellationToken)
    {
        for (var dir = folder; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            var git = Path.Combine(dir, ".git");
            try
            {
                if ((await FileSystems.For(git).StatAsync(git, cancellationToken).ConfigureAwait(false)).Exists)
                {
                    return dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary><paramref name="path"/> relative to <paramref name="root"/> with forward slashes, or null when it isn't under it.</summary>
    private static string? Relative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        return relative.Replace('\\', '/');
    }
}
