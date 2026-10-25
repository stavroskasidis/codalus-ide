using System.Xml;
using System.Xml.Linq;
using Codalus.Projects.Protocol;

namespace Codalus.Projects.Host;

/// <summary>
/// Docker Compose projects (<c>.dcproj</c>, M17a, ADR 0042): the project file the classic IDE adds for a compose file.
/// Its SDK (<c>Microsoft.Docker.Sdk</c>) ships only with the classic IDE, so it is never evaluated or built: its items
/// are read from the XML, and it loads as a solution item whose compose file the shell runs.
/// </summary>
internal static class ComposeProject
{
    public static bool Is(string path) => path.EndsWith(".dcproj", StringComparison.OrdinalIgnoreCase);

    public static EvaluatedProject Read(string path, string name)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(path);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return new EvaluatedProject(new ProjectInfo(path, name, ProjectStates.Failed, ex.Message, [], null, [], [], [], false), []);
        }

        var folder = System.IO.Path.GetDirectoryName(path)!;
        var items = new List<ProjectItemInfo>();
        foreach (var item in document.Descendants().Where(e => e.Name.LocalName is "None" or "Content" && e.Attribute("Include") is not null))
        {
            foreach (var include in item.Attribute("Include")!.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (include.Contains('*', StringComparison.Ordinal))
                {
                    continue;
                }

                var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, include.Replace('\\', System.IO.Path.DirectorySeparatorChar)));
                var dependentUpon = item.Elements().FirstOrDefault(e => e.Name.LocalName == "DependentUpon")?.Value.Trim();
                var link = full.StartsWith(folder + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? null : System.IO.Path.GetFileName(full);
                items.Add(new ProjectItemInfo("None", full, link, string.IsNullOrEmpty(dependentUpon) ? null : dependentUpon));
            }
        }

        var info = new ProjectInfo(path, name, ProjectStates.Loaded, null, [], null, [.. items], [], [], true);
        return new EvaluatedProject(info, []);
    }
}
