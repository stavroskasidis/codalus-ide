using NuGet.Frameworks;

namespace Codalus.Projects.Host.Packages;

/// <summary>
/// Whether a package version supports a project's target frameworks (M33, ADR 0030), from the frameworks of its
/// dependency groups: <c>nuget pack</c> writes one group per framework the package has assemblies for, empty or not.
/// It is an estimate: a package without groups (or with only a framework-less one) says nothing, and a group doesn't
/// prove that the package has assemblies for it.
/// </summary>
internal static class FrameworkCompatibility
{
    /// <summary>The project's frameworks, parsed (unknown ones are left out).</summary>
    public static IReadOnlyList<NuGetFramework> Parse(IEnumerable<string>? targetFrameworks)
    {
        var result = new List<NuGetFramework>();
        foreach (var name in targetFrameworks ?? [])
        {
            var framework = NuGetFramework.Parse(name);
            if (framework.IsUnsupported || IsMisread(name, framework))
            {
                continue;
            }

            result.Add(framework);
        }

        return result;
    }

    /// <summary>
    /// True when every framework of the project has a compatible group, false when one doesn't, null when the package
    /// lists no frameworks.
    /// </summary>
    public static bool? Supports(IEnumerable<NuGetFramework> packageFrameworks, IReadOnlyList<NuGetFramework> projectFrameworks)
    {
        var groups = packageFrameworks.ToList();
        if (projectFrameworks.Count == 0 || groups.Count == 0 || groups.Any(g => g.IsAny || g.IsAgnostic))
        {
            return null;
        }

        var compatibility = DefaultCompatibilityProvider.Instance;
        return projectFrameworks.All(project => groups.Any(group => compatibility.IsCompatible(project, group)));
    }

    /// <summary>
    /// NuGet before 5.8 (the SDKs before .NET 5) reads <c>net5.0</c> and later as .NET Framework 5.0: those frameworks'
    /// compatibility isn't known with such an SDK.
    /// </summary>
    private static bool IsMisread(string name, NuGetFramework framework) =>
        name.StartsWith("net", StringComparison.OrdinalIgnoreCase) && name.Contains('.', StringComparison.Ordinal)
        && !name.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase)
        && framework.Framework == FrameworkConstants.FrameworkIdentifiers.Net;
}
