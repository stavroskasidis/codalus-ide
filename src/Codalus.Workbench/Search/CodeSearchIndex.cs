using System.Runtime.CompilerServices;
using System.Text;
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
    private LastMatch? _lastMatch;
    private int _generation; // counts Clear's: what was built for a closed solution isn't put back

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
            _lastMatch = null;
            _generation++;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// <see cref="Replace"/> off the calling thread: the files are stamped and the snapshot built in the background (an
    /// index of the 96-project benchmark's shape, 144,000 symbols, takes 0.15–0.3 s), then put in place, unless the index
    /// was cleared meanwhile. Returns whether it was put in place.
    /// </summary>
    public async Task<bool> ReplaceAsync(IReadOnlyList<IndexedDocument> documents)
    {
        var generation = Volatile.Read(ref _generation);
        var built = await Task.Run(() => Build(Map(documents), [], loaded: true, complete: true, completedAt: DateTime.UtcNow)).ConfigureAwait(false);
        lock (_gate)
        {
            if (_generation != generation)
            {
                return false;
            }

            _snapshot = built with { Files = _snapshot.Files };
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// <see cref="Update"/> off the calling thread, built again when another change came first; nothing changes if the
    /// index was cleared meanwhile. Returns whether it was put in place.
    /// </summary>
    public async Task<bool> UpdateAsync(IReadOnlyList<IndexedDocument> documents, IReadOnlyList<string> removed)
    {
        var generation = Volatile.Read(ref _generation);
        var changed = await Task.Run(() => Map(documents)).ConfigureAwait(false);
        while (true)
        {
            var basis = _snapshot;
            var built = await Task.Run(() =>
            {
                var map = new Dictionary<string, IndexedFile>(basis.Documents, SolutionService.PathComparer);
                foreach (var path in removed)
                {
                    map.Remove(path);
                }

                foreach (var (path, file) in changed)
                {
                    map[path] = file;
                }

                return Build(map, basis.Files, loaded: true, basis.Complete, basis.CompletedAt);
            }).ConfigureAwait(false);
            lock (_gate)
            {
                if (_generation != generation)
                {
                    return false;
                }

                if (ReferenceEquals(_snapshot, basis))
                {
                    _snapshot = built;
                    break;
                }
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>The host's documents by path, each stamped with its file's length and time written.</summary>
    private static Dictionary<string, IndexedFile> Map(IEnumerable<IndexedDocument> documents)
    {
        var map = new Dictionary<string, IndexedFile>(SolutionService.PathComparer);
        foreach (var document in documents)
        {
            if (FromHost(document) is { } file)
            {
                map[file.Path] = file;
            }
        }

        return map;
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
        if (text.Length == 0 || !types && !members || limit <= 0)
        {
            return [];
        }

        // ADR 0078's budget: 100 ms for a 96-project solution, as the user types. Names repeat (members above all), so
        // each distinct name is matched once, and only the names that have every letter of the query (a mask computed
        // when the index was built) are matched at all; typing on ("In", then "Inv") matches only the names the
        // previous query matched. The names are then taken best first, their declarations taking their kind's weight
        // and their file's boost (found from the context's files and project), into a heap bounded by the limit and
        // ordered as the results are; a name that can no longer reach the heap ends the walk. Results and highlights
        // are built for the kept alone.
        var snapshot = _snapshot;
        var names = MatchNames(snapshot, text, cancellationToken);
        var kept = Select(snapshot, names, types, members, context, include, limit, cancellationToken, out var full);
        var results = Build(kept);
        if (full && results.Count < kept.Length)
        {
            // Two kept declarations were one result (CodeSearchRanker.Order keeps one of each), so fewer than the
            // limit were left: everything is ranked instead. It takes a host reporting a declaration twice.
            results = Build(Select(snapshot, names, types, members, context, include, int.MaxValue, cancellationToken, out _));
        }

        return results;

        IReadOnlyList<CodeSearchResult> Build(Hit[] hits)
        {
            // In the index's order, which breaks Order's last ties.
            Array.Sort(hits, static (a, b) => a.Ordinal.CompareTo(b.Ordinal));
            return CodeSearchRanker.Order(hits.Select(h => new CodeSearchResult(h.Entry.IsType ? CodeSearchKind.Type : CodeSearchKind.Member, h.Entry.Name)
            {
                SymbolKind = h.Entry.Kind,
                Container = h.Entry.Container,
                Project = h.Entry.File.Project,
                Path = h.Entry.File.Path,
                Line = h.Entry.Line,
                Column = h.Entry.Character,
                Length = h.Entry.Name.Length,
                Highlights = FuzzyMatcher.Match(text, h.Entry.Name)?.Positions ?? [],
                Score = h.Score,
            }), limit);
        }
    }

    // The loops below are compiled fully optimized at once (AggressiveOptimization): a search runs them over the whole
    // index a few times, too few for tiered compilation to replace its first, unoptimized code in time.

    /// <summary>The distinct names that match <paramref name="text"/> (their numbers), and their match scores, best first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private (int[] Names, int[] Scores) MatchNames(Snapshot snapshot, string text, CancellationToken cancellationToken)
    {
        var names = snapshot.Names;
        var masks = snapshot.Masks;
        var asciiNames = snapshot.NameIsAscii;
        var mask = CharMask(text);

        // An ASCII query matching an ASCII name has every character in order (the matcher takes them so), so the
        // quick test is needed only for the others.
        var asciiQuery = Ascii.IsValid(text);

        // Typing on: a name the shorter query didn't match can't match the longer one (the matcher takes the query's
        // characters in order, so the longer query fails where the shorter one did).
        var previous = _lastMatch;
        var candidates = previous is not null && ReferenceEquals(previous.Snapshot, snapshot)
            && text.Length > previous.Text.Length && text.StartsWith(previous.Text, StringComparison.Ordinal)
            ? previous.Names
            : null;
        var count = candidates?.Length ?? names.Length;
        var found = new int[Math.Min(count, 1024)];
        var scores = new int[found.Length];
        var matched = 0;
        var lowest = int.MaxValue;
        var highest = int.MinValue;
        for (var k = 0; k < count; k++)
        {
            if ((k & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var i = candidates is null ? k : candidates[k];
            var name = names[i];
            if ((masks[i] & mask) != mask
                || !(asciiQuery && asciiNames[i]) && !CodeSearchRanker.MayMatch(text, name)
                || !FuzzyMatcher.TryScore(text, name, out var score))
            {
                continue;
            }

            if (matched == found.Length)
            {
                Array.Resize(ref found, found.Length * 2);
                Array.Resize(ref scores, found.Length);
            }

            found[matched] = i;
            scores[matched++] = score;
            lowest = Math.Min(lowest, score);
            highest = Math.Max(highest, score);
        }

        _lastMatch = new LastMatch(snapshot, text, found.AsSpan(0, matched).ToArray());

        // Best first, by counting: scores span a few hundred values at most.
        var counts = new int[matched == 0 ? 1 : highest - lowest + 2];
        for (var k = 0; k < matched; k++)
        {
            counts[highest - scores[k] + 1]++;
        }

        for (var c = 1; c < counts.Length; c++)
        {
            counts[c] += counts[c - 1];
        }

        var sortedNames = new int[matched];
        var sortedScores = new int[matched];
        for (var k = 0; k < matched; k++)
        {
            var at = counts[highest - scores[k]]++;
            sortedNames[at] = found[k];
            sortedScores[at] = scores[k];
        }

        return (sortedNames, sortedScores);
    }

    /// <summary>The best <paramref name="limit"/> declarations of <paramref name="names"/>; <paramref name="full"/> when that many were kept.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Hit[] Select(Snapshot snapshot, (int[] Names, int[] Scores) names, bool types, bool members, CodeSearchRankContext context, Func<string, bool>? include, int limit, CancellationToken cancellationToken, out bool full)
    {
        var typeWeight = CodeSearchRanker.KindWeight(CodeSearchKind.Type, external: false);
        var memberWeight = CodeSearchRanker.KindWeight(CodeSearchKind.Member, external: false);
        var reach = Math.Max(types ? typeWeight : int.MinValue, members ? memberWeight : int.MinValue) + MaxContextBoost(context);
        var sources = snapshot.Sources;
        var boosts = ContextBoosts(snapshot, context);
        var scope = include is null ? null : new sbyte[sources.Length];
        var snapshotNames = snapshot.Names;
        var entries = snapshot.Entries;
        var entrySource = snapshot.EntrySource;
        var entryIsType = snapshot.EntryIsType;
        var starts = snapshot.Starts;
        var top = new TopHeap<Hit, HitOrder>(limit);
        var visited = 0;
        for (var n = 0; n < names.Names.Length; n++)
        {
            var name = names.Names[n];
            var matchScore = names.Scores[n];

            // The names come best first: once the heap is full, a name whose best declaration would score below the
            // heap's last can't place, nor can any name after it.
            if (top.IsFull && matchScore + reach < top.Worst.Score)
            {
                break;
            }

            for (var e = starts[name]; e < starts[name + 1]; e++)
            {
                if ((++visited & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var isType = entryIsType[e];
                if (isType ? !types : !members)
                {
                    continue;
                }

                var source = entrySource[e];
                if (scope is not null)
                {
                    if (scope[source] == 0)
                    {
                        scope[source] = include!(sources[source].Path) ? (sbyte)1 : (sbyte)-1;
                    }

                    if (scope[source] < 0)
                    {
                        continue;
                    }
                }

                var score = matchScore + boosts[source] + (isType ? typeWeight : memberWeight);
                if (!top.IsFull || score > top.Worst.Score || score == top.Worst.Score && top.Worst.Name != name)
                {
                    // (A declaration of the worst's name that only ties it comes after it, so ranks below it.)
                    top.Offer(new Hit(entries[e], snapshotNames[name], name, score, e));
                }
            }
        }

        full = top.IsFull;
        return top.ToArray();
    }

    /// <summary>
    /// <see cref="CodeSearchRanker.ContextBoost"/> of each document, found from the context's files and project rather
    /// than by comparing every document with them.
    /// </summary>
    private static int[] ContextBoosts(Snapshot snapshot, CodeSearchRankContext context)
    {
        var boosts = new int[snapshot.Sources.Length];
        for (var i = 0; i < context.RecentFiles.Count && i < 8; i++)
        {
            if (context.RecentFiles[i] is { } recent && snapshot.SourcesByPath.TryGetValue(recent, out var recentSources))
            {
                foreach (var source in recentSources)
                {
                    // The most recent of the files it is counts.
                    if (boosts[source] == 0)
                    {
                        boosts[source] = Math.Max(3, 15 - 2 * i);
                    }
                }
            }
        }

        if (context.ActiveDocument is { } document && snapshot.SourcesByPath.TryGetValue(document, out var documentSources))
        {
            foreach (var source in documentSources)
            {
                boosts[source] += 4;
            }
        }

        if (context.ActiveProject is { } project && snapshot.SourcesByProject.TryGetValue(project, out var projectSources))
        {
            foreach (var source in projectSources)
            {
                boosts[source] += 6;
            }
        }

        return boosts;
    }

    /// <summary>The most <see cref="CodeSearchRanker.ContextBoost"/> can add in <paramref name="context"/>.</summary>
    private static int MaxContextBoost(CodeSearchRankContext context) =>
        (context.RecentFiles.Count > 0 ? 15 : 0) + (context.ActiveDocument is null ? 0 : 4) + (context.ActiveProject is null ? 0 : 6);

    /// <summary>
    /// A bit for each ASCII letter (either case), digit and other ASCII character of <paramref name="text"/> (spaces
    /// and other characters left out): a name can match a query only if its mask has every bit of the query's.
    /// </summary>
    private static ulong CharMask(ReadOnlySpan<char> text)
    {
        var mask = 0UL;
        foreach (var c in text)
        {
            if (c is ' ' or >= (char)128)
            {
                continue;
            }

            var bit = c switch
            {
                >= 'a' and <= 'z' => c - 'a',
                >= 'A' and <= 'Z' => c - 'A',
                >= '0' and <= '9' => 26 + (c - '0'),
                _ => 36 + (c % 28),
            };
            mask |= 1UL << bit;
        }

        return mask;
    }

    /// <summary>A declaration kept for the results: its name (and the name's number), its score and its place in the index.</summary>
    private readonly record struct Hit(Entry Entry, string NameText, int Name, int Score, int Ordinal);

    /// <summary>The names the last query matched, for the next one when it types on.</summary>
    private sealed record LastMatch(Snapshot Snapshot, string Text, int[] Names);

    /// <summary>
    /// <see cref="CodeSearchRanker.Order"/>'s order, worst first (the heap's top is the one to drop): score, then
    /// shorter names, then by name, path and line, then the index's order. A name's declarations are in the index by
    /// path and line, so for two of one name the index's order decides.
    /// </summary>
    private readonly struct HitOrder : IComparer<Hit>
    {
        public int Compare(Hit x, Hit y)
        {
            if (x.Score != y.Score)
            {
                return x.Score.CompareTo(y.Score);
            }

            if (x.Name != y.Name)
            {
                if (x.NameText.Length != y.NameText.Length)
                {
                    return y.NameText.Length.CompareTo(x.NameText.Length);
                }

                var order = string.Compare(y.NameText, x.NameText, StringComparison.OrdinalIgnoreCase);
                if (order == 0)
                {
                    order = string.Compare(y.Entry.File.Path, x.Entry.File.Path, StringComparison.OrdinalIgnoreCase);
                }

                if (order == 0)
                {
                    order = y.Entry.Line.CompareTo(x.Entry.Line);
                }

                if (order != 0)
                {
                    return order;
                }
            }

            return y.Ordinal.CompareTo(x.Ordinal);
        }
    }

    /// <summary>
    /// The best items offered, up to a limit: a binary heap whose top is the worst kept (<typeparamref name="TOrder"/>
    /// orders worst first), replaced when a better one comes.
    /// </summary>
    private sealed class TopHeap<T, TOrder>(int limit)
        where TOrder : struct, IComparer<T>
    {
        private T[] _items = new T[Math.Min(limit, 256)];
        private int _count;

        public bool IsFull => _count >= limit;

        /// <summary>The worst item kept (there must be one).</summary>
        public T Worst => _items[0];

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Offer(T item)
        {
            var order = default(TOrder);
            int i;
            if (_count < limit)
            {
                if (_count == _items.Length)
                {
                    Array.Resize(ref _items, (int)Math.Min(2L * _items.Length, limit));
                }

                // Up from the end while better than the parent.
                for (i = _count++; i > 0; i = (i - 1) >> 1)
                {
                    var parent = _items[(i - 1) >> 1];
                    if (order.Compare(item, parent) >= 0)
                    {
                        break;
                    }

                    _items[i] = parent;
                }
            }
            else if (order.Compare(item, _items[0]) > 0)
            {
                // In place of the worst, down while worse than a child.
                for (i = 0; ;)
                {
                    var child = 2 * i + 1;
                    if (child >= _count)
                    {
                        break;
                    }

                    if (child + 1 < _count && order.Compare(_items[child + 1], _items[child]) < 0)
                    {
                        child++;
                    }

                    if (order.Compare(item, _items[child]) <= 0)
                    {
                        break;
                    }

                    _items[i] = _items[child];
                    i = child;
                }
            }
            else
            {
                return;
            }

            _items[i] = item;
        }

        public T[] ToArray() => _items.AsSpan(0, _count).ToArray();
    }

    /// <summary>
    /// Files whose names match (or whose relative paths do, for a query with a separator), ranked; the container is
    /// the folder below <paramref name="root"/>.
    /// </summary>
    public static IReadOnlyList<CodeSearchResult> SearchFiles(IEnumerable<string> files, CodeSearchQuery query, CodeSearchRankContext context, string? root, Func<string, string?>? projectOf, int limit, CancellationToken cancellationToken)
    {
        var text = query.Text;
        if (text.Length == 0 || !query.Wants(CodeSearchKind.File) || limit <= 0)
        {
            return [];
        }

        // Scored without allocating (a file's name is a span of its path) into a bounded heap ordered as the results
        // are; results, highlights and containers are built for the kept alone.
        var byPath = text.Contains('/') || text.Contains('\\');
        var pattern = byPath ? text.Replace('\\', '/') : text;
        var hits = ScoreFiles(files, pattern, byPath, context, root, projectOf, limit, cancellationToken);

        // In the files' order, which breaks Order's last ties.
        Array.Sort(hits, static (a, b) => a.Ordinal.CompareTo(b.Ordinal));
        var results = CodeSearchRanker.Order(hits.Select(Result), limit);
        if (hits.Length >= limit && results.Count < hits.Length)
        {
            // Two kept files were one result (paths that differ in case alone), so fewer than the limit were left:
            // everything is ranked instead.
            results = CodeSearchRanker.Order(ScoreFiles(files, pattern, byPath, context, root, projectOf, int.MaxValue, cancellationToken).OrderBy(h => h.Ordinal).Select(Result), limit);
        }

        return results;

        CodeSearchResult Result(FileHit hit)
        {
            var name = hit.Path[hit.NameStart..];
            var subject = byPath ? Relative(hit.Path, root).Replace('\\', '/') : name;
            var offset = subject.Length - name.Length;
            var positions = FuzzyMatcher.Match(pattern, subject)?.Positions ?? [];
            return new CodeSearchResult(CodeSearchKind.File, name)
            {
                Container = System.IO.Path.GetDirectoryName(Relative(hit.Path, root)) is { Length: > 0 } folder ? folder : null,
                Path = hit.Path,
                Project = hit.Project,
                Highlights = [.. positions.Where(p => p >= offset).Select(p => p - offset)],
                Score = hit.Score,
            };
        }
    }

    /// <summary>The best <paramref name="limit"/> of <paramref name="files"/> that match <paramref name="pattern"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static FileHit[] ScoreFiles(IEnumerable<string> files, string pattern, bool byPath, CodeSearchRankContext context, string? root, Func<string, string?>? projectOf, int limit, CancellationToken cancellationToken)
    {
        // FuzzyMatcher.Match's scores: it trims the pattern, and a blank one matches everything.
        var trimmed = pattern.AsSpan().Trim();
        var fileWeight = CodeSearchRanker.KindWeight(CodeSearchKind.File, external: false);

        // A file can be one of the context's only if its path is as long as one of theirs.
        var lengths = new List<int>();
        for (var i = 0; i < context.RecentFiles.Count && i < 8; i++)
        {
            lengths.Add(context.RecentFiles[i]?.Length ?? -1);
        }

        lengths.Add(context.ActiveDocument?.Length ?? -1);
        var contextLengths = lengths.ToArray();
        var top = new TopHeap<FileHit, FileHitOrder>(limit);
        var ordinal = 0;
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = System.IO.Path.GetFileName(path.AsSpan());
            var subject = byPath ? Relative(path, root).Replace('\\', '/').AsSpan() : name;
            var matchScore = 0;
            if (!CodeSearchRanker.MayMatch(pattern, subject) || trimmed.Length > 0 && !FuzzyMatcher.TryScore(trimmed, subject, out matchScore))
            {
                continue;
            }

            var project = projectOf?.Invoke(path);
            var boost = CodeSearchRanker.ContextBoost(contextLengths.AsSpan().Contains(path.Length) ? path : null, project, context);
            top.Offer(new FileHit(path, path.Length - name.Length, project, matchScore + fileWeight + boost, ordinal++));
        }

        return top.ToArray();
    }

    private static string Relative(string path, string? root) =>
        root is not null && FileSystem.FileSystems.IsUnder(path, root) ? System.IO.Path.GetRelativePath(root, path) : path;

    /// <summary>A file kept for the results; its name starts at <see cref="NameStart"/> in its path.</summary>
    private readonly record struct FileHit(string Path, int NameStart, string? Project, int Score, int Ordinal);

    /// <summary><see cref="CodeSearchRanker.Order"/>'s order for files, worst first: score, shorter names, name, path, then the files' order.</summary>
    private readonly struct FileHitOrder : IComparer<FileHit>
    {
        public int Compare(FileHit x, FileHit y)
        {
            if (x.Score != y.Score)
            {
                return x.Score.CompareTo(y.Score);
            }

            var xName = x.Path.AsSpan(x.NameStart);
            var yName = y.Path.AsSpan(y.NameStart);
            if (xName.Length != yName.Length)
            {
                return yName.Length.CompareTo(xName.Length);
            }

            var order = yName.CompareTo(xName, StringComparison.OrdinalIgnoreCase);
            if (order == 0)
            {
                order = string.Compare(y.Path, x.Path, StringComparison.OrdinalIgnoreCase);
            }

            return order != 0 ? order : y.Ordinal.CompareTo(x.Ordinal);
        }
    }

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
        // The snapshot is immutable: sorted and serialized in the background (megabytes for a large solution).
        var snapshot = _snapshot;
        return store.WriteAsync(key, FileName, () => new CodeSearchIndexFile(FormatVersion, [.. snapshot.Documents.Values.OrderBy(d => d.Path, StringComparer.Ordinal)], snapshot.Files), CodeSearchJsonContext.Default.CodeSearchIndexFile);
    }

    // ------------------------------------------------------------------ snapshot

    private static Snapshot Build(Dictionary<string, IndexedFile> documents, IReadOnlyList<string> files, bool loaded, bool complete, DateTime? completedAt)
    {
        var sources = documents.Values.ToArray();
        var entries = new List<Entry>(sources.Sum(d => d.Symbols.Count));
        for (var s = 0; s < sources.Length; s++)
        {
            var file = sources[s];
            foreach (var symbol in file.Symbols)
            {
                entries.Add(new Entry(symbol.Name, symbol.Kind, IsTypeKind(symbol.Kind), symbol.Line, symbol.Character, symbol.Container, file, s));
            }
        }

        // The declarations grouped by name, a name's (Entries[Starts[n]..Starts[n + 1]]) in the order the results
        // break ties in (path, then line), so that of two declarations of a name, the first ranks first when they
        // score the same; with each declaration's document and kind alongside, for the search's loop.
        var groups = entries.GroupBy(e => e.Name, StringComparer.Ordinal).ToArray();
        var names = new string[groups.Length];
        var masks = new ulong[groups.Length];
        var ascii = new bool[groups.Length];
        var starts = new int[groups.Length + 1];
        var grouped = new Entry[entries.Count];
        var k = 0;
        for (var n = 0; n < groups.Length; n++)
        {
            names[n] = groups[n].Key;
            masks[n] = CharMask(names[n]);
            ascii[n] = Ascii.IsValid(names[n]);
            starts[n] = k;
            foreach (var entry in groups[n].OrderBy(e => e.File.Path, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Line))
            {
                grouped[k++] = entry;
            }
        }

        starts[groups.Length] = k;
        var entrySources = new int[grouped.Length];
        var entryTypes = new bool[grouped.Length];
        for (var e = 0; e < grouped.Length; e++)
        {
            entrySources[e] = grouped[e].Source;
            entryTypes[e] = grouped[e].IsType;
        }

        return new Snapshot(grouped, entrySources, entryTypes, names, masks, ascii, starts, sources, Lookup(sources, f => f.Path), Lookup(sources, f => f.Project), documents, files, loaded, complete, completedAt);

        // The documents' numbers by path or project, case ignored as the ranking compares them.
        static Dictionary<string, int[]> Lookup(IndexedFile[] sources, Func<IndexedFile, string?> key) =>
            Enumerable.Range(0, sources.Length)
                .Where(s => key(sources[s]) is not null)
                .GroupBy(s => key(sources[s])!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Classes, interfaces, structs and enums are types; the rest are members.</summary>
    public static bool IsTypeKind(int kind) => kind is SymbolKind.Class or SymbolKind.Interface or SymbolKind.Struct or SymbolKind.Enum or SymbolKind.Module;

    /// <summary>
    /// The index: the declarations grouped by distinct name (<see cref="Names"/>, each with its <see cref="Masks"/>
    /// bits and whether it's ASCII, its declarations from <see cref="Starts"/>), each declaration's document (by its number in
    /// <see cref="Sources"/>) and whether it's a type; the documents' numbers by path and by project.
    /// </summary>
    private sealed record Snapshot(Entry[] Entries, int[] EntrySource, bool[] EntryIsType, string[] Names, ulong[] Masks, bool[] NameIsAscii, int[] Starts, IndexedFile[] Sources, Dictionary<string, int[]> SourcesByPath, Dictionary<string, int[]> SourcesByProject, IReadOnlyDictionary<string, IndexedFile> Documents, IReadOnlyList<string> Files, bool Loaded, bool Complete, DateTime? CompletedAt)
    {
        public static Snapshot Empty { get; } = new([], [], [], [], [], [], [0], [], [], [], new Dictionary<string, IndexedFile>(), [], false, false, null);
    }

    private sealed record Entry(string Name, int Kind, bool IsType, int Line, int Character, string? Container, IndexedFile File, int Source);
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
