using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Shell.Infrastructure;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>A comment task: its token (TODO, HACK…), its text, and where it is.</summary>
public sealed record TaskRow(string Token, string Text, string Path, int Line)
{
    public string File => System.IO.Path.GetFileName(Path);

    public string AccessibleName => $"{Token}: {Text}, {File} line {Line}";
}

/// <summary>
/// The Task List (M12b): comments starting with a task token (setting <c>taskList.tokens</c>, by default <c>TODO</c>,
/// <c>HACK</c>, <c>UNDONE</c>) in the solution's files, unsaved text included. Scans on the thread pool when it opens
/// and on Refresh; a filter narrows by text or file.
/// </summary>
public sealed class TaskListViewModel(Func<IReadOnlyList<string>> files, Func<string, string?> openText, Func<IReadOnlyList<string>> tokens, Func<string, Position, Task> open) : ObservableObject, IDisposable
{
    public const string ToolWindowId = "taskList";
    private const int MaxTasks = 5000;
    private static readonly HashSet<string> s_textExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".vb", ".fs", ".fsx", ".csx", ".razor", ".cshtml", ".xaml", ".axaml", ".xml", ".csproj", ".vbproj", ".fsproj", ".props", ".targets",
        ".js", ".ts", ".tsx", ".jsx", ".css", ".scss", ".html", ".json", ".jsonc", ".yml", ".yaml", ".ps1", ".sh", ".py", ".sql", ".md",
    };

    private readonly List<TaskRow> _all = [];
    private CancellationTokenSource? _scan;
    private string _filter = string.Empty;
    private string _summary = string.Empty;
    private bool _scanned;

    public ObservableCollection<TaskRow> Rows { get; } = [];

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value ?? string.Empty))
            {
                Apply();
            }
        }
    }

    public string Summary
    {
        get => _summary;
        private set => SetField(ref _summary, value);
    }

    /// <summary>Scans once, when the window first shows.</summary>
    public void EnsureScanned()
    {
        if (!_scanned)
        {
            _ = RefreshAsync();
        }
    }

    public async Task RefreshAsync()
    {
        _scanned = true;
        _scan?.Cancel();
        _scan?.Dispose();
        var cts = _scan = new CancellationTokenSource();
        var paths = files().Where(f => s_textExtensions.Contains(System.IO.Path.GetExtension(f))).ToList();
        var texts = paths.Select(p => (p, openText(p))).Where(x => x.Item2 is not null).ToDictionary(x => x.p, x => x.Item2!, DaedalusStudio.Workbench.Projects.SolutionService.PathComparer);
        var words = tokens().Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => Regex.Escape(t.Trim())).ToList();
        if (words.Count == 0)
        {
            _all.Clear();
            Apply();
            return;
        }

        // A comment marker, then a token as a whole word: // TODO: x, /* HACK x, # UNDONE x, <!-- TODO x, ' TODO x.
        var regex = new Regex(@"(?://+|/\*+|#|<!--|'|\*)\s*(" + string.Join('|', words) + @")\b[:\s-]*(.*)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        Summary = $"Scanning {paths.Count} files…";
        try
        {
            var found = await Task.Run(() => Scan(paths, texts, regex, cts.Token), cts.Token).ConfigureAwait(true);
            _all.Clear();
            _all.AddRange(found);
            Apply();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static List<TaskRow> Scan(List<string> paths, Dictionary<string, string> texts, Regex regex, CancellationToken cancellationToken)
    {
        var rows = new List<TaskRow>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string text;
            try
            {
                text = texts.TryGetValue(path, out var open) ? open : File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var line = 0;
            foreach (var raw in text.Split('\n'))
            {
                line++;
                if (regex.Match(raw) is { Success: true } match)
                {
                    var token = match.Groups[1].Value.ToUpperInvariant();
                    var description = match.Groups[2].Value.Replace("*/", string.Empty, StringComparison.Ordinal).Replace("-->", string.Empty, StringComparison.Ordinal).Trim();
                    rows.Add(new TaskRow(token, description.Length == 0 ? "(no description)" : description, path, line));
                    if (rows.Count >= MaxTasks)
                    {
                        return rows;
                    }
                }
            }
        }

        return rows;
    }

    private void Apply()
    {
        Rows.Clear();
        foreach (var row in _all.Where(r => _filter.Length == 0 || r.Text.Contains(_filter, StringComparison.OrdinalIgnoreCase) || r.File.Contains(_filter, StringComparison.OrdinalIgnoreCase) || r.Token.Equals(_filter, StringComparison.OrdinalIgnoreCase)))
        {
            Rows.Add(row);
        }

        Summary = _all.Count == 0 ? "No tasks in the solution's files." : $"{_all.Count} task(s){(Rows.Count < _all.Count ? $", {Rows.Count} shown" : string.Empty)}.";
    }

    public Task OpenAsync(TaskRow row) => open(row.Path, new Position(row.Line - 1, 0));

    public void Dispose()
    {
        _scan?.Cancel();
        _scan?.Dispose();
    }
}
