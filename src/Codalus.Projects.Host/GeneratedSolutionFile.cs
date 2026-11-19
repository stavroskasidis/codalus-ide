namespace Codalus.Projects.Host;

/// <summary>The solutions and filters the host generates as builds' and restores' entry points (<c>build.slnf</c>, <c>folder.slnx</c>).</summary>
internal static class GeneratedSolutionFile
{
    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/>, unless the file already holds it.
    /// </summary>
    /// <remarks>
    /// Each build and restore writes its entry point again, and a restore started before (a <c>dotnet restore</c> of the
    /// same file, which a build waits for only after writing) may be reading it then: on Windows the write failed with a
    /// sharing violation and the build didn't run ("The build could not run: The process cannot access the file
    /// '…\build.slnf'", <c>ComposeProjectTests</c> in a loaded local run, docs/ci-flakes.md). The same text, as it nearly
    /// always is, isn't written again; a changed one (a project unloaded meanwhile) retries a sharing violation for a few
    /// seconds, as <c>ProjectXml.SaveFile</c> does.
    /// </remarks>
    public static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(path) && Read(path) == text)
                {
                    return;
                }

                File.WriteAllText(path, text);
                return;
            }
            catch (IOException) when (attempt < 10 && OperatingSystem.IsWindows())
            {
                // A sharing violation: a restore (or this host's other write of the same file) has it open this moment.
                Thread.Sleep(100 * attempt);
            }
        }
    }

    /// <summary>The file's text, read beside whoever has it open, writers included.</summary>
    private static string Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
