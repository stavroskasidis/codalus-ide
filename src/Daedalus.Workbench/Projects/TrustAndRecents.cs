using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daedalus.Workbench.Projects;

/// <summary>
/// Folders the user trusts (ADR 0004): opening a solution evaluates its projects, which runs code from them, so
/// a solution is evaluated only if its folder or a parent folder is trusted. Kept in the user's state, never in
/// the repository.
/// </summary>
public sealed class TrustStore(string file)
{
    private readonly Lock _gate = new();
    private List<string>? _folders;

    public IReadOnlyList<string> Folders
    {
        get
        {
            lock (_gate)
            {
                return [.. Load()];
            }
        }
    }

    public bool IsTrusted(string path)
    {
        var full = Path.GetFullPath(path);
        lock (_gate)
        {
            return Load().Any(folder => IsSameOrUnder(full, folder));
        }
    }

    public void Trust(string folder) => Update(list =>
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!list.Any(f => IsSameOrUnder(full, f)))
        {
            list.RemoveAll(f => IsSameOrUnder(f, full));
            list.Add(full);
        }
    });

    public void Revoke(string folder) => Update(list =>
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        list.RemoveAll(f => SolutionService.PathComparer.Equals(f, full));
    });

    private void Update(Action<List<string>> change)
    {
        lock (_gate)
        {
            var list = Load();
            change(list);
            StateFile.Write(file, new TrustState { Folders = [.. list] }, StateJsonContext.Default.TrustState);
        }
    }

    private List<string> Load() => _folders ??= [.. StateFile.Read(file, StateJsonContext.Default.TrustState)?.Folders ?? []];

    private static bool IsSameOrUnder(string path, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(folder);
        var comparison = SolutionService.PathComparer == StringComparer.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
}

/// <summary>Recently opened solutions, most recent first (File → Open Recent, the Start page).</summary>
public sealed class RecentSolutions(string file)
{
    private const int Max = 15;
    private readonly Lock _gate = new();
    private List<string>? _items;

    public event EventHandler? Changed;

    public IReadOnlyList<string> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. Load()];
            }
        }
    }

    public void Add(string path) => Update(list =>
    {
        var full = Path.GetFullPath(path);
        list.RemoveAll(p => SolutionService.PathComparer.Equals(p, full));
        list.Insert(0, full);
        if (list.Count > Max)
        {
            list.RemoveRange(Max, list.Count - Max);
        }
    });

    public void Remove(string path) => Update(list => list.RemoveAll(p => SolutionService.PathComparer.Equals(p, Path.GetFullPath(path))));

    private void Update(Action<List<string>> change)
    {
        lock (_gate)
        {
            var list = Load();
            change(list);
            StateFile.Write(file, new RecentState { Solutions = [.. list] }, StateJsonContext.Default.RecentState);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private List<string> Load() => _items ??= [.. StateFile.Read(file, StateJsonContext.Default.RecentState)?.Solutions ?? []];
}

internal sealed record TrustState
{
    public IReadOnlyList<string>? Folders { get; init; }
}

internal sealed record RecentState
{
    public IReadOnlyList<string>? Solutions { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TrustState))]
[JsonSerializable(typeof(RecentState))]
internal sealed partial class StateJsonContext : JsonSerializerContext;

/// <summary>Small JSON state files: unreadable files count as empty, writes are atomic.</summary>
internal static class StateFile
{
    public static T? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), info) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Write<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, info));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing a recent-list entry is better than failing the user's action.
        }
    }
}
