using System.Globalization;
using Codalus.Projects.Protocol;

namespace Codalus.Workbench.Projects;

/// <summary>
/// .NET Framework projects (M43a, ADR 0075, D31): SDK-style <c>net4x</c> projects and non-SDK-style ones open, edit
/// and build on every OS; they run (and, from M43b, debug and test) only on Windows, on the desktop CLR: their
/// <c>.exe</c> starts directly, not through <c>dotnet</c>. Running them on Mono is a later decision.
/// </summary>
public static class NetFrameworkProjects
{
    /// <summary>A .NET Framework moniker: <c>net48</c>, <c>net472</c>, <c>net35</c> (no dot, unlike <c>net8.0</c>).</summary>
    public static bool IsMoniker(string? targetFramework) =>
        targetFramework is { Length: >= 5 and <= 6 } tfm && tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase)
        && tfm[3..].All(char.IsAsciiDigit) && tfm[3] is >= '1' and <= '4';

    /// <summary>The version of a .NET Framework moniker: <c>net472</c> → 4.7.2, <c>net48</c> → 4.8; null for others.</summary>
    public static Version? VersionOf(string? targetFramework)
    {
        if (!IsMoniker(targetFramework))
        {
            return null;
        }

        var digits = targetFramework![3..].Select(c => c - '0').ToArray();
        return digits.Length == 2 ? new Version(digits[0], digits[1]) : new Version(digits[0], digits[1], digits[2]);
    }

    /// <summary>".NET Framework 4.8" for <c>net48</c>; the moniker itself for others.</summary>
    public static string Display(string targetFramework) =>
        VersionOf(targetFramework) is { } version ? ".NET Framework " + version.ToString() : targetFramework;

    /// <summary>Whether a project is a .NET Framework one: non-SDK-style, or every target framework is .NET Framework's.</summary>
    public static bool IsNetFramework(ProjectInfo project) =>
        project.IsNonSdkStyle || (project.TargetFrameworks.Length > 0 && project.TargetFrameworks.All(IsMoniker));

    /// <summary>The highest .NET Framework version a project targets (for the environment check), or null.</summary>
    public static Version? HighestVersion(ProjectInfo project) =>
        project.TargetFrameworks.Select(VersionOf).OfType<Version>().DefaultIfEmpty().Max();

    // ------------------------------------------------------------------ the installed runtime (Windows)

    /// <summary>
    /// The registry key whose <c>Release</c> DWORD says which .NET Framework 4.x is installed (Microsoft's documented
    /// way: <c>HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full</c>).
    /// </summary>
    public const string RuntimeKey = @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full";

    /// <summary>The key of .NET Framework 3.5 (and 2.0, 3.0), whose <c>Install</c> DWORD is 1 when it's installed.</summary>
    public const string Runtime35Key = @"SOFTWARE\Microsoft\NET Framework Setup\NDP\v3.5";

    public const string RuntimeToolId = "netfx-runtime";

    public const string DownloadPage = "https://dotnet.microsoft.com/download/dotnet-framework";

    /// <summary>The minimum <c>Release</c> value of each version (Microsoft's table), newest first.</summary>
    private static readonly (int Release, Version Version)[] s_releases =
    [
        (533320, new Version(4, 8, 1)),
        (528040, new Version(4, 8)),
        (461808, new Version(4, 7, 2)),
        (461308, new Version(4, 7, 1)),
        (460798, new Version(4, 7)),
        (394802, new Version(4, 6, 2)),
        (394254, new Version(4, 6, 1)),
        (393295, new Version(4, 6)),
        (379893, new Version(4, 5, 2)),
        (378675, new Version(4, 5, 1)),
        (378389, new Version(4, 5)),
    ];

    /// <summary>The .NET Framework version a <c>Release</c> value means (4.5 to 4.8.1), or null below 4.5 or without one.</summary>
    public static Version? VersionFromRelease(int? release) =>
        release is { } value ? s_releases.FirstOrDefault(r => value >= r.Release).Version : null;

    /// <summary>
    /// Check Environment's row for the solution's .NET Framework projects (on Windows): the installed .NET Framework 4.x
    /// against the highest version they target, and .NET Framework 3.5 when a project targets 2.0 to 3.5. Building needs
    /// neither (the reference assemblies packages); running does.
    /// </summary>
    public static IReadOnlyList<Setup.ToolStatus> ToolRows(IReadOnlyList<ProjectInfo> projects, int? release, bool? has35)
    {
        var targeted = projects.Where(IsNetFramework).Select(HighestVersion).OfType<Version>().ToList();
        if (targeted.Count == 0)
        {
            return [];
        }

        var rows = new List<Setup.ToolStatus>();
        var installed = VersionFromRelease(release);
        var modern = targeted.Where(v => v.Major == 4).DefaultIfEmpty().Max();
        if (modern is not null)
        {
            var enough = installed is not null && installed >= modern;
            rows.Add(new Setup.ToolStatus(RuntimeToolId, ".NET Framework", false, enough ? Setup.ToolState.Found : installed is null ? Setup.ToolState.Missing : Setup.ToolState.Mismatch,
                "Running the solution's .NET Framework apps (building needs only the reference assemblies packages)")
            {
                Version = installed?.ToString(),
                WantedVersion = modern.ToString(),
                Detail = enough ? null
                    : installed is null ? $"No .NET Framework 4.5 or later was found (the registry's {RuntimeKey}). The solution targets {modern}."
                    : $"The solution targets .NET Framework {modern}; {installed} is installed. Apps of newer versions fail to start.",
            });
        }

        if (targeted.Any(v => v.Major < 4))
        {
            rows.Add(new Setup.ToolStatus(RuntimeToolId + ":3.5", ".NET Framework 3.5", false, has35 == true ? Setup.ToolState.Found : Setup.ToolState.Missing,
                "Running the solution's apps for .NET Framework 2.0 to 3.5")
            {
                Version = has35 == true ? "3.5" : null,
                Detail = has35 == true ? null : "Turn it on in Windows Features (\".NET Framework 3.5 (includes .NET 2.0 and 3.0)\").",
            });
        }

        return rows;
    }

    /// <summary>The install plan of one of <see cref="ToolRows"/>' rows: Microsoft's download page.</summary>
    public static Setup.InstallPlan? PlanFor(Setup.ToolStatus tool) =>
        tool.Id.StartsWith(RuntimeToolId, StringComparison.Ordinal)
            ? new Setup.InstallPlan(tool.Id, Setup.InstallKind.OpenPage, ".NET Framework's download page", DownloadPage)
            : null;

    /// <summary>The <c>Release</c> value read from the registry (a DWORD), or null.</summary>
    public static int? Release(object? value) => value switch
    {
        int number => number,
        long number => (int)Math.Min(number, int.MaxValue),
        string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };
}
