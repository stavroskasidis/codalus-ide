using System.Collections.Concurrent;
using Daedalus.LanguageServices.Protocol;
using Daedalus.Workbench.Projects;

namespace Daedalus.Workbench.Languages;

/// <summary>The diagnostics of one file, as last published by its language service.</summary>
public sealed record FileDiagnostics(string Path, string LanguageId, int? Version, IReadOnlyList<Diagnostic> Items);

/// <summary>
/// Diagnostics of every file, fed by language services (<c>publishDiagnostics</c>). Squiggles read one file; the Error
/// List reads them all. <see cref="Changed"/> may be raised on any thread.
/// </summary>
public sealed class DiagnosticsStore
{
    private readonly ConcurrentDictionary<string, FileDiagnostics> _files = new(SolutionService.PathComparer);

    /// <summary>A file's diagnostics changed (the path).</summary>
    public event EventHandler<string>? Changed;

    public IReadOnlyCollection<FileDiagnostics> All => [.. _files.Values.Where(f => f.Items.Count > 0)];

    public IReadOnlyList<Diagnostic> For(string path) => _files.TryGetValue(path, out var file) ? file.Items : [];

    public FileDiagnostics? Get(string path) => _files.GetValueOrDefault(path);

    public void Set(string path, string languageId, int? version, IReadOnlyList<Diagnostic> items)
    {
        if (items.Count == 0 && !_files.ContainsKey(path))
        {
            return;
        }

        if (items.Count == 0)
        {
            _files.TryRemove(path, out _);
        }
        else
        {
            _files[path] = new FileDiagnostics(path, languageId, version, items);
        }

        Changed?.Invoke(this, path);
    }

    /// <summary>Drops everything a language service published (its host stopped for good, or the solution closed).</summary>
    public void Clear(string? languageId = null)
    {
        foreach (var file in _files.Values.Where(f => languageId is null || f.LanguageId == languageId).ToList())
        {
            if (_files.TryRemove(file.Path, out _))
            {
                Changed?.Invoke(this, file.Path);
            }
        }
    }
}
