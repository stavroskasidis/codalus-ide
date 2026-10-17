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
        if (items.Count == 0)
        {
            if (!_files.TryGetValue(path, out var existing) || !existing.TryRemove(languageId, out _))
            {
                return;
            }

            if (existing.IsEmpty)
            {
                _files.TryRemove(path, out _);
            }
        }
        else
        {
            _files.GetOrAdd(path, _ => new ConcurrentDictionary<string, FileDiagnostics>(StringComparer.Ordinal))[languageId] = new FileDiagnostics(path, languageId, version, items);
        }

        Changed?.Invoke(this, path);
    }

    /// <summary>Drops everything a language service published (its host stopped for good), or everything.</summary>
    public void Clear(string? languageId = null)
    {
        foreach (var (path, sources) in _files.ToList())
        {
            var removed = languageId is null ? !sources.IsEmpty : sources.TryRemove(languageId, out _);
            if (languageId is null || sources.IsEmpty)
            {
                _files.TryRemove(path, out _);
            }

            if (removed)
            {
                Changed?.Invoke(this, path);
            }
        }
    }
}
