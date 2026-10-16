using System.Xml.Linq;

namespace Codalus.Workbench.Testing;

/// <summary>
/// Which test framework a solution's test projects use, from their project files (the SDK and package references):
/// MSTest, NUnit, TUnit or xUnit (the default), as Create Unit Tests decides (M8). Used to ask AI for tests (M28b).
/// </summary>
public static class TestFrameworks
{
    public static string Detect(IEnumerable<string> testProjects)
    {
        foreach (var project in testProjects)
        {
            if (Detect(project) is { } framework)
            {
                return framework;
            }
        }

        return "xUnit";
    }

    /// <summary>The framework of one project file, or null when it can't be read or names none.</summary>
    public static string? Detect(string projectPath)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(projectPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }

        var sdk = document.Root?.Attribute("Sdk")?.Value ?? "";
        var packages = document.Descendants().Where(e => e.Name.LocalName == "PackageReference").Select(e => e.Attribute("Include")?.Value ?? "").ToList();
        bool Has(string prefix) => packages.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return sdk.StartsWith("MSTest.Sdk", StringComparison.OrdinalIgnoreCase) || Has("MSTest") ? "MSTest"
            : Has("NUnit") ? "NUnit"
            : Has("TUnit") ? "TUnit"
            : Has("xunit") ? "xUnit"
            : null;
    }
}
