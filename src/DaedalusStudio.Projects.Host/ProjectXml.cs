using Microsoft.Build.Construction;

namespace DaedalusStudio.Projects.Host;

/// <summary>Saving project files edited through MSBuild's construction model.</summary>
internal static class ProjectXml
{
    /// <summary>
    /// Saves <paramref name="xml"/>. MSBuild verifies after writing that the element isn't dirty again; on Linux that check
    /// sometimes races with evaluations in other collections (MSB0001 "Should not be dirty now") although the file has
    /// been written. That one error is ignored; anything else propagates.
    /// </summary>
    public static void SaveFile(this ProjectRootElement xml)
    {
        try
        {
            xml.Save();
        }
        catch (Exception ex) when (ex.GetType().Name == "InternalErrorException" && ex.Message.Contains("Should not be dirty now", StringComparison.Ordinal))
        {
        }
    }
}
