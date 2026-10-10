using System.Reflection;
using System.Text.Json.Nodes;

namespace DaedalusStudio.Tests;

/// <summary>
/// Pins the repository's SDK (the <c>sdk</c> section of its <c>global.json</c>) above fixtures copied out of the
/// repository. Such a copy otherwise gets the newest SDK installed, a preview included. The regular fixtures stay on
/// the SDK the repository builds with, and samples/Net11 brings its own <c>global.json</c> for .NET 11 (M30).
/// </summary>
internal static class FixtureSdk
{
    private static readonly Lazy<string> s_globalJson = new(() =>
    {
        var repo = typeof(FixtureSdk).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "DaedalusRepoRoot").Value!;
        var sdk = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, "global.json")))!["sdk"]!.DeepClone();
        return new JsonObject { ["sdk"] = sdk }.ToJsonString() + "\n";
    });

    /// <summary>Writes the pin into <paramref name="root"/>, the folder above the copied fixtures.</summary>
    public static void Pin(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "global.json"), s_globalJson.Value);
    }
}
