using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Codalus.Projects.Protocol;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Build;

/// <summary>
/// A named set of startup projects (ADR 0110): the classic IDE's multi-project launch profile, read from and written to
/// the solution's launch files (<see cref="SolutionLaunchFiles"/>). <see cref="Shared"/> sets are in the file people
/// commit, the others in its <c>.user</c> file.
/// </summary>
public sealed class LaunchSet(string name, IReadOnlyList<StartupEntry> entries, bool shared)
{
    public string Name { get; } = name;

    /// <summary>The projects in start order, each with its action and launch profile (the file's <c>DebugTarget</c>).</summary>
    public IReadOnlyList<StartupEntry> Entries { get; } = entries;

    public bool Shared { get; } = shared;

    /// <summary>The file it was read from (null: not saved yet).</summary>
    public string? File { get; init; }

    /// <summary>Its JSON object as read, so a rewrite keeps the properties Codalus doesn't know.</summary>
    internal JsonObject? Source { get; init; }

    /// <summary>The set with other entries (and sharing), still tied to the file and object it came from.</summary>
    public LaunchSet With(string name, IReadOnlyList<StartupEntry> entries, bool shared) => new(name, entries, shared) { File = File, Source = Source };
}

/// <summary>What reading a solution's launch files found: the sets, and lines to log (a file that couldn't be read, a set hidden by another of its name).</summary>
public sealed record LaunchSetsRead(IReadOnlyList<LaunchSet> Sets, IReadOnlyList<string> Problems);

/// <summary>
/// The solution's launch files (ADR 0110), in the classic IDE's format: next to <c>Contoso.sln</c>,
/// <c>Contoso.slnLaunch</c> (shared, committed) and <c>Contoso.slnLaunch.user</c> (the user's own); next to
/// <c>Contoso.slnx</c>, <c>Contoso.slnxLaunch</c> first, then <c>Contoso.slnLaunch</c> (a file kept from before a
/// migration), each with its <c>.user</c> file. A file is a JSON array of
/// <c>{ "Name", "Projects": [{ "Path", "Action", "DebugTarget" }] }</c>: paths relative to the solution with
/// backslashes, actions <c>Start</c>, <c>StartWithoutDebugging</c> or <c>None</c>.
/// </summary>
public static class SolutionLaunchFiles
{
    public const string UserSuffix = ".user";

    private static readonly JsonDocumentOptions s_read = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// The shared launch files of a solution, in the order they're read (the first holds a name both have); empty for a
    /// folder or a project opened alone, which have no solution file.
    /// </summary>
    public static IReadOnlyList<string> SharedFiles(string solutionPath, string format)
    {
        var directory = Path.GetDirectoryName(solutionPath)!;
        var name = Path.GetFileNameWithoutExtension(solutionPath);
        return format switch
        {
            SolutionFormats.Slnx => [Path.Combine(directory, name + ".slnxLaunch"), Path.Combine(directory, name + ".slnLaunch")],
            SolutionFormats.Sln => [Path.Combine(directory, name + ".slnLaunch")],
            _ => [],
        };
    }

    /// <summary>The files a set of this kind is read from, in order.</summary>
    public static IReadOnlyList<string> Files(string solutionPath, string format, bool shared) =>
        shared ? SharedFiles(solutionPath, format) : [.. SharedFiles(solutionPath, format).Select(f => f + UserSuffix)];

    /// <summary>Reads every set: the shared files' first, then the user files'. A name already read hides a later set of that name.</summary>
    public static LaunchSetsRead Read(string solutionPath, string format)
    {
        var directory = Path.GetDirectoryName(solutionPath)!;
        var sets = new List<LaunchSet>();
        var problems = new List<string>();
        foreach (var (file, shared) in Files(solutionPath, format, shared: true).Select(f => (f, true)).Concat(Files(solutionPath, format, shared: false).Select(f => (f, false))))
        {
            string text;
            try
            {
                if (!System.IO.File.Exists(file))
                {
                    continue;
                }

                text = System.IO.File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{Path.GetFileName(file)} couldn't be read: {ex.Message}");
                continue;
            }

            JsonArray array;
            try
            {
                array = Parse(text);
            }
            catch (JsonException ex)
            {
                problems.Add($"{Path.GetFileName(file)} isn't a list of launch profiles: {ex.Message}");
                continue;
            }

            foreach (var node in array.OfType<JsonObject>())
            {
                if (Text(node, "Name") is not { Length: > 0 } name)
                {
                    continue;
                }

                if (sets.FirstOrDefault(s => s.Name == name) is { } earlier)
                {
                    problems.Add($"{Path.GetFileName(file)}: the launch profile '{name}' is hidden by the one in {Path.GetFileName(earlier.File)}.");
                    continue;
                }

                sets.Add(new LaunchSet(name, ReadEntries(node, directory), shared) { File = file, Source = (JsonObject)node.DeepClone() });
            }
        }

        return new LaunchSetsRead(sets, problems);
    }

    /// <summary>
    /// Saves a set: in the file it came from while its sharing is the same, else in the first existing file of its kind
    /// (a new one, <c>.slnxLaunch</c> for a <c>.slnx</c> and <c>.slnLaunch</c> for a <c>.sln</c>, if there's none), and
    /// out of the file it came from. <paramref name="previousName"/> is the name it had there (a rename). Other sets and
    /// their properties stay as they are. Returns the file it's in.
    /// </summary>
    public static string Save(string solutionPath, string format, LaunchSet set, string? previousName = null)
    {
        var directory = Path.GetDirectoryName(solutionPath)!;
        var kind = Files(solutionPath, format, set.Shared);
        if (kind.Count == 0)
        {
            throw new InvalidOperationException("Only a solution file has launch profiles.");
        }

        var target = set.File is { } from && kind.Contains(from, SolutionService.PathComparer)
            ? from
            : kind.FirstOrDefault(System.IO.File.Exists) ?? kind[0];
        var oldName = previousName ?? set.Name;
        var moved = set.File is { } source && !SolutionService.PathComparer.Equals(source, target);
        Edit(target, array =>
        {
            var node = ToJson(set, directory);
            var index = IndexOf(array, moved ? set.Name : oldName);
            if (index >= 0)
            {
                array[index] = node;
            }
            else
            {
                array.Add((JsonNode)node);
            }
        });
        if (moved)
        {
            Edit(set.File!, array => Remove(array, oldName));
        }

        return target;
    }

    /// <summary>Removes a set from its file; the file goes when no set is left in it.</summary>
    public static void Delete(LaunchSet set)
    {
        if (set.File is { } file)
        {
            Edit(file, array => Remove(array, set.Name));
        }
    }

    /// <summary>A file's new text after <paramref name="change"/>, or null when the change leaves it empty.</summary>
    internal static string? Apply(string? existing, Action<JsonArray> change)
    {
        var array = string.IsNullOrWhiteSpace(existing) ? [] : Parse(existing);
        change(array);
        if (array.Count == 0)
        {
            return null;
        }

        var newline = existing?.Contains("\r\n", StringComparison.Ordinal) == true ? "\r\n" : "\n";
        return Serialize(array).Replace("\n", newline, StringComparison.Ordinal) + newline;
    }

    private static void Edit(string file, Action<JsonArray> change)
    {
        var existing = System.IO.File.Exists(file) ? System.IO.File.ReadAllText(file) : null;
        if (Apply(existing, change) is { } text)
        {
            SolutionStateLayout.WriteAllTextAtomic(file, text);
        }
        else if (existing is not null)
        {
            System.IO.File.Delete(file);
        }
    }

    private static JsonArray Parse(string text) =>
        JsonNode.Parse(text, documentOptions: s_read) as JsonArray ?? throw new JsonException("The file's top level isn't an array.");

    private static List<StartupEntry> ReadEntries(JsonObject node, string directory)
    {
        var entries = new List<StartupEntry>();
        if (node["Projects"] is not JsonArray projects)
        {
            return entries;
        }

        foreach (var project in projects.OfType<JsonObject>())
        {
            if (Text(project, "Path") is not { Length: > 0 } path)
            {
                continue;
            }

            var full = FullPath(directory, path);
            if (entries.Any(e => SolutionService.PathComparer.Equals(e.Project, full)))
            {
                continue;
            }

            entries.Add(new StartupEntry(full, ParseAction(Text(project, "Action")), Text(project, "DebugTarget") is { Length: > 0 } target ? target : null));
        }

        return entries;
    }

    /// <summary>A file's path, with either slash, relative to the solution's folder (or rooted).</summary>
    internal static string FullPath(string directory, string path)
    {
        var native = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(directory, native));
    }

    /// <summary>A project's path as the file writes it: relative to the solution's folder, with backslashes.</summary>
    internal static string FilePath(string directory, string project) =>
        Path.GetRelativePath(directory, project).Replace('/', '\\');

    /// <summary>An action the file names; one Codalus doesn't know is None (its entry is kept as it is on a rewrite).</summary>
    internal static StartupAction ParseAction(string? action) => action switch
    {
        _ when string.Equals(action, nameof(StartupAction.Start), StringComparison.OrdinalIgnoreCase) => StartupAction.Start,
        _ when string.Equals(action, nameof(StartupAction.StartWithoutDebugging), StringComparison.OrdinalIgnoreCase) => StartupAction.StartWithoutDebugging,
        _ => StartupAction.None,
    };

    private static bool IsKnownAction(string? action) =>
        action is null || Enum.GetNames<StartupAction>().Contains(action, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A set as the file's JSON: its object as read with the name and projects replaced. Each project keeps its own
    /// object's other properties; a None project is left out unless its object had an action Codalus doesn't know.
    /// </summary>
    internal static JsonObject ToJson(LaunchSet set, string directory)
    {
        var node = (JsonObject?)set.Source?.DeepClone() ?? [];
        var sources = (set.Source?["Projects"] as JsonArray)?.OfType<JsonObject>()
            .Where(p => Text(p, "Path") is { Length: > 0 })
            .GroupBy(p => FullPath(directory, Text(p, "Path")!), SolutionService.PathComparer)
            .ToDictionary(g => g.Key, g => g.First(), SolutionService.PathComparer) ?? [];
        var projects = new JsonArray();
        foreach (var entry in set.Entries)
        {
            var source = sources.GetValueOrDefault(entry.Project);
            if (entry.Action == StartupAction.None)
            {
                if (source is not null && !IsKnownAction(Text(source, "Action")))
                {
                    projects.Add(source.DeepClone());
                }

                continue;
            }

            // A project read from the file keeps its path as written there (its slashes, its spelling).
            var project = (JsonObject?)source?.DeepClone() ?? [];
            project["Path"] = source is not null ? Text(source, "Path") : FilePath(directory, entry.Project);
            project["Action"] = entry.Action.ToString();
            if (entry.Profile is { Length: > 0 } profile)
            {
                project["DebugTarget"] = profile;
            }
            else
            {
                project.Remove("DebugTarget");
            }

            projects.Add((JsonNode)project);
        }

        node["Name"] = set.Name;
        node["Projects"] = projects;
        return node;
    }

    private static int IndexOf(JsonArray array, string name)
    {
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is JsonObject node && Text(node, "Name") == name)
            {
                return i;
            }
        }

        return -1;
    }

    private static void Remove(JsonArray array, string name)
    {
        if (IndexOf(array, name) is var index and >= 0)
        {
            array.RemoveAt(index);
        }
    }

    private static string? Text(JsonObject node, string key) =>
        node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string Serialize(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, IndentSize = 2, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            node.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
