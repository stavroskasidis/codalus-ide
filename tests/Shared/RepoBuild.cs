using System.Reflection;

namespace Codalus.Tests;

/// <summary>
/// The target framework the repository's projects build for (<c>Directory.Build.props</c>), so tests find the hosts,
/// plugins and servers under <c>bin/&lt;configuration&gt;/&lt;framework&gt;/</c> without naming a .NET version (M30b).
/// The plugin API and the sample plugins stay on <c>net10.0</c>.
/// </summary>
internal static class RepoBuild
{
    public static string TargetFramework { get; } =
        typeof(RepoBuild).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "CodalusTargetFramework").Value!;
}
