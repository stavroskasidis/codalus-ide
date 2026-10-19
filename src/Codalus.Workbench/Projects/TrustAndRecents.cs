using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codalus.Workbench.Projects;

/// <summary>
/// Folders the user trusts (ADR 0004): opening a solution evaluates its projects, which runs code from them, so
/// a solution is evaluated only if its folder or a parent folder is trusted. Kept in the user's state, never in
/// the repository.
/// </summary>
/// <remarks>
/// Folders are saved and looked up by their real path (<see cref="Codalus.Platform.RealPath"/>, symbolic links
/// resolved), the spelling the shell opens solutions and folders under: trusting <c>/var/folders/…</c> on macOS covers
/// <c>/private/var/folders/…</c>, and a folder trusted through a link is trusted through its target and the other way
/// round. Entries saved before under a link's spelling are kept as they are and match by their spelling or by where
/// they lead now. Paths compare with the platform's case rules (<see cref="SolutionService.PathComparer"/>: ignoring
/// case on Windows and macOS). On Windows <see cref="Codalus.Platform.RealPath"/> leaves links as they are.
/// </remarks>
public sealed class TrustStore(string file)
{
    private readonly Lock _gate = new();
    private List<string>? _folders;

    /// <summary>The trusted folders, as saved.</summary>
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

    /// <summary>
    /// Whether <paramref name="path"/> is in a trusted folder, judged by its real path: where it leads is what runs, so a
    /// link inside a trusted folder that leads out of it isn't trusted.
    /// </summary>
    public bool IsTrusted(string path)
    {
        var real = Real(path);
        lock (_gate)
        {
            var folders = Load();

            // The saved spellings first: the saved folders are resolved (reading the disk) only when none matches.
            return folders.Any(folder => IsSameOrUnder(real, folder))
                || folders.Any(folder => IsSameOrUnder(real, Real(folder)));
        }
    }

    public void Trust(string folder) => Update(list =>
    {
        var real = Real(folder);
        if (!list.Any(f => IsSameOrUnder(real, f) || IsSameOrUnder(real, Real(f))))
        {
            list.RemoveAll(f => IsSameOrUnder(f, real) || IsSameOrUnder(Real(f), real));
            list.Add(real);
        }
    });

    /// <summary>Stops trusting <paramref name="folder"/>, whichever spelling it was trusted under.</summary>
    public void Revoke(string folder) => Update(list =>
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var real = Real(full);
        list.RemoveAll(f => SolutionService.PathComparer.Equals(f, full) || SolutionService.PathComparer.Equals(f, real) || SolutionService.PathComparer.Equals(Real(f), real));
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

    /// <summary>The folder's real path (links resolved), without a trailing separator.</summary>
    private static string Real(string path) => Path.TrimEndingDirectorySeparator(Codalus.Platform.RealPath.Resolve(path));

    private static bool IsSameOrUnder(string path, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(folder);
        var comparison = SolutionService.PathComparer == StringComparer.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
}

/// <summary>A recent solution, project or folder (M38): when it was last opened, and whether the user pinned it.</summary>
public sealed record RecentEntry(string Path, DateTimeOffset LastOpened, bool Pinned)
{
    /// <summary>The solution's or folder's name.</summary>
    public string Name => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path));
}

/// <summary>
/// Recently opened solutions, projects and folders, most recent first (File → Open Recent, the start window, the Start
/// page). Pinned entries stay however many others are opened; the others keep the newest <see cref="Max"/>.
/// </summary>
public sealed class RecentSolutions(string file, Func<DateTimeOffset>? clock = null)
{
    public const int Max = 50;
    private readonly Lock _gate = new();
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);
    private List<RecentEntry>? _entries;

    public event EventHandler? Changed;

    /// <summary>The paths, most recently opened first.</summary>
    public IReadOnlyList<string> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. Load().Select(e => e.Path)];
            }
        }
    }

    /// <summary>The entries, most recently opened first.</summary>
    public IReadOnlyList<RecentEntry> Entries
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
        var pinned = list.Any(e => SolutionService.PathComparer.Equals(e.Path, full) && e.Pinned);
        list.RemoveAll(e => SolutionService.PathComparer.Equals(e.Path, full));
        list.Insert(0, new RecentEntry(full, _clock(), pinned));
        var unpinned = list.Where(e => !e.Pinned).Skip(Max).ToList();
        list.RemoveAll(unpinned.Contains);
    });

    public void Remove(string path) => Update(list => list.RemoveAll(e => SolutionService.PathComparer.Equals(e.Path, Path.GetFullPath(path))));

    public void SetPinned(string path, bool pinned) => Update(list =>
    {
        var index = list.FindIndex(e => SolutionService.PathComparer.Equals(e.Path, Path.GetFullPath(path)));
        if (index >= 0)
        {
            list[index] = list[index] with { Pinned = pinned };
        }
    });

    /// <summary>The entries the start window shows, in groups: Pinned, Today, Yesterday, This week, This month, Older.</summary>
    public static IReadOnlyList<(string Group, IReadOnlyList<RecentEntry> Entries)> Group(IEnumerable<RecentEntry> entries, DateTimeOffset now)
    {
        var today = now.Date;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // weeks start on Monday
        string GroupOf(RecentEntry e)
        {
            var day = e.LastOpened.ToOffset(now.Offset).Date;
            return e.Pinned ? "Pinned"
                : day >= today ? "Today"
                : day == today.AddDays(-1) ? "Yesterday"
                : day >= weekStart ? "This week"
                : day.Year == today.Year && day.Month == today.Month ? "This month"
                : "Older";
        }

        string[] order = ["Pinned", "Today", "Yesterday", "This week", "This month", "Older"];
        return [.. entries.GroupBy(GroupOf).OrderBy(g => Array.IndexOf(order, g.Key))
            .Select(g => (g.Key, (IReadOnlyList<RecentEntry>)[.. g.OrderByDescending(e => e.LastOpened)]))];
    }

    private void Update(Action<List<RecentEntry>> change)
    {
        lock (_gate)
        {
            var list = Load();
            change(list);
            StateFile.Write(file, new RecentState { Entries = [.. list.Select(e => new RecentStateEntry(e.Path, e.LastOpened, e.Pinned ? true : null))] }, StateJsonContext.Default.RecentState);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The file's entries; an older file has only paths, which keep their order and no time ("Older").</summary>
    private List<RecentEntry> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        var state = StateFile.Read(file, StateJsonContext.Default.RecentState);
        _entries = state?.Entries is { } entries
            ? [.. entries.Select(e => new RecentEntry(e.Path, e.LastOpened, e.Pinned == true))]
            : [.. (state?.Solutions ?? []).Select(p => new RecentEntry(p, DateTimeOffset.MinValue, false))];
        return _entries;
    }
}

internal sealed record TrustState
{
    public IReadOnlyList<string>? Folders { get; init; }
}

internal sealed record RecentState
{
    /// <summary>The paths, as files before M38 wrote them.</summary>
    public IReadOnlyList<string>? Solutions { get; init; }

    public IReadOnlyList<RecentStateEntry>? Entries { get; init; }
}

internal sealed record RecentStateEntry(string Path, DateTimeOffset LastOpened, bool? Pinned);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
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
