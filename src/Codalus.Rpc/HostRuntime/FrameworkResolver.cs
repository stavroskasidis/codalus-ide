using System.Globalization;

namespace Codalus.Rpc;

/// <summary>
/// A shared framework's version as the .NET host reads its folder names (<c>shared/Microsoft.NETCore.App/&lt;version&gt;</c>):
/// SemVer 2, <c>major.minor.patch[-prerelease][+build]</c>. Pre-release identifiers compare as SemVer orders them
/// (numbers as numbers, below words; a shorter list first), the build part not at all.
/// </summary>
public sealed class FrameworkVersion : IComparable<FrameworkVersion>, IEquatable<FrameworkVersion>
{
    private readonly string[] _prerelease;

    private FrameworkVersion(string text, int major, int minor, int patch, string[] prerelease)
    {
        Text = text;
        Major = major;
        Minor = minor;
        Patch = patch;
        _prerelease = prerelease;
    }

    public string Text { get; }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>The pre-release label without its dash (<c>rc.2.26504.105</c>), or empty for a release.</summary>
    public string Prerelease => string.Join('.', _prerelease);

    public bool IsPrerelease => _prerelease.Length > 0;

    public static FrameworkVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{text}' isn't a .NET version (major.minor.patch[-prerelease]).");

    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FrameworkVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var core = text;
        var plus = core.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            if (!ValidIdentifiers(core[(plus + 1)..]))
            {
                return false;
            }

            core = core[..plus];
        }

        string[] prerelease = [];
        var dash = core.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            var label = core[(dash + 1)..];
            if (!ValidIdentifiers(label))
            {
                return false;
            }

            prerelease = label.Split('.');
            core = core[..dash];
        }

        var parts = core.Split('.');
        if (parts.Length != 3 || !Number(parts[0], out var major) || !Number(parts[1], out var minor) || !Number(parts[2], out var patch))
        {
            return false;
        }

        version = new FrameworkVersion(text, major, minor, patch, prerelease);
        return true;
    }

    private static bool Number(string text, out int value)
    {
        value = 0;
        return text.Length > 0 && text.All(char.IsAsciiDigit) && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool ValidIdentifiers(string label) =>
        label.Length > 0 && label.Split('.').All(id => id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));

    public int CompareTo(FrameworkVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var byNumber = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (byNumber != 0)
        {
            return byNumber;
        }

        // A release is above its pre-releases.
        if (!IsPrerelease || !other.IsPrerelease)
        {
            return IsPrerelease == other.IsPrerelease ? 0 : IsPrerelease ? -1 : 1;
        }

        for (var i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
        {
            var a = _prerelease[i];
            var b = other._prerelease[i];
            var aNumeric = a.All(char.IsAsciiDigit);
            var bNumeric = b.All(char.IsAsciiDigit);
            var part = (aNumeric, bNumeric) switch
            {
                (true, true) => CompareNumeric(a, b),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a, b),
            };
            if (part != 0)
            {
                return part;
            }
        }

        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    private static int CompareNumeric(string a, string b)
    {
        a = a.TrimStart('0');
        b = b.TrimStart('0');
        return a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
    }

    public bool Equals(FrameworkVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is FrameworkVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    public override string ToString() => Text;

    public static bool operator ==(FrameworkVersion? a, FrameworkVersion? b) => a is null ? b is null : a.Equals(b);

    public static bool operator !=(FrameworkVersion? a, FrameworkVersion? b) => !(a == b);

    public static bool operator <(FrameworkVersion a, FrameworkVersion b) => a.CompareTo(b) < 0;

    public static bool operator >(FrameworkVersion a, FrameworkVersion b) => a.CompareTo(b) > 0;

    public static bool operator <=(FrameworkVersion a, FrameworkVersion b) => a.CompareTo(b) <= 0;

    public static bool operator >=(FrameworkVersion a, FrameworkVersion b) => a.CompareTo(b) >= 0;
}

/// <summary>A <c>runtimeconfig.json</c>'s <c>rollForward</c> (the .NET host's names, case-insensitive).</summary>
public enum RollForwardPolicy
{
    /// <summary>Exactly the requested version.</summary>
    Disable,

    /// <summary>The highest patch of the requested major.minor.</summary>
    LatestPatch,

    /// <summary>The default: the lowest minor at or above the requested one (its latest patch), within the major.</summary>
    Minor,

    /// <summary>The highest minor within the major.</summary>
    LatestMinor,

    /// <summary>The lowest version at or above the requested one, across majors (then its latest patch).</summary>
    Major,

    /// <summary>The highest version at all.</summary>
    LatestMajor,
}

/// <summary>
/// A framework an app asks for, with the settings that decide which installed version runs it: the version, the
/// roll-forward policy, and whether a release should be preferred to pre-releases (a release was asked for and
/// <c>DOTNET_ROLL_FORWARD_TO_PRERELEASE</c> isn't 1).
/// </summary>
public sealed record FrameworkReference(string Name, FrameworkVersion Version, RollForwardPolicy RollForward, bool PreferRelease)
{
    public const string NetCoreApp = "Microsoft.NETCore.App";

    /// <summary>A reference as the host reads it from a runtimeconfig.json.</summary>
    public static FrameworkReference Create(string name, FrameworkVersion version, RollForwardPolicy rollForward = RollForwardPolicy.Minor, bool rollForwardToPrerelease = false) =>
        new(name, version, rollForward, PreferRelease: !version.IsPrerelease && !rollForwardToPrerelease);

    /// <summary>"Microsoft.NETCore.App 11.0.0-rc.2.26504.105 (rollForward Major)".</summary>
    public override string ToString() => $"{Name} {Version} (rollForward {RollForward})";
}

/// <summary>
/// Which installed version of a framework the .NET host (hostfxr) picks for a reference, by its rules
/// (<c>fx_resolver.cpp</c> and <c>fx_reference.cpp</c> in dotnet/runtime, documented in its
/// <c>docs/design/features/framework-version-resolution.md</c>):
/// <list type="bullet">
/// <item><see cref="RollForwardPolicy.Disable"/>: the exact version only.</item>
/// <item>Otherwise the versions at or above the requested one that the policy allows (a different major only for Major
/// and LatestMajor, a different minor only from Minor on); the lowest of them, or the highest for LatestMinor and
/// LatestMajor; then, unless that is a pre-release, the highest patch of its major.minor.</item>
/// <item>A release reference looks at releases first, and at pre-releases only when no release fits; a pre-release
/// reference looks at both at once. So <c>11.0.0-rc.2</c> runs on <c>11.0.0-rc.2</c>, a later release candidate, or
/// 11.0.0 and later, but never on <c>11.0.0-rc.1</c>.</item>
/// </list>
/// </summary>
public static class FrameworkResolver
{
    private enum Range
    {
        Exact,
        Patch,
        Minor,
        Major,
    }

    /// <summary>The version the host would run <paramref name="reference"/> on, or null when none of <paramref name="installed"/> fits.</summary>
    public static FrameworkVersion? Resolve(FrameworkReference reference, IEnumerable<FrameworkVersion> installed)
    {
        var versions = installed.Distinct().ToList();
        if (reference.RollForward == RollForwardPolicy.Disable)
        {
            return versions.FirstOrDefault(v => v == reference.Version);
        }

        if (reference.PreferRelease && Search(versions, reference, releaseOnly: true) is { } release)
        {
            return release;
        }

        return Search(versions, reference, releaseOnly: false);
    }

    private static Range RangeOf(RollForwardPolicy policy) => policy switch
    {
        RollForwardPolicy.Disable => Range.Exact,
        RollForwardPolicy.LatestPatch => Range.Patch,
        RollForwardPolicy.Minor or RollForwardPolicy.LatestMinor => Range.Minor,
        _ => Range.Major,
    };

    private static FrameworkVersion? Search(List<FrameworkVersion> versions, FrameworkReference reference, bool releaseOnly)
    {
        var range = RangeOf(reference.RollForward);
        var toHighest = range != Range.Patch && reference.RollForward is RollForwardPolicy.LatestMinor or RollForwardPolicy.LatestMajor;
        FrameworkVersion? best = null;
        foreach (var version in versions)
        {
            if ((releaseOnly && version.IsPrerelease) || version < reference.Version || !Compatible(reference, range, version))
            {
                continue;
            }

            best = best is null ? version : toHighest ? Max(best, version) : Min(best, version);
        }

        if (best is null || best.IsPrerelease)
        {
            return best; // a pre-release match doesn't roll to its latest patch
        }

        // The automatic roll to the latest patch of the match's major.minor.
        var start = best;
        foreach (var version in versions)
        {
            if ((!releaseOnly || !version.IsPrerelease) && version >= start && version.Major == start.Major && version.Minor == start.Minor)
            {
                best = Max(best, version);
            }
        }

        return best;
    }

    private static bool Compatible(FrameworkReference reference, Range range, FrameworkVersion higher)
    {
        if (higher == reference.Version)
        {
            return true;
        }

        if (higher.Major != reference.Version.Major && range < Range.Major)
        {
            return false;
        }

        if (higher.Minor != reference.Version.Minor && range < Range.Minor)
        {
            return false;
        }

        return range != Range.Exact;
    }

    private static FrameworkVersion Max(FrameworkVersion a, FrameworkVersion b) => a >= b ? a : b;

    private static FrameworkVersion Min(FrameworkVersion a, FrameworkVersion b) => a <= b ? a : b;

    /// <summary>
    /// Versions for a message, oldest first, with runs of releases of one major.minor shortened:
    /// <c>8.0.20, 9.0.9, 10.0.0–10.0.9, 11.0.0-rc.1.26425.128</c>.
    /// </summary>
    public static string Describe(IEnumerable<FrameworkVersion> versions)
    {
        var parts = new List<string>();
        foreach (var group in versions.Distinct().Order().GroupBy(v => (v.Major, v.Minor, v.IsPrerelease)))
        {
            var list = group.ToList();
            if (group.Key.IsPrerelease || list.Count < 3)
            {
                parts.AddRange(list.Select(v => v.Text));
            }
            else
            {
                parts.Add($"{list[0].Text}–{list[^1].Text}");
            }
        }

        return string.Join(", ", parts);
    }
}
