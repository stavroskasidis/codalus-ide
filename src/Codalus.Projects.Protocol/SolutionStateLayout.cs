namespace Codalus.Projects.Protocol;

/// <summary>
/// Where a solution's per-user state lives (ADR 0070, roadmap F7): <c>&lt;solution folder&gt;/.codalus/state/</c>, one
/// folder per solution file (<c>App.slnx/</c>; several solutions can share a folder) and <c>folder/</c> for a folder
/// opened without a solution. The folder ignores itself in Git (its own <c>.gitignore</c> holds <c>*</c>). Shared by
/// the shell, which keeps the session there, and the build host, which keeps the unloaded projects there.
/// </summary>
public static class SolutionStateLayout
{
    /// <summary>The state folder of a folder opened without a solution (folder mode, ADR 0033).</summary>
    public const string FolderModeName = "folder";

    /// <summary>The unloaded projects, written by the build host (ADR 0013).</summary>
    public const string UnloadedProjectsFile = "solution.json";

    /// <summary><c>&lt;folder&gt;/.codalus/state</c>.</summary>
    public static string Root(string solutionFolder) => Path.Combine(solutionFolder, ".codalus", "state");

    /// <summary>The folder that holds the solution's (or the folder's) state files.</summary>
    public static string Directory(string solutionPath, bool isFolder) =>
        isFolder
            ? Path.Combine(Root(Path.TrimEndingDirectorySeparator(solutionPath)), FolderModeName)
            : Path.Combine(Root(Path.GetDirectoryName(solutionPath)!), Path.GetFileName(solutionPath));

    /// <summary>
    /// Creates the state folder of a solution, and, the first time, <c>.codalus/state/.gitignore</c> with <c>*</c> so
    /// the state never shows in Git; on Windows the new <c>state</c> folder is hidden, as the classic IDE hides
    /// <c>.vs</c> (<c>.codalus</c> itself stays visible: it also holds shared settings, tasks and launch files).
    /// Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the folder can't be written.
    /// </summary>
    public static string EnsureDirectory(string solutionPath, bool isFolder)
    {
        var directory = Directory(solutionPath, isFolder);
        var root = Path.GetDirectoryName(directory)!;
        var created = !System.IO.Directory.Exists(root);
        System.IO.Directory.CreateDirectory(directory);
        var gitignore = Path.Combine(root, ".gitignore");
        if (!File.Exists(gitignore))
        {
            WriteAllTextAtomic(gitignore, "# Codalus IDE's per-user state for the solutions in this folder: never committed.\n*\n");
        }

        if (created && OperatingSystem.IsWindows())
        {
            try
            {
                File.SetAttributes(root, File.GetAttributes(root) | FileAttributes.Hidden);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Cosmetic only.
            }
        }

        return directory;
    }

    /// <summary>Writes through a temporary file and a rename, so a crash or a full disk never leaves half a file.</summary>
    public static void WriteAllTextAtomic(string path, string text)
    {
        var temp = path + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The original error is what matters.
            }

            throw;
        }
    }
}
