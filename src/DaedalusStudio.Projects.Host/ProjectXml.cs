using Microsoft.Build.Construction;

namespace DaedalusStudio.Projects.Host;

/// <summary>Saving project files edited through MSBuild's construction model.</summary>
internal static class ProjectXml
{
    /// <summary>
    /// Saves <paramref name="xml"/>. MSBuild verifies after writing that the element isn't dirty again; on Linux that check
    /// sometimes races with evaluations in other collections (MSB0001 "Should not be dirty now") although the file has
    /// been written. That one error is ignored. On Windows a sharing violation (a reader holds the file) is retried a few
    /// times; anything else propagates.
    /// </summary>
    public static void SaveFile(this ProjectRootElement xml)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                xml.Save();
                return;
            }
            catch (Exception ex) when (ex.GetType().Name == "InternalErrorException" && ex.Message.Contains("Should not be dirty now", StringComparison.Ordinal))
            {
                return;
            }
            catch (IOException) when (attempt < 5 && OperatingSystem.IsWindows())
            {
                // A sharing violation: an evaluation, a restore or a virus scanner is reading the file this moment.
                Thread.Sleep(100 * attempt);
            }
        }
    }
}
