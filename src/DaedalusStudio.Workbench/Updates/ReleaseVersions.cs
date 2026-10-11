using System.Globalization;

namespace DaedalusStudio.Workbench.Updates;

/// <summary>SemVer 2.0 precedence for release versions (<c>0.2.0-nightly.20261011.1</c> &lt; <c>0.2.0</c>); build metadata is ignored.</summary>
public static class ReleaseVersions
{
    public static int Compare(string a, string b)
    {
        var (coreA, preA) = Split(a);
        var (coreB, preB) = Split(b);
        for (var i = 0; i < 3; i++)
        {
            var result = Part(coreA, i).CompareTo(Part(coreB, i));
            if (result != 0)
            {
                return result;
            }
        }

        // A release is higher than its prereleases.
        if (preA.Length == 0 || preB.Length == 0)
        {
            return (preA.Length == 0, preB.Length == 0) switch
            {
                (true, true) => 0,
                (true, false) => 1,
                _ => -1,
            };
        }

        for (var i = 0; i < Math.Min(preA.Length, preB.Length); i++)
        {
            var numericA = long.TryParse(preA[i], NumberStyles.None, CultureInfo.InvariantCulture, out var x);
            var numericB = long.TryParse(preB[i], NumberStyles.None, CultureInfo.InvariantCulture, out var y);
            var result = (numericA, numericB) switch
            {
                (true, true) => x.CompareTo(y),
                (true, false) => -1, // numeric identifiers sort before alphanumeric ones
                (false, true) => 1,
                _ => string.CompareOrdinal(preA[i], preB[i]),
            };
            if (result != 0)
            {
                return result;
            }
        }

        return preA.Length.CompareTo(preB.Length);
    }

    public static bool IsNewer(string candidate, string current) => Compare(candidate, current) > 0;

    private static (string[] Core, string[] Prerelease) Split(string version)
    {
        var withoutBuild = version.Split('+', 2)[0];
        var parts = withoutBuild.Split('-', 2);
        return (parts[0].Split('.'), parts.Length > 1 ? parts[1].Split('.') : []);
    }

    private static long Part(string[] core, int index) =>
        index < core.Length && long.TryParse(core[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
