using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace Codalus.Workbench.Testing;

/// <summary>A named set of test items (by key), saved per solution under <c>.codalus/playlists/</c> (ADR 0010, F7).</summary>
public sealed record Playlist(string Name, IReadOnlyList<string> Tests);

/// <summary>Reads and writes playlists, and imports Visual Studio <c>.playlist</c> files.</summary>
public static class Playlists
{
    /// <summary>Stands for the solution folder in saved keys, so playlists work in another clone.</summary>
    public const string SolutionDirToken = "$(SolutionDir)";

    public static string Folder(string solutionPath) => Path.Combine(Path.GetDirectoryName(solutionPath)!, ".codalus", "playlists");

    public static IReadOnlyList<Playlist> Load(string solutionPath)
    {
        var folder = Folder(solutionPath);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var playlists = new List<Playlist>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var saved = JsonSerializer.Deserialize(File.ReadAllText(file), PlaylistJsonContext.Default.SavedPlaylist);
                if (saved is not null)
                {
                    playlists.Add(new Playlist(saved.Name ?? Path.GetFileNameWithoutExtension(file), [.. saved.Tests.Select(t => Expand(t, solutionPath))]));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A broken file is skipped; the others still load.
            }
        }

        return playlists;
    }

    public static async Task SaveAsync(string solutionPath, Playlist playlist, CancellationToken cancellationToken)
    {
        var folder = Folder(solutionPath);
        Directory.CreateDirectory(folder);
        var saved = new SavedPlaylist { Name = playlist.Name, Tests = [.. playlist.Tests.Select(t => Contract(t, solutionPath))] };
        var json = JsonSerializer.Serialize(saved, PlaylistJsonContext.Default.SavedPlaylist);
        await File.WriteAllTextAsync(Path.Combine(folder, FileName(playlist.Name)), json, cancellationToken).ConfigureAwait(false);
    }

    public static void Delete(string solutionPath, string name)
    {
        var file = Path.Combine(Folder(solutionPath), FileName(name));
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    // Playlists travel with the solution (and through git), so the file name is the same on every OS: Windows' set of
    // invalid characters, not Path.GetInvalidFileNameChars(), which on Linux and macOS lets ':' and '?' through.
    private static string FileName(string name) =>
        string.Concat(name.Select(c => char.IsControl(c) || "\"<>|:*?\\/".Contains(c) ? '_' : c)) + ".json";

    private static string Contract(string key, string solutionPath)
    {
        var dir = Path.GetDirectoryName(solutionPath)!;
        return key.Replace(dir, SolutionDirToken, StringComparison.OrdinalIgnoreCase);
    }

    private static string Expand(string key, string solutionPath) =>
        key.Replace(SolutionDirToken, Path.GetDirectoryName(solutionPath)!, StringComparison.Ordinal);

    /// <summary>
    /// The tests a Visual Studio playlist selects: version 1.0 (<c>&lt;Add Test="Ns.Class.Method" /&gt;</c>) and 2.0
    /// (rules on Project, Namespace, Class and TestWithNormalizedFullyQualifiedName). Returns the matching node keys.
    /// </summary>
    public static IReadOnlyList<string> ImportVisualStudio(XDocument document, IReadOnlyList<TestNode> roots)
    {
        var leaves = roots.SelectMany(r => r.Leaves()).ToList();
        var keys = new List<string>();
        var root = document.Root ?? throw new FormatException("The playlist is empty.");
        foreach (var add in root.Descendants("Add"))
        {
            if ((string?)add.Attribute("Test") is { } test)
            {
                keys.AddRange(leaves.Where(l => TestNames.Matches(l, test, null)).Select(l => l.Key));
            }
        }

        // Version 2.0: the innermost rules hold the most specific properties.
        foreach (var rule in root.Descendants("Rule").Where(r => !r.Elements("Rule").Any()))
        {
            var properties = rule.AncestorsAndSelf("Rule").SelectMany(r => r.Elements("Property"))
                .Where(p => p.Attribute("Value") is not null)
                .ToDictionary(p => (string)p.Attribute("Name")!, p => (string)p.Attribute("Value")!, StringComparer.OrdinalIgnoreCase);
            if (properties.Count == 0)
            {
                continue;
            }

            keys.AddRange(leaves.Where(leaf =>
                (!properties.TryGetValue("Project", out var project) || string.Equals(leaf.Root.Label, project, StringComparison.OrdinalIgnoreCase))
                && (!properties.TryGetValue("Namespace", out var ns) || (TestNames.ClassName(leaf)?.StartsWith(ns + ".", StringComparison.Ordinal) ?? false))
                && (!properties.TryGetValue("Class", out var cls) || (TestNames.ClassName(leaf) is { } c && (c == cls || c.EndsWith("." + cls, StringComparison.Ordinal))))
                && (!properties.TryGetValue("TestWithNormalizedFullyQualifiedName", out var name) || TestNames.Matches(leaf, name, null)))
                .Select(l => l.Key));
        }

        return [.. keys.Distinct(StringComparer.Ordinal)];
    }

    internal sealed class SavedPlaylist
    {
        public string? Name { get; set; }

        public List<string> Tests { get; set; } = [];
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(Playlists.SavedPlaylist))]
internal sealed partial class PlaylistJsonContext : JsonSerializerContext;
