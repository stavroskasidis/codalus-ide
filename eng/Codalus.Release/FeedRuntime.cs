using System.Text.Json;
using System.Text.Json.Nodes;

namespace Codalus.Release;

/// <summary>
/// The .NET runtime a release's hosts need, written into its Velopack feed before the feed is signed (M49, ADR 0080):
/// every entry of the release's version gets
/// <c>"CodalusFrameworks": [ { "Name": …, "Version": …, "RollForward": … } ]</c>, from the hosts' runtimeconfig.json
/// files in the published app (per framework, the highest version asked for). The IDE compares it with what's installed
/// before it downloads the release (<c>Codalus.Workbench.Updates.UpdateRuntime</c>); Velopack ignores the property.
/// </summary>
internal static class FeedRuntime
{
    public const string Property = "CodalusFrameworks";

    /// <summary>The hosts' folders that run on the installed .NET (as <c>IdeRuntime.HostFolders</c>).</summary>
    public static readonly string[] HostFolders = ["pluginhost", "buildhost", "lshost", "replhost", "markuphost", "datahost", "debughost", "t4host", "diaghost"];

    /// <summary>What the hosts under <paramref name="app"/> ask for: name, version, rollForward.</summary>
    public static IReadOnlyList<(string Name, string Version, string RollForward)> Frameworks(string app)
    {
        var found = new List<(string Name, string Version, string RollForward)>();
        foreach (var folder in HostFolders.Select(f => Path.Combine(app, f)).Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "codalus-*.runtimeconfig.json"))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (!document.RootElement.TryGetProperty("runtimeOptions", out var options))
                {
                    continue;
                }

                var rollForward = options.TryGetProperty("rollForward", out var roll) ? roll.GetString() ?? "Minor" : "Minor";
                var entries = new List<JsonElement>();
                if (options.TryGetProperty("framework", out var framework))
                {
                    entries.Add(framework);
                }

                if (options.TryGetProperty("frameworks", out var frameworks))
                {
                    entries.AddRange(frameworks.EnumerateArray());
                }

                foreach (var entry in entries)
                {
                    found.Add((entry.GetProperty("name").GetString()!, entry.GetProperty("version").GetString()!,
                        entry.TryGetProperty("rollForward", out var own) ? own.GetString() ?? rollForward : rollForward));
                }
            }
        }

        return [.. found.GroupBy(f => f.Name, StringComparer.Ordinal).Select(g => g.OrderByDescending(f => f.Version, Comparer<string>.Create(CompareVersions)).First())];
    }

    /// <summary>Adds <paramref name="frameworks"/> to the entries of <paramref name="version"/> in the feed at <paramref name="feedPath"/>; returns how many.</summary>
    public static int Annotate(string feedPath, string version, IReadOnlyList<(string Name, string Version, string RollForward)> frameworks)
    {
        var feed = JsonNode.Parse(File.ReadAllText(feedPath)) ?? throw new InvalidOperationException($"{feedPath} is empty.");
        var annotated = 0;
        foreach (var asset in feed["Assets"]?.AsArray() ?? throw new InvalidOperationException($"{feedPath} has no Assets."))
        {
            if (asset is JsonObject entry && entry["Version"]?.GetValue<string>() == version)
            {
                entry[Property] = new JsonArray([.. frameworks.Select(f => (JsonNode)new JsonObject { ["Name"] = f.Name, ["Version"] = f.Version, ["RollForward"] = f.RollForward })]);
                annotated++;
            }
        }

        File.WriteAllText(feedPath, feed.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return annotated;
    }

    /// <summary>SemVer precedence, enough to pick the highest of the hosts' versions.</summary>
    private static int CompareVersions(string a, string b)
    {
        static (Version Number, string[] Label) Split(string v)
        {
            var dash = v.IndexOf('-', StringComparison.Ordinal);
            var number = Version.TryParse(dash < 0 ? v : v[..dash], out var parsed) ? parsed : new Version(0, 0);
            return (number, dash < 0 ? [] : v[(dash + 1)..].Split('.'));
        }

        var (numberA, labelA) = Split(a);
        var (numberB, labelB) = Split(b);
        if (numberA.CompareTo(numberB) is var byNumber and not 0)
        {
            return byNumber;
        }

        if (labelA.Length == 0 || labelB.Length == 0)
        {
            return labelB.Length.CompareTo(labelA.Length); // a release is above its pre-releases
        }

        for (var i = 0; i < Math.Min(labelA.Length, labelB.Length); i++)
        {
            var part = long.TryParse(labelA[i], out var x) && long.TryParse(labelB[i], out var y) ? x.CompareTo(y) : string.CompareOrdinal(labelA[i], labelB[i]);
            if (part != 0)
            {
                return part;
            }
        }

        return labelA.Length.CompareTo(labelB.Length);
    }
}
