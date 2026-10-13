using System.Text.Json;
using System.Text.Json.Serialization;

namespace Codalus.Workbench.Bookmarks;

/// <summary>A bookmark: a file, a one-based line and an optional name.</summary>
public sealed record Bookmark(string Path, int Line, string? Name = null)
{
    public string Display => Name is { Length: > 0 } name ? name : $"{System.IO.Path.GetFileName(Path)}:{Line}";
}

/// <summary>
/// Bookmarks (M12b): toggled on a line, visited in order across files (Ctrl+K Ctrl+N / Ctrl+K Ctrl+P), named, and
/// kept in the state folder (<c>bookmarks.json</c>). Editors move them with their lines. Thread-safe; <see cref="Changed"/>
/// is raised on the calling thread.
/// </summary>
public sealed class BookmarkService(string file)
{
    private readonly Lock _gate = new();
    private List<Bookmark>? _bookmarks;

    public event EventHandler? Changed;

    public IReadOnlyList<Bookmark> All
    {
        get
        {
            lock (_gate)
            {
                return [.. Loaded().OrderBy(b => b.Path, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Line)];
            }
        }
    }

    public IReadOnlyList<Bookmark> For(string path) => [.. All.Where(b => Same(b.Path, path))];

    /// <summary>Adds a bookmark on the line, or removes the one there. Returns whether the line has one now.</summary>
    public bool Toggle(string path, int line)
    {
        bool added;
        lock (_gate)
        {
            var bookmarks = Loaded();
            var existing = bookmarks.FindIndex(b => Same(b.Path, path) && b.Line == line);
            added = existing < 0;
            if (added)
            {
                bookmarks.Add(new Bookmark(path, line));
            }
            else
            {
                bookmarks.RemoveAt(existing);
            }

            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    public void Remove(Bookmark bookmark) => Update(list => list.Remove(bookmark));

    public void Rename(Bookmark bookmark, string? name) => Update(list =>
    {
        var index = list.IndexOf(bookmark);
        if (index >= 0)
        {
            list[index] = bookmark with { Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim() };
        }
    });

    public void Clear() => Update(list => list.Clear());

    /// <summary>The lines of a file's bookmarks moved (an edit above them): replaces them in order.</summary>
    public void Move(string path, IReadOnlyList<(Bookmark Bookmark, int Line)> moves)
    {
        if (moves.All(m => m.Bookmark.Line == m.Line))
        {
            return;
        }

        Update(list =>
        {
            foreach (var (bookmark, line) in moves)
            {
                var index = list.IndexOf(bookmark);
                if (index >= 0)
                {
                    list[index] = bookmark with { Line = line };
                }
            }

            // Two bookmarks that ended on the same line (the lines between them were deleted) become one.
            var duplicates = list.GroupBy(b => (b.Path.ToUpperInvariant(), b.Line)).SelectMany(g => g.Skip(1)).ToList();
            foreach (var duplicate in duplicates)
            {
                list.Remove(duplicate);
            }
        });
    }

    /// <summary>The next (or previous) bookmark after a position, wrapping around across files.</summary>
    public Bookmark? Next(string? path, int line, int direction)
    {
        var all = All;
        if (all.Count == 0)
        {
            return null;
        }

        int Compare(Bookmark b)
        {
            var byPath = path is null ? 1 : string.Compare(b.Path, path, StringComparison.OrdinalIgnoreCase);
            return byPath != 0 ? byPath : b.Line.CompareTo(line);
        }

        return direction > 0
            ? all.FirstOrDefault(b => Compare(b) > 0) ?? all[0]
            : all.LastOrDefault(b => Compare(b) < 0) ?? all[^1];
    }

    private void Update(Action<List<Bookmark>> change)
    {
        lock (_gate)
        {
            change(Loaded());
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private List<Bookmark> Loaded()
    {
        if (_bookmarks is not null)
        {
            return _bookmarks;
        }

        try
        {
            _bookmarks = File.Exists(file) ? JsonSerializer.Deserialize(File.ReadAllText(file), BookmarkJsonContext.Default.ListBookmark) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _bookmarks = [];
        }

        return _bookmarks;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(_bookmarks ?? [], BookmarkJsonContext.Default.ListBookmark));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool Same(string a, string b) => Projects.SolutionService.PathComparer.Equals(a, b);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<Bookmark>))]
internal sealed partial class BookmarkJsonContext : JsonSerializerContext;
