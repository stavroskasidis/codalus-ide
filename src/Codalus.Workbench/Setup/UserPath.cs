namespace Codalus.Workbench.Setup;

/// <summary>
/// The PATH the user's new terminals get, read again while the IDE runs: a program installed since it started (by hand,
/// from a download page, in another terminal) is on it, and the IDE's own PATH, taken once at startup, lacks it until a
/// restart. On Windows the registry's (<see cref="WindowsPath"/>), elsewhere the login shell's (<see cref="LoginShellPath"/>).
/// </summary>
public static class UserPath
{
    /// <summary>Adds the folders the user's PATH has now and this process's lacks to this process's; the folders added.</summary>
    public static IReadOnlyList<string> Refresh() => OperatingSystem.IsWindows() ? WindowsPath.Refresh() : LoginShellPath.Refresh();

    /// <summary>
    /// <paramref name="path"/> with <paramref name="added"/> (from <see cref="Refresh"/>) where <see cref="Refresh"/> puts
    /// them: at the end on Windows, first elsewhere; each folder once.
    /// </summary>
    public static string AddTo(string? path, IReadOnlyList<string> added) =>
        OperatingSystem.IsWindows() ? WindowsPath.Merge(path, added).Path : LoginShellPath.AddNew(path, string.Join(':', added)).Path;
}
