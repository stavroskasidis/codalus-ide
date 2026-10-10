using System.Globalization;
using System.Text.RegularExpressions;

namespace DaedalusStudio.Release;

/// <summary>What a build is released as (D21).</summary>
internal enum ReleaseKind
{
    /// <summary>The <c>VERSION</c> as it is, from <c>master</c>.</summary>
    Stable,

    /// <summary><c>X.Y.Z-nightly.YYYYMMDD.N</c> from <c>develop</c>: sorts before the stable <c>X.Y.Z</c>.</summary>
    Nightly,

    /// <summary><c>X.Y.Z-dev</c>: local builds, which never update themselves (M32).</summary>
    Dev,
}

/// <summary>The version of the next release, as the one-line <c>VERSION</c> file at the repository root holds it.</summary>
internal sealed partial record ReleaseVersion(int Major, int Minor, int Patch)
{
    public const string FileName = "VERSION";

    public static ReleaseVersion Parse(string text)
    {
        var match = CoreVersion().Match(text.Trim());
        if (!match.Success)
        {
            throw new FormatException($"'{text.Trim()}' is not a release version (MAJOR.MINOR.PATCH).");
        }

        return new ReleaseVersion(Number(match, 1), Number(match, 2), Number(match, 3));

        static int Number(Match match, int group) => int.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
    }

    public static ReleaseVersion Read(string file) => Parse(File.ReadAllText(file));

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    /// <summary>The version a build of this kind is stamped with.</summary>
    public string For(ReleaseKind kind, DateOnly date = default, int run = 0) => kind switch
    {
        ReleaseKind.Stable => ToString(),
        ReleaseKind.Nightly when run > 0 => string.Create(CultureInfo.InvariantCulture, $"{this}-nightly.{date:yyyyMMdd}.{run}"),
        ReleaseKind.Nightly => throw new ArgumentOutOfRangeException(nameof(run), "A nightly needs the run number."),
        ReleaseKind.Dev => $"{this}-dev",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>What <c>develop</c> builds after a stable release: the next minor version (D21).</summary>
    public ReleaseVersion NextMinor() => new(Major, Minor + 1, 0);

    public static string Tag(string version) => "v" + version;

    /// <summary>A stable release's tag: <c>vX.Y.Z</c> and nothing after it.</summary>
    public static bool IsStableTag(string tag) => StableTag().IsMatch(tag);

    /// <summary>A nightly's tag: <c>vX.Y.Z-nightly.YYYYMMDD.N</c>.</summary>
    public static bool IsNightlyTag(string tag) => NightlyTag().IsMatch(tag);

    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")]
    private static partial Regex CoreVersion();

    [GeneratedRegex(@"^v\d+\.\d+\.\d+$")]
    private static partial Regex StableTag();

    [GeneratedRegex(@"^v\d+\.\d+\.\d+-nightly\.\d{8}\.\d+$")]
    private static partial Regex NightlyTag();
}
