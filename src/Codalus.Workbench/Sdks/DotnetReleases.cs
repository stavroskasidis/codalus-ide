using System.Globalization;
using System.Text.Json;
using Codalus.Workbench.Setup;

namespace Codalus.Workbench.Sdks;

/// <summary>
/// A .NET channel (<c>10.0</c>) as Microsoft's <c>releases-index.json</c> describes it (ADR 0112): its newest release,
/// runtime and SDK, its support phase (<c>preview</c>, <c>go-live</c>, <c>active</c>, <c>maintenance</c>, <c>eol</c>),
/// LTS or STS, and its end of support.
/// </summary>
public sealed record DotnetChannel(string Version, string SupportPhase)
{
    public string? LatestRelease { get; init; }

    public string? LatestRuntime { get; init; }

    public string? LatestSdk { get; init; }

    /// <summary><c>lts</c> or <c>sts</c>.</summary>
    public string? ReleaseType { get; init; }

    public DateOnly? EndOfLife { get; init; }

    /// <summary>Whether the newest release fixes security issues.</summary>
    public bool Security { get; init; }

    /// <summary>A preview or a release candidate (<c>go-live</c>): shown only with Show previews.</summary>
    public bool IsPreview => SupportPhase is "preview" or "go-live";

    /// <summary>Out of support on <paramref name="today"/>: its phase says so, or its end of support has passed.</summary>
    public bool IsOutOfSupport(DateOnly today) => SupportPhase == "eol" || EndOfLife is { } end && end < today;

    /// <summary>The channel's major and minor numbers, for ordering (<c>10.0</c> after <c>9.0</c>).</summary>
    public Version Number => System.Version.TryParse(Version, out var number) ? number : new Version(0, 0);
}

/// <summary>
/// One release of a channel, from its <c>releases.json</c> (ADR 0112): the runtime, ASP.NET Core and Windows Desktop
/// versions it brings, its SDKs (one per feature band), and whether it fixes security issues.
/// </summary>
public sealed record DotnetRelease(string Version)
{
    public DateOnly? Date { get; init; }

    public bool Security { get; init; }

    /// <summary><c>Microsoft.NETCore.App</c>'s version.</summary>
    public string? Runtime { get; init; }

    /// <summary><c>Microsoft.AspNetCore.App</c>'s version.</summary>
    public string? AspNetCore { get; init; }

    /// <summary><c>Microsoft.WindowsDesktop.App</c>'s version.</summary>
    public string? WindowsDesktop { get; init; }

    /// <summary>The SDKs of this release, one per feature band (<c>10.0.104</c>, <c>10.0.400</c>).</summary>
    public IReadOnlyList<string> Sdks { get; init; } = [];

    /// <summary>The version this release brings of <paramref name="framework"/>, or null.</summary>
    public string? VersionOf(string framework) => framework switch
    {
        DotnetFrameworks.NetCore => Runtime,
        DotnetFrameworks.AspNetCore => AspNetCore,
        DotnetFrameworks.WindowsDesktop => WindowsDesktop,
        _ => null,
    };
}

/// <summary>A channel's releases, newest first, as its <c>releases.json</c> lists them.</summary>
public sealed record DotnetChannelReleases(string Channel, IReadOnlyList<DotnetRelease> Releases)
{
    /// <summary>The release that brought the SDK <paramref name="sdk"/>, or null (a daily build isn't in the file).</summary>
    public DotnetRelease? ReleaseOfSdk(string sdk) => Releases.FirstOrDefault(r => r.Sdks.Contains(sdk, StringComparer.OrdinalIgnoreCase));

    /// <summary>The newest release whose <paramref name="framework"/> is <paramref name="version"/>, or null.</summary>
    public DotnetRelease? ReleaseOfRuntime(string framework, string version) =>
        Releases.FirstOrDefault(r => string.Equals(r.VersionOf(framework), version, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every SDK of the channel, in any release.</summary>
    public IEnumerable<string> AllSdks => Releases.SelectMany(r => r.Sdks);
}

/// <summary>The shared frameworks the manager knows: what a runtime-only install brings, and what an SDK brings.</summary>
public static class DotnetFrameworks
{
    public const string NetCore = "Microsoft.NETCore.App";
    public const string AspNetCore = "Microsoft.AspNetCore.App";
    public const string WindowsDesktop = "Microsoft.WindowsDesktop.App";

    public static readonly string[] All = [NetCore, AspNetCore, WindowsDesktop];

    /// <summary>The name a user reads: <c>.NET Runtime</c>, <c>ASP.NET Core Runtime</c>, <c>Windows Desktop Runtime</c>.</summary>
    public static string Title(string framework) => framework switch
    {
        NetCore => ".NET Runtime",
        AspNetCore => "ASP.NET Core Runtime",
        WindowsDesktop => "Windows Desktop Runtime",
        _ => framework,
    };

    /// <summary><c>dotnet-install</c>'s <c>--runtime</c> value for a framework.</summary>
    public static string InstallScriptName(string framework) => framework switch
    {
        AspNetCore => "aspnetcore",
        WindowsDesktop => "windowsdesktop",
        _ => "dotnet",
    };
}

/// <summary>Reads Microsoft's release metadata (ADR 0112). Unknown properties are ignored; a missing one is null.</summary>
public static class DotnetReleaseParser
{
    private static readonly JsonDocumentOptions s_options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>The channels of <c>releases-index.json</c>, newest first.</summary>
    public static IReadOnlyList<DotnetChannel> ParseIndex(string json)
    {
        using var document = JsonDocument.Parse(json, s_options);
        if (!document.RootElement.TryGetProperty("releases-index", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("releases-index.json has no \"releases-index\" list.");
        }

        var channels = new List<DotnetChannel>();
        foreach (var item in list.EnumerateArray())
        {
            if (Text(item, "channel-version") is not { } version)
            {
                continue;
            }

            channels.Add(new DotnetChannel(version, Text(item, "support-phase") ?? "active")
            {
                LatestRelease = Text(item, "latest-release"),
                LatestRuntime = Text(item, "latest-runtime"),
                LatestSdk = Text(item, "latest-sdk"),
                ReleaseType = Text(item, "release-type"),
                EndOfLife = Date(item, "eol-date"),
                Security = item.TryGetProperty("security", out var security) && security.ValueKind == JsonValueKind.True,
            });
        }

        return [.. channels.OrderByDescending(c => c.Number)];
    }

    /// <summary>A channel's <c>releases.json</c>: its releases, newest first.</summary>
    public static DotnetChannelReleases ParseChannel(string json)
    {
        using var document = JsonDocument.Parse(json, s_options);
        var root = document.RootElement;
        var channel = Text(root, "channel-version") ?? string.Empty;
        var releases = new List<DotnetRelease>();
        if (root.TryGetProperty("releases", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (Text(item, "release-version") is not { } version)
                {
                    continue;
                }

                var sdks = new List<string>();
                if (item.TryGetProperty("sdks", out var all) && all.ValueKind == JsonValueKind.Array)
                {
                    sdks.AddRange(all.EnumerateArray().Select(s => Text(s, "version")).OfType<string>());
                }

                if (item.TryGetProperty("sdk", out var sdk) && Text(sdk, "version") is { } single && !sdks.Contains(single, StringComparer.OrdinalIgnoreCase))
                {
                    sdks.Add(single);
                }

                releases.Add(new DotnetRelease(version)
                {
                    Date = Date(item, "release-date"),
                    Security = item.TryGetProperty("security", out var security) && security.ValueKind == JsonValueKind.True,
                    Runtime = Nested(item, "runtime"),
                    AspNetCore = Nested(item, "aspnetcore-runtime"),
                    WindowsDesktop = Nested(item, "windowsdesktop"),
                    Sdks = [.. sdks.Distinct(StringComparer.OrdinalIgnoreCase)],
                });
            }
        }

        return new DotnetChannelReleases(channel, [.. releases.OrderByDescending(r => r.Version, Comparer<string>.Create(EnvironmentCheck.CompareVersions))]);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static string? Nested(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? Text(value, "version") : null;

    private static DateOnly? Date(JsonElement element, string name) =>
        Text(element, name) is { } text && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}
