using System.Text.Json.Serialization;
using Codalus.LanguageServices.Protocol;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Search;

/// <summary>
/// Code Search's persistent index (M47, ADR 0078): the solution's files and every type and member the language
/// services declared in them, kept next to the solution (<c>.codalus/state/&lt;solution&gt;/code-search-index.json</c>,
/// ADR 0070), so a second open searches at once, before the language host has loaded a project. A document whose
/// file changed since it was indexed (length or time written) is left out when the index is read, until the language
/// host indexes it again. Searching is in memory over an immutable snapshot, from any thread.
/// </summary>
public sealed class CodeSearchIndex
{
    /// <summary>The state file's name in the solution's state folder.</summary>
    public const string FileName = "code-search-index.json";

    /// <summary>The format's version: a file of another version is ignored (and rebuilt).</summary>
    public const int FormatVersion = 1;

    private readonly Lock _gate = new();
    private Snapshot _snapshot = Snapshot.Empty;

    /// <summary>Raised (on any thread) when the index changed: loaded, rebuilt or updated.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether the index was read from the solution's state or built in this session.</summary>
    public bool IsLoaded => _snapshot.Loaded;

    /// <summary>Whether the language host indexed the whole solution in this session (not only a saved index).</summary>
    public bool IsComplete => _snapshot.Complete;

    /// <summary>When the whole solution was last indexed in this session.</summary>
    public DateTime? CompletedAt => _snapshot.CompletedAt;

    public int SymbolCount => _snapshot.Entries.Length;

    public int DocumentCount => _snapshot.Documents.Count;

    /// <summary>The solution's files, as last known (saved with the index, so they show before projects evaluate).</summary>
    public IReadOnlyList<string> Files => _snapshot.Files;

    /// <summary>Forgets everything (the solution closed).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _snapshot = Snapshot.Empty;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces the symbols with the language host's index of the whole solution.</summary>
    public void Replace(IEnumerable<IndexedDocument> documents)
    {
        var map = new Dictionary<string, IndexedFile>(SolutionService.PathComparer);
        foreach (var document in documents)
        {
            if (FromHost(document) is { } file)
            {
                map[file.Path] = file;
            }
        }

        lock (_gate)
        {
            _snapshot = Build(map, _snapshot.Files, loaded: true, complete: true, completedAt: DateTime.UtcNow);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Updates the symbols of some documents (an edit, a save); <paramref name="removed"/> are gone.</summary>
    public void Update(IEnumerable<IndexedDocument> documents, IEnumerable<string>? removed = null)
    {
        lock (_gate)
        {
            var map = new Dictionary<string, IndexedFile>(_snapshot.Documents, SolutionService.PathComparer);
            foreach (var path in removed ?? [])
            {
                map.Remove(path);
            }

            foreach (var document in documents)
            {
                if (FromHost(document) is { } file)
                {
                    map[file.Path] = file;
                }
            }

            _snapshot = Build(map, _snapshot.Files, loaded: true, _snapshot.Complete, _snapshot.CompletedAt);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records the solution's files (from the projects' items), saved with the index.</summary>
    public void SetFiles(IReadOnlyList<string> files)
    {
        lock (_gate)
        {
            if (files.SequenceEqual(_snapshot.Files, SolutionService.PathComparer))
            {
                return;
            }

            _snapshot = _snapshot with { Files = files };
        }
    }

    private static IndexedFile? FromHost(IndexedDocument document)
    {
        if (DocumentUris.ToPath(document.Uri) is not { } path)
        {
            return null;
        }

        var (length, written) = Stamp(path);
        return new IndexedFile(path, document.Project, length, written, [.. document.Symbols.Select(s => new IndexedFileSymbol(s.Name, s.Kind, s.Line, s.Character, s.Container))]);
    }

    private static (long Length, long Written) Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (-1, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (-1, 0);
        }
    }

    // ------------------------------------------------------------------ search

    /// <summary>
    /// Types and members whose names match <paramref name="query"/> (camel case, word starts, then any subsequence;
    /// the matched characters are the highlights), ranked with <paramref name="context"/>. <paramref name="include"/>
    /// filters by file (the scope).
    /// </summary>
    public IReadOnlyList<CodeSearchResult> Search(CodeSearchQuery query, CodeSearchRankContext context, Func<string, bool>? include, int limit, CancellationToken cancellationToken)
    {
        var text = query.Text;
        var types = query.Wants(CodeSearchKind.Type);
        var members = query.Wants(CodeSearchKind.Member);
        if (text.Length == 0 || !types && !members)
        {
            return [];
        }

        // A large index is scanned in parallel chunks, each keeping its best results only (ADR 0078's budget: 100 ms
        // for a 96-project solution); results are built for those alone.
        var entries = _snapshot.Entries;
        var chunks = Math.Clamp(entries.Length / 8192, 1, Environment.ProcessorCount);
        var best = new List<(Entry Entry, int Score)>[chunks];
        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = chunks }, chunk =>
        {
            var top = new PriorityQueue<(Entry Entry, int Score), int>();
            var from = (int)((long)entries.Length * chunk / chunks);
            var to = (int)((long)entries.Length * (chunk + 1) / chunks);
            for (var i = from; i < to; i++)
            {
                if (((i - from) & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var entry = entries[i];
                if ((entry.IsType ? !types : !members) || !CodeSearchRanker.MayMatch(text, entry.Name) || !FuzzyMatcher.TryScore(text, entry.Name, out var matchScore)
                    || include is not null && !include(entry.File.Path))
                {
                    continue;
                }

                var score = CodeSearchRanker.Score(entry.IsType ? CodeSearchKind.Type : CodeSearchKind.Member, entry.File.Path, entry.File.Project, external: false, matchScore, context);
                if (top.Count < limit)
                {
                    top.Enqueue((entry, score), score);
                }
                else if (top.TryPeek(out _, out var lowest) && score > lowest)
                {
                    top.DequeueEnqueue((entry, score), score);
                }
            }

            best[chunk] = [.. top.UnorderedItems.Select(i => i.Element)];
        });

        return CodeSearchRanker.Order(best.SelectMany(b => b).Select(b => new CodeSearchResult(b.Entry.IsType ? CodeSearchKind.Type : CodeSearchKind.Member, b.Entry.Name)
        {
            SymbolKind = b.Entry.Kind,
            Container = b.Entry.Container,
            Project = b.Entry.File.Project,
            Path = b.Entry.File.Path,
            Line = b.Entry.Line,
            Column = b.Entry.Character,
            Length = b.Entry.Name.Length,
            Highlights = FuzzyMatcher.Match(text, b.Entry.Name)?.Positions ?? [],
            Score = b.Score,
        }), limit);
    }

    /// <summary>
    /// Files whose names match (or whose relative paths do, for a query with a separator), ranked; the container is
    /// the folder below <paramref name="root"/>.
    /// </summary>
    public static IReadOnlyList<CodeSearchResult> SearchFiles(IEnumerable<string> files, CodeSearchQuery query, CodeSearchRankContext context, string? root, Func<string, string?>? projectOf, int limit, CancellationToken cancellationToken)
    {
        var text = query.Text;
        if (text.Length == 0 || !query.Wants(CodeSearchKind.File))
        {
            return [];
        }

        var byPath = text.Contains('/') || text.Contains('\\');
        var pattern = byPath ? text.Replace('\\', '/') : text;
        var found = new List<CodeSearchResult>();
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = System.IO.Path.GetFileName(path);
            var subject = byPath ? Relative(path, root).Replace('\\', '/') : name;
            if (!CodeSearchRanker.MayMatch(pattern, subject) || FuzzyMatcher.Match(pattern, subject) is not { } match)
            {
                continue;
            }

            var offset = subject.Length - name.Length;
            var result = new CodeSearchResult(CodeSearchKind.File, name)
            {
                Container = System.IO.Path.GetDirectoryName(Relative(path, root)) is { Length: > 0 } folder ? folder : null,
                Path = path,
                Project = projectOf?.Invoke(path),
                Highlights = [.. match.Positions.Where(p => p >= offset).Select(p => p - offset)],
            };
            found.Add(result with { Score = CodeSearchRanker.Score(result, match.Score, context) });
        }

        return CodeSearchRanker.Order(found, limit);
    }

    private static string Relative(string path, string? root) =>
        root is not null && FileSystem.FileSystems.IsUnder(path, root) ? System.IO.Path.GetRelativePath(root, path) : path;

    // ------------------------------------------------------------------ persistence

    /// <summary>
    /// Reads the solution's saved index (off the calling thread), leaving out documents whose files changed or are
    /// gone. False when there is none (or it is of another format).
    /// </summary>
    public async Task<bool> LoadAsync(SolutionStateStore store, SolutionStateKey key, CancellationToken cancellationToken)
    {
        if (await store.ReadAsync(key, FileName, CodeSearchJsonContext.Default.CodeSearchIndexFile).ConfigureAwait(false) is not { Version: FormatVersion } saved)
        {
            return false;
        }

        var map = await Task.Run(() =>
        {
            var documents = new Dictionary<string, IndexedFile>(SolutionService.PathComparer);
            foreach (var document in saved.Documents ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Stamp(document.Path) == (document.Length, document.Written))
                {
                    documents[document.Path] = document;
                }
            }

            return documents;
        }, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            if (_snapshot.Complete)
            {
                return true; // the language host was faster: its index is newer
            }

            _snapshot = Build(map, saved.Files ?? [], loaded: true, complete: false, completedAt: null);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Writes the index next to the solution, in the background.</summary>
    public Task SaveAsync(SolutionStateStore store, SolutionStateKey key)
    {
        var snapshot = _snapshot;
        return store.WriteAsync(key, FileName, new CodeSearchIndexFile(FormatVersion, [.. snapshot.Documents.Values.OrderBy(d => d.Path, StringComparer.Ordinal)], snapshot.Files), CodeSearchJsonContext.Default.CodeSearchIndexFile);
    }

    // ------------------------------------------------------------------ snapshot

    private static Snapshot Build(Dictionary<string, IndexedFile> documents, IReadOnlyList<string> files, bool loaded, bool complete, DateTime? completedAt)
    {
        var entries = new List<Entry>(documents.Values.Sum(d => d.Symbols.Count));
        foreach (var file in documents.Values)
        {
            foreach (var symbol in file.Symbols)
            {
                entries.Add(new Entry(symbol.Name, symbol.Kind, IsTypeKind(symbol.Kind), symbol.Line, symbol.Character, symbol.Container, file));
            }
        }

        return new Snapshot([.. entries], documents, files, loaded, complete, completedAt);
    }

    /// <summary>Classes, interfaces, structs and enums are types; the rest are members.</summary>
    public static bool IsTypeKind(int kind) => kind is SymbolKind.Class or SymbolKind.Interface or SymbolKind.Struct or SymbolKind.Enum or SymbolKind.Module;

    private sealed record Snapshot(Entry[] Entries, IReadOnlyDictionary<string, IndexedFile> Documents, IReadOnlyList<string> Files, bool Loaded, bool Complete, DateTime? CompletedAt)
    {
        public static Snapshot Empty { get; } = new([], new Dictionary<string, IndexedFile>(), [], false, false, null);
    }

    private sealed record Entry(string Name, int Kind, bool IsType, int Line, int Character, string? Container, IndexedFile File);
}

/// <summary>The saved index (<see cref="CodeSearchIndex.FileName"/>).</summary>
internal sealed record CodeSearchIndexFile(int Version, List<IndexedFile>? Documents, IReadOnlyList<string>? Files);

/// <summary>A document's symbols, with its file's length and time written (UTC ticks) when it was indexed.</summary>
internal sealed record IndexedFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("length")] long Length,
    [property: JsonPropertyName("written")] long Written,
    [property: JsonPropertyName("symbols")] List<IndexedFileSymbol> Symbols);

/// <summary>A symbol, with short names: a large solution's index has hundreds of thousands.</summary>
internal sealed record IndexedFileSymbol(
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("k")] int Kind,
    [property: JsonPropertyName("l")] int Line,
    [property: JsonPropertyName("c")] int Character,
    [property: JsonPropertyName("in")] string? Container);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CodeSearchIndexFile))]
internal sealed partial class CodeSearchJsonContext : JsonSerializerContext;
