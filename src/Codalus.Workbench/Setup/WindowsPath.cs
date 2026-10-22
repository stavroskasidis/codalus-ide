namespace Codalus.Workbench.Setup;

/// <summary>
/// The PATH Windows gives new processes (ADR 0083): an installer (winget's Node.js) changes it in the registry, and a
/// running Codalus keeps the PATH it started with, so a program just installed stays "missing" until a restart. This reads
/// the machine's and the user's <c>Environment\Path</c> again (expanded) and adds their new folders to this process's PATH,
/// at its end; nothing is removed or reordered. Hosts started later inherit them.
/// </summary>
public static class WindowsPath
{
    /// <summary>The folders of the registry's PATH (machine, then user), expanded; empty off Windows or when it can't be read.</summary>
    public static IReadOnlyList<string> ReadRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var folders = new List<string>();
        try
        {
            using (var machine = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"))
            {
                folders.AddRange(Split(machine?.GetValue("Path") as string));
            }

            using var user = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment");
            folders.AddRange(Split(user?.GetValue("Path") as string));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // What was read by then.
        }

        return folders;

        static IEnumerable<string> Split(string? path) =>
            (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Environment.ExpandEnvironmentVariables);
    }

    /// <summary>
    /// <paramref name="current"/> with the folders of <paramref name="registry"/> it doesn't have added at its end (compared
    /// without case and a trailing separator, as Windows does); the folders added, for the log.
    /// </summary>
    public static (string Path, IReadOnlyList<string> Added) Merge(string? current, IEnumerable<string> registry)
    {
        var folders = (current ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        var known = folders.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<string>();
        foreach (var folder in registry)
        {
            if (folder.Length > 0 && known.Add(Normalize(folder)))
            {
                folders.Add(folder);
                added.Add(folder);
            }
        }

        return (string.Join(Path.PathSeparator, folders), added);

        static string Normalize(string folder) => folder.Trim().TrimEnd('\\', '/');
    }

    /// <summary>Reads the registry's PATH and adds its new folders to this process's PATH (Windows only); the folders added.</summary>
    public static IReadOnlyList<string> Refresh(Func<IReadOnlyList<string>>? read = null)
    {
        if (!OperatingSystem.IsWindows() && read is null)
        {
            return [];
        }

        var (path, added) = Merge(Environment.GetEnvironmentVariable("PATH"), (read ?? ReadRegistry)());
        if (added.Count > 0)
        {
            Environment.SetEnvironmentVariable("PATH", path);
        }

        return added;
    }
}
