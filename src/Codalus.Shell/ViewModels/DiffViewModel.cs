using Codalus.Shell.Docking;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Git;

namespace Codalus.Shell.ViewModels;

/// <summary>
/// A diff document (ADR 0008, #86): two versions of a file, aligned row by row, side by side or inline, each side
/// colored like the editor: by its language's grammar, and by the language service where the side is the open
/// document's text.
/// </summary>
public sealed class DiffViewModel : ObservableObject
{
    private readonly GitService? _git;
    private bool _sideBySide = true;
    private string _summary = string.Empty;
    private int _colorsVersion;
    private readonly Dictionary<int, (IReadOnlyList<(int Start, int Length)> Old, IReadOnlyList<(int Start, int Length)> New)> _words = [];

    public DiffViewModel(GitService git, string path, bool staged)
    {
        _git = git;
        Path = path;
        Staged = staged;
    }

    /// <summary>Two files, side by side (<c>codalus diff left right</c>): no Git, no staging.</summary>
    public DiffViewModel(string leftPath, string rightPath)
    {
        LeftPath = leftPath;
        Path = rightPath;
    }

    /// <summary>
    /// Two versions of a file read by <paramref name="oldText"/> and <paramref name="newText"/> (revisions, M13: a commit
    /// against its parent, a branch against the working tree…). <paramref name="key"/> identifies the document.
    /// </summary>
    public DiffViewModel(string path, string key, string oldTitle, Func<Task<string>> oldText, string newTitle, Func<Task<string>> newText)
    {
        Path = path;
        Key = key;
        _oldTitle = oldTitle;
        _newTitle = newTitle;
        _oldProvider = oldText;
        _newProvider = newText;
    }

    private readonly string? _oldTitle;
    private readonly string? _newTitle;
    private readonly Func<Task<string>>? _oldProvider;
    private readonly Func<Task<string>>? _newProvider;

    /// <summary>The shell's services: colors, languages and open documents (null in a test: plain text).</summary>
    public DiffServices? Services { get; set; }

    /// <summary>A revision comparison's document key; null otherwise.</summary>
    public string? Key { get; }

    public bool IsRevisionComparison => _oldProvider is not null;

    public string Path { get; }

    /// <summary>The left file of a file comparison; null for Git diffs.</summary>
    public string? LeftPath { get; }

    public bool IsFileComparison => LeftPath is not null;

    public bool Staged { get; }

    public FileDiff? Diff { get; private set; }

    public string OldText { get; private set; } = string.Empty;

    public string NewText { get; private set; } = string.Empty;

    public IReadOnlyList<string> OldLines { get; private set; } = [];

    public IReadOnlyList<string> NewLines { get; private set; } = [];

    /// <summary>Aligned rows of both texts (side by side, and in that order for the inline view).</summary>
    public IReadOnlyList<DiffRow> Rows { get; private set; } = [];

    /// <summary>The old file's colors (the grammar's), or null until they're ready or without a grammar.</summary>
    public SourceTokens? OldColors { get; private set; }

    /// <summary>The new file's colors: the language service's where it is the open document, else the grammar's.</summary>
    public SourceTokens? NewColors { get; private set; }

    /// <summary>Which side's text is the open document's: its colors are the language service's (null for neither).</summary>
    public bool? OpenSideIsOld { get; private set; }

    public bool SideBySide
    {
        get => _sideBySide;
        set
        {
            if (SetField(ref _sideBySide, value))
            {
                OnPropertyChanged(nameof(Inline));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool Inline
    {
        get => !_sideBySide;
        set => SideBySide = !value;
    }

    public string Summary
    {
        get => _summary;
        private set => SetField(ref _summary, value);
    }

    public string OldTitle => _oldTitle ?? LeftPath ?? (Staged ? "HEAD" : "Index");

    public string NewTitle => _newTitle ?? (IsFileComparison ? Path : Staged ? "Index (staged)" : "Working tree");

    public bool CanStage => !Staged && !IsFileComparison && !IsRevisionComparison;

    public bool CanUnstage => Staged;

    /// <summary>The texts or rows changed; views rebuild.</summary>
    public event EventHandler? Changed;

    /// <summary>The sides' colors changed; views draw again.</summary>
    public event EventHandler? ColorsChanged;

    /// <summary>The old file's path: the left file of a file comparison, else the file.</summary>
    public string OldPath => LeftPath ?? Path;

    public async Task LoadAsync()
    {
        if (_oldProvider is not null && _newProvider is not null)
        {
            await LoadTextsAsync(await _oldProvider().ConfigureAwait(true), await _newProvider().ConfigureAwait(true)).ConfigureAwait(true);
            return;
        }

        if (_git is null)
        {
            await LoadFilesAsync().ConfigureAwait(true);
            return;
        }

        var oldText = await _git.ShowAsync(Path, Staged ? "HEAD" : ":").ConfigureAwait(true) ?? string.Empty;
        string newText;
        if (Staged)
        {
            newText = await _git.ShowAsync(Path, ":").ConfigureAwait(true) ?? string.Empty;
        }
        else
        {
            try
            {
                newText = File.Exists(Path) ? await File.ReadAllTextAsync(Path).ConfigureAwait(true) : string.Empty;
            }
            catch (IOException)
            {
                newText = string.Empty;
            }
        }

        Diff = await _git.DiffAsync(Path, Staged).ConfigureAwait(true);
        await SetTextsAsync(oldText, newText).ConfigureAwait(true);
        var hunks = Diff?.Hunks.Count ?? 0;
        Summary = Diff is null ? "No changes." : Diff.IsBinary ? "Binary file." : $"{hunks} change{(hunks == 1 ? string.Empty : "s")}: +{Diff.Hunks.Sum(h => h.Added)} −{Diff.Hunks.Sum(h => h.Removed)}";
        Changed?.Invoke(this, EventArgs.Empty);
        _ = ColorAsync();
    }

    private async Task LoadFilesAsync()
    {
        static async Task<string> ReadAsync(string path)
        {
            try
            {
                return File.Exists(path) ? await File.ReadAllTextAsync(path).ConfigureAwait(false) : string.Empty;
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }

        await LoadTextsAsync(await ReadAsync(LeftPath!).ConfigureAwait(true), await ReadAsync(Path).ConfigureAwait(true)).ConfigureAwait(true);
    }

    private async Task LoadTextsAsync(string oldText, string newText)
    {
        await SetTextsAsync(oldText, newText).ConfigureAwait(true);
        var added = Rows.Count(r => r.NewLine is not null && r.IsChange);
        var removed = Rows.Count(r => r.OldLine is not null && r.IsChange);
        Summary = added + removed == 0 ? "The files are identical." : $"+{added} −{removed}";
        Changed?.Invoke(this, EventArgs.Empty);
        _ = ColorAsync();
    }

    private async Task SetTextsAsync(string oldText, string newText)
    {
        var oldLines = LineDiff.Lines(oldText);
        var newLines = LineDiff.Lines(newText);
        var rows = await Task.Run(() => DiffRows.Align(LineDiff.Diff(oldLines, newLines))).ConfigureAwait(true);
        if (oldText != OldText)
        {
            OldColors = null;
        }

        if (newText != NewText)
        {
            NewColors = null;
        }

        OldText = oldText;
        NewText = newText;
        OldLines = oldLines;
        NewLines = newLines;
        Rows = rows;
        _words.Clear();
    }

    /// <summary>
    /// The changed words of a row that pairs a removed line with the added line replacing it (D45 question 14: a
    /// character diff widened to whole words); none for other rows.
    /// </summary>
    public (IReadOnlyList<(int Start, int Length)> Old, IReadOnlyList<(int Start, int Length)> New) WordRanges(int row)
    {
        if ((uint)row >= (uint)Rows.Count || Rows[row] is not { Kind: '~', OldLine: { } oldLine, NewLine: { } newLine })
        {
            return ([], []);
        }

        if (!_words.TryGetValue(row, out var ranges))
        {
            _words[row] = ranges = WordDiff.Ranges(OldLines[oldLine - 1], NewLines[newLine - 1]);
        }

        return ranges;
    }

    /// <summary>
    /// Colors both sides, off the UI thread: each file's text by its language's grammar, then the side that is the open
    /// document's text by its language service (#86). A reload's coloring replaces one still running.
    /// </summary>
    public async Task ColorAsync()
    {
        if (Services is not { } services)
        {
            return;
        }

        var version = ++_colorsVersion;
        var token = CancellationToken.None;
        var (oldText, newText) = (OldText, NewText);
        var themeId = services.Editor.Theme.Id;
        try
        {
            var oldColors = await services.Tokenizer.TokenizeAsync(oldText, services.LanguageOf(OldPath), themeId, token).ConfigureAwait(true);
            var newColors = await services.Tokenizer.TokenizeAsync(newText, services.LanguageOf(Path), themeId, token).ConfigureAwait(true);
            if (version != _colorsVersion)
            {
                return; // a reload's coloring is on its way
            }

            (OldColors, NewColors, OpenSideIsOld) = (oldColors, newColors, null);
            ColorsChanged?.Invoke(this, EventArgs.Empty);

            // The open document's side: its language service's tokens, as the editor colors it.
            foreach (var (isOld, text) in new[] { (false, newText), (true, oldText) })
            {
                if (text.Length == 0 || await services.SemanticTokensAsync(isOld ? OldPath : Path, text, token).ConfigureAwait(true) is not { } data)
                {
                    continue;
                }

                if (version != _colorsVersion)
                {
                    return;
                }

                var semantic = SourceTokens.FromSemantic(data, (isOld ? OldLines : NewLines).Count);
                if (isOld)
                {
                    OldColors = semantic;
                }
                else
                {
                    NewColors = semantic;
                }

                OpenSideIsOld = isOld;
                ColorsChanged?.Invoke(this, EventArgs.Empty);
                break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            // Colors are a nicety: a language service that fails leaves the grammar's.
        }
    }

    /// <summary>The git hunk that covers a new-side (or, for removals, old-side) line.</summary>
    public DiffHunk? HunkAt(int? newLine, int? oldLine)
    {
        if (Diff is null)
        {
            return null;
        }

        foreach (var hunk in Diff.Hunks)
        {
            if (newLine is { } n && n >= hunk.NewStart && n < hunk.NewStart + Math.Max(1, hunk.NewCount))
            {
                return hunk;
            }

            if (oldLine is { } o && o >= hunk.OldStart && o < hunk.OldStart + Math.Max(1, hunk.OldCount))
            {
                return hunk;
            }
        }

        return null;
    }

    /// <summary>The first changed row after (or before) <paramref name="row"/>, for next/previous change.</summary>
    public int? NextChange(int row, int direction)
    {
        for (var i = row + direction; i >= 0 && i < Rows.Count; i += direction)
        {
            if (Rows[i].Kind != ' ' && (i - direction < 0 || i - direction >= Rows.Count || Rows[i - direction].Kind == ' '))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// Stages, unstages or discards only the changed lines in the given rows (per-line staging, M13): a removal is
    /// chosen by its old line, an addition by its new line.
    /// </summary>
    public async Task ApplyToLinesAsync(IReadOnlyCollection<int> rows, string action)
    {
        if (Diff is null || _git is null)
        {
            return;
        }

        var oldLines = rows.Where(r => r >= 0 && r < Rows.Count).Select(r => Rows[r].OldLine).OfType<int>().ToHashSet();
        var newLines = rows.Where(r => r >= 0 && r < Rows.Count).Select(r => Rows[r].NewLine).OfType<int>().ToHashSet();
        foreach (var hunk in Diff.Hunks)
        {
            var chosen = new HashSet<int>();
            int oldNo = hunk.OldStart, newNo = hunk.NewStart;
            for (var i = 0; i < hunk.Lines.Count; i++)
            {
                switch (hunk.Lines[i].Kind)
                {
                    case ' ':
                        oldNo++;
                        newNo++;
                        break;
                    case '-':
                        if (oldLines.Contains(oldNo++))
                        {
                            chosen.Add(i);
                        }

                        break;
                    case '+':
                        if (newLines.Contains(newNo++))
                        {
                            chosen.Add(i);
                        }

                        break;
                }
            }

            if (chosen.Count == 0)
            {
                continue;
            }

            switch (action)
            {
                case "stage":
                    await _git.StageLinesAsync(Diff, hunk, chosen).ConfigureAwait(true);
                    break;
                case "unstage":
                    await _git.UnstageLinesAsync(Diff, hunk, chosen).ConfigureAwait(true);
                    break;
                case "discard":
                    await _git.DiscardLinesAsync(Diff, hunk, chosen).ConfigureAwait(true);
                    break;
            }

            break; // the diff changed: one hunk per action
        }

        await LoadAsync().ConfigureAwait(true);
    }

    public async Task ApplyToHunkAsync(DiffHunk hunk, string action)
    {
        if (Diff is null || _git is null)
        {
            return;
        }

        switch (action)
        {
            case "stage":
                await _git.StageHunkAsync(Diff, hunk).ConfigureAwait(true);
                break;
            case "unstage":
                await _git.UnstageHunkAsync(Diff, hunk).ConfigureAwait(true);
                break;
            case "discard":
                await _git.DiscardHunkAsync(Diff, hunk).ConfigureAwait(true);
                break;
        }

        await LoadAsync().ConfigureAwait(true);
    }
}

/// <summary>A diff document tab.</summary>
public sealed class DiffDocument : ShellDocument
{
    public DiffDocument(DiffViewModel viewModel, EditorFont font, DiffServices? services = null)
    {
        ViewModel = viewModel;
        Font = font;
        viewModel.Services ??= services;
        Id = viewModel.Key is { } key ? "diff:revisions:" + key : viewModel.LeftPath is { } left ? IdFor(left, viewModel.Path) : IdFor(viewModel.Path, viewModel.Staged);
        Title = viewModel.IsRevisionComparison
            ? $"{System.IO.Path.GetFileName(viewModel.Path)} ({viewModel.OldTitle} ↔ {viewModel.NewTitle})"
            : viewModel.LeftPath is { } leftPath
                ? $"{System.IO.Path.GetFileName(leftPath)} ↔ {System.IO.Path.GetFileName(viewModel.Path)}"
                : $"{System.IO.Path.GetFileName(viewModel.Path)} ({(viewModel.Staged ? "Staged" : "Working Tree")})";
    }

    public static string IdFor(string path, bool staged) => $"diff:{(staged ? "staged" : "worktree")}:{path}";

    public static string IdFor(string left, string right) => $"diff:files:{left}|{right}";

    public override string Kind => "diff";

    public DiffViewModel ViewModel { get; }

    public EditorFont Font { get; }
}
