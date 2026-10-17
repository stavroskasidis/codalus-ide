using System.Globalization;
using System.Xml.Linq;

namespace Codalus.Projects.Host;

/// <summary>
/// .NET Framework projects in the build host (M43a, ADR 0075). No MSBuild or NuGet types, so the tests compile it in.
/// <list type="bullet">
/// <item>SDK-style <c>net4x</c> projects need nothing: the SDK adds <c>Microsoft.NETFramework.ReferenceAssemblies</c>
/// (MIT) to their restore wherever the targeting pack isn't installed, on every OS.</item>
/// <item>Non-SDK-style ("old-style") projects import <c>Microsoft.CSharp.targets</c>, which the SDK's MSBuild has too,
/// but look for the reference assemblies only where Visual Studio's targeting packs install them. The build host gives
/// them the same reference assemblies package, restored into the global packages folder: a targets file imported
/// through <c>CustomBeforeMicrosoftCommonTargets</c> (an environment variable, the lowest-priority property source, as
/// <c>EnableWindowsTargeting</c>) points <c>TargetFrameworkRootPath</c> at it, as the package's own targets do for
/// SDK-style projects, when the framework's reference assemblies aren't installed.</item>
/// <item>Old-style projects that import Visual Studio's own targets (under <c>MSBuild\Microsoft\VisualStudio</c>) open
/// but can't build: those targets come only with Visual Studio's MSBuild, which is proprietary.</item>
/// </list>
/// </summary>
internal static class NetFramework
{
    /// <summary>The property MSBuild imports a file from at the start of Microsoft.Common.CurrentVersion.targets.</summary>
    public const string CustomBeforeMicrosoftCommonTargets = "CustomBeforeMicrosoftCommonTargets";

    /// <summary>The global packages folder, for the targets file (an environment variable the build host sets).</summary>
    public const string PackageRootProperty = "CodalusNuGetPackageRoot";

    /// <summary>The property the targets file sets to the reference assemblies it used (empty: it didn't).</summary>
    public const string ReferenceAssembliesProperty = "CodalusReferenceAssemblies";

    /// <summary>The reference assemblies packages' id prefix and version (the last release, 1.0.3, has every framework).</summary>
    public const string ReferenceAssembliesPackage = "Microsoft.NETFramework.ReferenceAssemblies";

    public const string ReferenceAssembliesVersion = "1.0.3";

    /// <summary>
    /// What old-style projects import (M43a). Before Microsoft.Common.CurrentVersion.targets sets
    /// <c>TargetFrameworkIdentifier</c> (an old-style project often leaves it empty for .NET Framework) and computes
    /// <c>FrameworkPathOverride</c> from <c>TargetFrameworkRootPath</c>. It applies only when MSBuild finds no reference
    /// assemblies of the framework itself (a Windows machine with the Developer Pack keeps them).
    /// </summary>
    internal const string ReferenceAssembliesTargetsText = """
        <Project>
          <!-- Written by Codalus IDE's build host (M43a, ADR 0075): .NET Framework reference assemblies from the
               Microsoft.NETFramework.ReferenceAssemblies packages for non-SDK-style projects, as SDK-style ones get them. -->
          <PropertyGroup Condition="'$(UsingMicrosoftNETSdk)' != 'true' and ('$(TargetFrameworkIdentifier)' == '' or '$(TargetFrameworkIdentifier)' == '.NETFramework') and '$(TargetFrameworkVersion)' != '' and '$(CodalusNuGetPackageRoot)' != ''">
            <_CodalusNetFxInstalled>$([Microsoft.Build.Utilities.ToolLocationHelper]::GetPathToStandardLibraries('.NETFramework', '$(TargetFrameworkVersion)', '$(TargetFrameworkProfile)'))</_CodalusNetFxInstalled>
            <_CodalusNetFxReferenceAssemblies>$([MSBuild]::EnsureTrailingSlash('$(CodalusNuGetPackageRoot)'))microsoft.netframework.referenceassemblies.net$(TargetFrameworkVersion.TrimStart('vV').Replace('.', ''))/1.0.3/build/</_CodalusNetFxReferenceAssemblies>
          </PropertyGroup>
          <PropertyGroup Condition="'$(_CodalusNetFxReferenceAssemblies)' != '' and '$(_CodalusNetFxInstalled)' == '' and Exists('$(_CodalusNetFxReferenceAssemblies).NETFramework/$(TargetFrameworkVersion)/mscorlib.dll')">
            <TargetFrameworkRootPath>$(_CodalusNetFxReferenceAssemblies)</TargetFrameworkRootPath>
            <EnableFrameworkPathOverride>false</EnableFrameworkPathOverride>
            <NoStdLib>true</NoStdLib>
            <CodalusReferenceAssemblies>$(_CodalusNetFxReferenceAssemblies)</CodalusReferenceAssemblies>
          </PropertyGroup>
          <ItemGroup Condition="'$(CodalusReferenceAssemblies)' != ''">
            <Reference Include="mscorlib" Pack="false" IsImplicitlyDefined="true" Visible="false" />
          </ItemGroup>
        </Project>
        """;

    private static readonly object s_writeGate = new();

    /// <summary>
    /// Writes the targets file (once) and points <c>CustomBeforeMicrosoftCommonTargets</c> at it, unless the user's
    /// environment sets that already (theirs wins; old-style projects then need the Developer Pack).
    /// </summary>
    public static string? UseReferenceAssemblies() =>
        UseReferenceAssemblies(Path.Combine(Path.GetTempPath(), "codalus-buildhost"), Environment.GetEnvironmentVariable, Environment.SetEnvironmentVariable);

    internal static string? UseReferenceAssemblies(string directory, Func<string, string?> get, Action<string, string?> set)
    {
        if (!string.IsNullOrEmpty(get(CustomBeforeMicrosoftCommonTargets)))
        {
            return null;
        }

        var path = Path.Combine(directory, "Codalus.NetFramework.targets");
        lock (s_writeGate)
        {
            try
            {
                if (!File.Exists(path) || File.ReadAllText(path) != ReferenceAssembliesTargetsText)
                {
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(path, ReferenceAssembliesTargetsText);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null; // old-style projects then need the Developer Pack, as before
            }
        }

        set(CustomBeforeMicrosoftCommonTargets, path);
        return path;
    }

    /// <summary>Tells the targets file where the global packages folder is (the solution's NuGet settings decide).</summary>
    public static void SetPackageRoot(string folder) => Environment.SetEnvironmentVariable(PackageRootProperty, folder);

    /// <summary>
    /// The short name of a .NET Framework version as NuGet spells it: <c>v4.7.2</c> → <c>net472</c>, <c>v4.8</c> →
    /// <c>net48</c>, <c>v3.5</c> → <c>net35</c>, <c>v2.0</c> → <c>net20</c>. Null for anything else.
    /// </summary>
    public static string? ShortName(string? targetFrameworkVersion)
    {
        if (string.IsNullOrWhiteSpace(targetFrameworkVersion))
        {
            return null;
        }

        var text = targetFrameworkVersion.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(text.Contains('.', StringComparison.Ordinal) ? text : text + ".0", out var version) || version.Major is < 1 or > 4)
        {
            return null;
        }

        return "net" + version.Major.ToString(CultureInfo.InvariantCulture) + Math.Max(0, version.Minor).ToString(CultureInfo.InvariantCulture)
               + (version.Build > 0 ? version.Build.ToString(CultureInfo.InvariantCulture) : string.Empty);
    }

    /// <summary>The version folder of a moniker, as the reference assemblies lay it out: <c>net472</c> → <c>v4.7.2</c>, <c>net48</c> → <c>v4.8</c>.</summary>
    public static string VersionFolder(string moniker) => "v" + string.Join('.', moniker[3..].Select(c => c.ToString()));

    /// <summary>Whether a target framework moniker is .NET Framework's: <c>net48</c>, <c>net472</c>, <c>net35</c> (no dot, unlike <c>net8.0</c>).</summary>
    public static bool IsMoniker(string? targetFramework) =>
        targetFramework is { Length: >= 5 and <= 6 } tfm && tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase)
        && tfm[3..].All(char.IsAsciiDigit) && tfm[3] is >= '1' and <= '4';

    /// <summary>The reference assemblies package of a .NET Framework moniker: <c>net472</c> → <c>Microsoft.NETFramework.ReferenceAssemblies.net472</c>.</summary>
    public static string ReferenceAssembliesPackageOf(string moniker) => ReferenceAssembliesPackage + "." + moniker.ToLowerInvariant();

    /// <summary>Where the reference assemblies package of <paramref name="moniker"/> unpacks in the global packages folder (its <c>build/</c> folder).</summary>
    public static string ReferenceAssembliesFolder(string globalPackagesFolder, string moniker) =>
        Path.Combine(globalPackagesFolder, ReferenceAssembliesPackageOf(moniker).ToLowerInvariant(), ReferenceAssembliesVersion, "build");

    // ------------------------------------------------------------------ packages.config

    /// <summary>A package of a <c>packages.config</c> file.</summary>
    public sealed record PackagesConfigEntry(string Id, string Version, string? TargetFramework, bool DevelopmentDependency);

    /// <summary>The <c>packages.config</c> of a project: <c>packages.&lt;project&gt;.config</c> or <c>packages.config</c> next to it (NuGet's order), or null.</summary>
    public static string? PackagesConfigOf(string projectPath)
    {
        var directory = Path.GetDirectoryName(projectPath)!;
        var named = Path.Combine(directory, "packages." + Path.GetFileNameWithoutExtension(projectPath) + ".config");
        if (File.Exists(named))
        {
            return named;
        }

        var plain = Path.Combine(directory, "packages.config");
        return File.Exists(plain) ? plain : null;
    }

    /// <summary>Reads a <c>packages.config</c> file; entries without an id or version are left out.</summary>
    public static IReadOnlyList<PackagesConfigEntry> ReadPackagesConfig(string path)
    {
        try
        {
            return ParsePackagesConfig(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<PackagesConfigEntry> ParsePackagesConfig(string text)
    {
        try
        {
            var document = XDocument.Parse(text);
            return [.. (document.Root?.Elements("package") ?? [])
                .Select(p => new PackagesConfigEntry(
                    ((string?)p.Attribute("id"))?.Trim() ?? string.Empty,
                    ((string?)p.Attribute("version"))?.Trim() ?? string.Empty,
                    ((string?)p.Attribute("targetFramework"))?.Trim(),
                    string.Equals((string?)p.Attribute("developmentDependency"), "true", StringComparison.OrdinalIgnoreCase)))
                .Where(p => p.Id.Length > 0 && p.Version.Length > 0)];
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    /// <summary>
    /// The folder <c>packages.config</c> packages go to: NuGet's <c>repositoryPath</c> setting when there is one, else
    /// <c>packages</c> next to the solution (the project's folder for a project opened on its own).
    /// </summary>
    public static string RepositoryPath(string? configured, string solutionDirectory) =>
        string.IsNullOrWhiteSpace(configured) ? Path.Combine(solutionDirectory, "packages") : Path.GetFullPath(configured, solutionDirectory);

    /// <summary>Where a <c>packages.config</c> package unpacks in the repository path: <c>&lt;Id&gt;.&lt;Version&gt;</c>.</summary>
    public static string PackageFolder(string repositoryPath, PackagesConfigEntry package) =>
        Path.Combine(repositoryPath, package.Id + "." + package.Version);

    // ------------------------------------------------------------------ Visual Studio's targets

    /// <summary>
    /// Why an old-style project can't build here, from its imports, or null: an import (not <c>Condition="false"</c>)
    /// whose path, expanded, is under <c>Microsoft/VisualStudio</c> and doesn't exist (<c>$(VSToolsPath)</c>: web
    /// application, Service Fabric, SQL and other Visual Studio project types).
    /// </summary>
    public static string? VisualStudioTargetsReason(string projectName, IEnumerable<(string Expanded, string? Condition)> imports)
    {
        foreach (var (expanded, condition) in imports)
        {
            if (string.Equals(condition?.Trim(), "false", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var normalized = expanded.Replace('\\', '/');
            var at = normalized.IndexOf("/Microsoft/VisualStudio/", StringComparison.OrdinalIgnoreCase);
            if (at < 0 || File.Exists(expanded.Replace('\\', Path.DirectorySeparatorChar)))
            {
                continue;
            }

            var targets = normalized[(at + "/Microsoft/VisualStudio/".Length)..];
            var slash = targets.IndexOf('/', StringComparison.Ordinal);
            targets = slash >= 0 && targets.StartsWith('v') ? targets[(slash + 1)..] : targets; // v17.0/WebApplications/… → WebApplications/…
            return $"{projectName} imports Visual Studio's own MSBuild targets ({targets}), which come only with Visual Studio and its Build Tools. "
                   + "It opens and edits here, but doesn't build: Codalus IDE builds with the .NET SDK's MSBuild, and Visual Studio's is proprietary.";
        }

        return null;
    }
}
