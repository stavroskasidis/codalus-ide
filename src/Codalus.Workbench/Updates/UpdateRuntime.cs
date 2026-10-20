using System.Text.Json;
using Codalus.Rpc;

namespace Codalus.Workbench.Updates;

/// <summary>
/// The .NET runtime a release's hosts need, as its signed update feed says (M49, ADR 0080). <c>eng/package.sh</c> adds
/// it to the release's entries in <c>releases.&lt;channel&gt;.json</c> before the feed is signed, from the hosts'
/// <c>runtimeconfig.json</c> files:
/// <code>"CodalusFrameworks": [ { "Name": "Microsoft.NETCore.App", "Version": "11.0.0-rc.2.26504.105", "RollForward": "Major" } ]</code>
/// The IDE compares it with what's installed before it downloads the release, so an update can't leave it unable to
/// start its hosts. Velopack ignores the extra property. A feed without it (releases before M49) asks for nothing.
/// </summary>
public static class UpdateRuntime
{
    public const string FeedProperty = "CodalusFrameworks";

    /// <summary>
    /// The frameworks the full package of <paramref name="version"/> in <paramref name="feedJson"/> asks for, with the
    /// settings the .NET host will apply in <paramref name="environment"/> (<c>DOTNET_ROLL_FORWARD</c> and
    /// <c>DOTNET_ROLL_FORWARD_TO_PRERELEASE</c> carry over). Empty when the feed doesn't say.
    /// </summary>
    public static IReadOnlyList<FrameworkReference> Requirements(string? feedJson, string version, Func<string, string?> environment)
    {
        if (string.IsNullOrEmpty(feedJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(feedJson);
            if (!document.RootElement.TryGetProperty("Assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var asset = assets.EnumerateArray().FirstOrDefault(a =>
                a.TryGetProperty("Type", out var type) && type.GetString() == "Full"
                && a.TryGetProperty("Version", out var v) && v.GetString() is { } text && ReleaseVersions.Compare(text, version) == 0
                && a.TryGetProperty(FeedProperty, out _));
            if (asset.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            RollForwardPolicy? forced = RuntimeConfig.TryParsePolicy(environment(RuntimeConfig.RollForwardVariable), out var fromEnvironment) ? fromEnvironment : null;
            var toPrerelease = environment(RuntimeConfig.RollForwardToPrereleaseVariable) is { } flag && int.TryParse(flag.Trim(), out var value) && value == 1;
            var references = new List<FrameworkReference>();
            foreach (var framework in asset.GetProperty(FeedProperty).EnumerateArray())
            {
                if (framework.TryGetProperty("Name", out var name) && name.GetString() is { Length: > 0 } fxName
                    && framework.TryGetProperty("Version", out var fxVersion) && FrameworkVersion.TryParse(fxVersion.GetString(), out var parsed))
                {
                    var policy = framework.TryGetProperty("RollForward", out var roll) && RuntimeConfig.TryParsePolicy(roll.GetString(), out var own) ? own : RollForwardPolicy.Minor;
                    references.Add(FrameworkReference.Create(fxName, parsed, forced ?? policy, toPrerelease));
                }
            }

            return references;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return [];
        }
    }
}
