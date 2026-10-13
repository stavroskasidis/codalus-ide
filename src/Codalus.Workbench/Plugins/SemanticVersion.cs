using System.Globalization;

namespace Codalus.Workbench.Plugins;

/// <summary>A SemVer 2.0 version (build metadata is ignored; a prerelease sorts before its release).</summary>
public sealed record SemanticVersion(int Major, int Minor, int Patch, string? Prerelease = null) : IComparable<SemanticVersion>
{
    public static SemanticVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a SemVer version.");

    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = new SemanticVersion(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var core = text.Trim().TrimStart('v');
        var plus = core.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            core = core[..plus];
        }

        string? prerelease = null;
        var dash = core.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            prerelease = core[(dash + 1)..];
            core = core[..dash];
        }

        var parts = core.Split('.');
        if (parts.Length != 3 || !parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }

        version = new SemanticVersion(
            int.Parse(parts[0], CultureInfo.InvariantCulture),
            int.Parse(parts[1], CultureInfo.InvariantCulture),
            int.Parse(parts[2], CultureInfo.InvariantCulture),
            prerelease);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var c = Major.CompareTo(other.Major);
        c = c != 0 ? c : Minor.CompareTo(other.Minor);
        c = c != 0 ? c : Patch.CompareTo(other.Patch);
        if (c != 0)
        {
            return c;
        }

        return (Prerelease, other.Prerelease) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => string.CompareOrdinal(Prerelease, other.Prerelease),
        };
    }

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}

/// <summary>
/// An npm-style version range: <c>*</c>, <c>1.2.3</c>, <c>1.2.x</c>, <c>^1.2.3</c>, <c>~1.2.3</c>, comparators
/// (<c>&gt;=</c>, <c>&gt;</c>, <c>&lt;</c>, <c>&lt;=</c>, <c>=</c>) joined by spaces (AND) and <c>||</c> (OR).
/// Caret on 0.x is narrow, as in npm: <c>^0.1.0</c> means <c>&gt;=0.1.0 &lt;0.2.0</c>.
/// </summary>
public sealed class VersionRange
{
    private readonly List<List<(string Op, SemanticVersion Version)>> _alternatives;

    private VersionRange(string text, List<List<(string, SemanticVersion)>> alternatives)
    {
        Text = text;
        _alternatives = alternatives;
    }

    public string Text { get; }

    public static VersionRange Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var alternatives = new List<List<(string, SemanticVersion)>>();
        foreach (var alternative in text.Split("||", StringSplitOptions.TrimEntries))
        {
            var comparators = new List<(string, SemanticVersion)>();
            foreach (var token in alternative.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                comparators.AddRange(Expand(token, text));
            }

            alternatives.Add(comparators);
        }

        return new VersionRange(text.Trim(), alternatives);
    }

    public static bool TryParse(string? text, out VersionRange? range, out string? error)
    {
        try
        {
            range = Parse(text ?? string.Empty);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            range = null;
            error = ex.Message;
            return false;
        }
    }

    public bool Satisfies(SemanticVersion version) =>
        _alternatives.Any(comparators => comparators.All(c => c.Op switch
        {
            ">=" => version >= c.Version,
            ">" => version > c.Version,
            "<" => version < c.Version,
            "<=" => version <= c.Version,
            _ => version.CompareTo(c.Version) == 0,
        }));

    public override string ToString() => Text;

    private static IEnumerable<(string, SemanticVersion)> Expand(string token, string range)
    {
        if (token is "*" or "x" or "X")
        {
            return [(">=", new SemanticVersion(0, 0, 0))];
        }

        foreach (var op in new[] { ">=", "<=", ">", "<", "=" })
        {
            if (token.StartsWith(op, StringComparison.Ordinal))
            {
                return [(op, Version(token[op.Length..], range))];
            }
        }

        if (token[0] is '^' or '~')
        {
            var v = Version(token[1..], range);
            var upper = token[0] == '~'
                ? new SemanticVersion(v.Major, v.Minor + 1, 0)
                : v.Major > 0 ? new SemanticVersion(v.Major + 1, 0, 0)
                : v.Minor > 0 ? new SemanticVersion(0, v.Minor + 1, 0)
                : new SemanticVersion(0, 0, v.Patch + 1);
            return [(">=", v), ("<", upper)];
        }

        // Wildcards: 1.x, 1.2.x
        var parts = token.Split('.');
        if (parts.Length < 3 || parts.Any(p => p is "x" or "X" or "*"))
        {
            var major = Number(parts[0], range);
            if (parts.Length == 1 || parts[1] is "x" or "X" or "*")
            {
                return [(">=", new SemanticVersion(major, 0, 0)), ("<", new SemanticVersion(major + 1, 0, 0))];
            }

            var minor = Number(parts[1], range);
            return [(">=", new SemanticVersion(major, minor, 0)), ("<", new SemanticVersion(major, minor + 1, 0))];
        }

        return [("=", Version(token, range))];
    }

    private static SemanticVersion Version(string text, string range) =>
        SemanticVersion.TryParse(text, out var v) ? v : throw new FormatException($"Invalid version '{text}' in range '{range}'.");

    private static int Number(string text, string range) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new FormatException($"Invalid range '{range}'.");
}
