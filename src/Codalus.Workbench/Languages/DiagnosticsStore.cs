using System.Collections.Concurrent;
using Codalus.LanguageServices.Protocol;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Languages;

/// <summary>The diagnostics of one file, as last published by one language service.</summary>
public sealed record FileDiagnostics(string Path, string LanguageId, int? Version, IReadOnlyList<Diagnostic> Items);

/// <summary>
/// Diagnostics of every file, fed by language services (<c>publishDiagnostics</c>, or pulled). A file can have several
/// sources: its language service and its linters (ESLint, ADR 0027), each replacing only its own. Squiggles read one
/// file; the Error List reads them all. <see cref="Changed"/> may be raised on any thread.
/// </summary>
public sealed class DiagnosticsStore
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, FileDiagnostics>> _files = new(SolutionService.PathComparer);

    // The errors and warnings of every file, kept as they change: the status bar counted every diagnostic of the
    // solution again at each change of one file's. Changes and their counts are made together under the lock.
    private readonly Lock _gate = new();
    private int _errors;
    private int _warnings;

    /// <summary>Every file's errors and warnings, of all sources.</summary>
    public (int Errors, int Warnings) Counts
    {
        get
        {
            lock (_gate)
            {
                return (_errors, _warnings);
            }
        }
    }

    private void Count(FileDiagnostics? file, int sign)
    {
        if (file is null)
        {
            return;
        }

        foreach (var item in file.Items)
        {
            _errors += item.Severity == DiagnosticSeverity.Error ? sign : 0;
            _warnings += item.Severity == DiagnosticSeverity.Warning ? sign : 0;
        }
    }

    /// <summary>A file's diagnostics changed (the path).</summary>
    public event EventHandler<string>? Changed;

    /// <summary>Every source's diagnostics of every file (a file appears once for each source that has some).</summary>
    public IReadOnlyCollection<FileDiagnostics> All => [.. _files.Values.SelectMany(sources => sources.Values).Where(f => f.Items.Count > 0)];

    /// <summary>A file's diagnostics, of all its sources.</summary>
    public IReadOnlyList<Diagnostic> For(string path)
    {
        if (!_files.TryGetValue(path, out var sources))
        {
            return [];
        }

        // One snapshot: a source can be added or removed on another thread between a count and a read of its values.
        var files = sources.Values.ToArray();
        return files.Length switch
        {
            0 => [],
            1 => files[0].Items,
            _ => [.. files.OrderBy(f => f.LanguageId, StringComparer.Ordinal).SelectMany(f => f.Items)],
        };
    }

    /// <summary>A file's diagnostics from one source.</summary>
    public FileDiagnostics? Get(string path, string languageId) =>
        _files.TryGetValue(path, out var sources) ? sources.GetValueOrDefault(languageId) : null;

    /// <summary>Replaces what <paramref name="languageId"/> (a language service) reported for <paramref name="path"/>.</summary>
    public void Set(string path, string languageId, int? version, IReadOnlyList<Diagnostic> items)
    {
        lock (_gate)
        {
            if (items.Count == 0)
            {
                if (!_files.TryGetValue(path, out var existing) || !existing.TryRemove(languageId, out var removed))
                {
                    return;
                }

                Count(removed, -1);
                if (existing.IsEmpty)
                {
                    _files.TryRemove(path, out _);
                }
            }
            else
            {
                var sources = _files.GetOrAdd(path, _ => new ConcurrentDictionary<string, FileDiagnostics>(StringComparer.Ordinal));
                var file = new FileDiagnostics(path, languageId, version, items);
                Count(sources.GetValueOrDefault(languageId), -1);
                sources[languageId] = file;
                Count(file, 1);
            }
        }

        Changed?.Invoke(this, path);
    }

    /// <summary>Drops everything a language service published (its host stopped for good), or everything.</summary>
    public void Clear(string? languageId = null)
    {
        var changed = new List<string>();
        lock (_gate)
        {
            foreach (var (path, sources) in _files.ToList())
            {
                bool removed;
                if (languageId is null)
                {
                    removed = !sources.IsEmpty;
                    foreach (var file in sources.Values)
                    {
                        Count(file, -1);
                    }
                }
                else
                {
                    removed = sources.TryRemove(languageId, out var file);
                    Count(file, -1);
                }

                if (languageId is null || sources.IsEmpty)
                {
                    _files.TryRemove(path, out _);
                }

                if (removed)
                {
                    changed.Add(path);
                }
            }
        }

        foreach (var path in changed)
        {
            Changed?.Invoke(this, path);
        }
    }
}
