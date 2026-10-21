using System.Globalization;
using Codalus.Workbench.Localization;

namespace Codalus.Workbench.Projects;

/// <summary>
/// The support lifecycle of .NET versions, for the SDK status (M9, M30) and New Project's framework labels (ADR 0082):
/// the end-of-support date of each channel, pinned from Microsoft's release index (<c>releases-index.json</c>). Each new
/// .NET release adds its row (<c>docs/release/dotnet-upgrade.md</c>).
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

    /// <summary>
    /// A target framework as New Project lists it (ADR 0082): <c>net10.0</c> → ".NET 10.0 (Long Term Support)", a
    /// Standard Term Support or out-of-support channel likewise, a channel whose only templates are prerelease
    /// (<paramref name="preview"/>) → "(Preview)"; <c>netstandard2.1</c> → ".NET Standard 2.1", <c>net48</c> → ".NET
    /// Framework 4.8". A channel the table doesn't know yet has no support label.
    /// </summary>
    public static string FrameworkLabel(string moniker, bool preview, DateOnly today)
    {
        var (kind, version) = FrameworkOrder.Key(moniker);
        if (kind == 1)
        {
            return Localizer.Format("templates.framework.netStandard", ".NET Standard {0}", version.ToString(2));
        }

        if (kind == 2)
        {
            return Localizer.Format("templates.framework.netFramework", ".NET Framework {0}", version.Build > 0 ? version.ToString(3) : version.ToString(2));
        }

        if (kind != 0)
        {
            return moniker;
        }

        var channel = version.ToString(2);
        var name = moniker.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase)
            ? Localizer.Format("templates.framework.netCore", ".NET Core {0}", channel)
            : Localizer.Format("templates.framework.dotnet", ".NET {0}", channel);
        if (preview)
        {
            return Localizer.Format("templates.framework.preview", "{0} (Preview)", name);
        }

        if (!s_channels.TryGetValue(channel, out var info))
        {
            return name;
        }

        if (info.EndOfSupport is { } end && end < today)
        {
            return Localizer.Format("templates.framework.outOfSupport", "{0} (Out of support)", name);
        }

        return info.Lts
            ? Localizer.Format("templates.framework.lts", "{0} (Long Term Support)", name)
            : Localizer.Format("templates.framework.sts", "{0} (Standard Term Support)", name);
    }
}
