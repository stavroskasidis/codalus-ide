namespace DaedalusStudio.Platform;

/// <summary>
/// Resolves symbolic links in every component of a path (like POSIX <c>realpath</c>, without P/Invoke). A solution
/// opened through a link (macOS's <c>/var</c> → <c>/private/var</c>) would otherwise be seen under two spellings:
/// MSBuild nodes, compilers and PDBs use the real one, so restores race and breakpoints don't bind.
/// </summary>
public static class RealPath
{
    public static string Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            return full; // junctions and links are rare in solution paths there; tools see the same spelling
        }

        try
        {
            var root = Path.GetPathRoot(full) ?? "/";
            var current = root;
            var parts = new Queue<string>(full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries));
            var hops = 0;
            while (parts.TryDequeue(out var part))
            {
                var next = Path.Combine(current, part);
                FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
                if (info.LinkTarget is { } target && hops++ < 40)
                {
                    var resolved = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(current, target));
                    var rest = parts.ToArray();
                    parts = new Queue<string>(resolved[(Path.GetPathRoot(resolved) ?? "/").Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Concat(rest));
                    current = Path.GetPathRoot(resolved) ?? "/";
                    continue;
                }

                current = next;
            }

            return current;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return full;
        }
    }
}
