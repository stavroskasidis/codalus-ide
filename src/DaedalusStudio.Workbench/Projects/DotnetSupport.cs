using System.Globalization;

namespace DaedalusStudio.Workbench.Projects;

/// <summary>
/// The support lifecycle of .NET versions, for the SDK status (M9, M30): the end-of-support date of each channel,
/// pinned from Microsoft's release index (<c>releases-index.json</c>). Each new .NET release adds its row
/// (<c>docs/release/dotnet-upgrade.md</c>).
/// </summary>
public static class DotnetSupport
{
    /// <summary>Channel (<c>major.minor</c>) → whether it's LTS, and the last day of support (null: not announced yet).</summary>
    private static readonly Dictionary<string, (bool Lts, DateOnly? EndOfSupport)> s_channels = new(StringComparer.Ordinal)
    {
        ["11.0"] = (false, null),
        ["10.0"] = (true, new DateOnly(2028, 11, 14)),
        ["9.0"] = (false, new DateOnly(2026, 11, 10)),
        ["8.0"] = (true, new DateOnly(2026, 11, 10)),
        ["7.0"] = (false, new DateOnly(2024, 5, 14)),
        ["6.0"] = (true, new DateOnly(2024, 11, 12)),
        ["5.0"] = (false, new DateOnly(2022, 5, 10)),
        ["3.1"] = (true, new DateOnly(2022, 12, 13)),
        ["3.0"] = (false, new DateOnly(2020, 3, 3)),
        ["2.2"] = (false, new DateOnly(2019, 12, 23)),
        ["2.1"] = (true, new DateOnly(2021, 8, 21)),
        ["2.0"] = (false, new DateOnly(2018, 10, 1)),
        ["1.1"] = (true, new DateOnly(2019, 6, 27)),
        ["1.0"] = (true, new DateOnly(2019, 6, 27)),
    };

    /// <summary>
    /// The support status of an SDK version (<c>8.0.425</c>, <c>11.0.100-rc.1.26425.128</c>) on <paramref name="today"/>:
    /// a short text, and whether it is out of support.
    /// </summary>
    public static (string Text, bool OutOfSupport) Status(string sdkVersion, DateOnly today)
    {
        var parts = sdkVersion.Split('-', 2)[0].Split('.');
        var channel = parts.Length >= 2 ? parts[0] + "." + parts[1] : sdkVersion;
        var preview = sdkVersion.Contains('-', StringComparison.Ordinal);
        if (!s_channels.TryGetValue(channel, out var info))
        {
            return (preview ? $"Preview of .NET {channel}" : $".NET {channel}", false);
        }

        var kind = info.Lts ? "LTS" : "STS";
        if (preview)
        {
            return ($"Preview of .NET {channel} ({kind})", false);
        }

        if (info.EndOfSupport is not { } end)
        {
            return ($".NET {channel} ({kind})", false);
        }

        var date = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return end < today
            ? ($"Out of support since {date} (.NET {channel})", true)
            : ($"Supported until {date} (.NET {channel} {kind})", false);
    }
}
