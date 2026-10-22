using Codalus.Shell.Docking;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Git;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>One side of a diff document: its file, what it is (Index, Working tree, a revision), and how it is stored.</summary>
public sealed record DiffSide(string Path, string Label, string? Note, string Indentation, string LineEnding, string Encoding)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// A diff document (ADR 0008, #86, D45): two versions of a file, aligned row by row, side by side or inline, each side
/// colored like the editor: by its language's grammar, and by the language service where the side is the open
/// document's text. A change is a run of changed rows.
/// </summary>
public sealed class DiffViewModel : ObservableObject
{
    /// <summary>The <c>git.diff.layout</c> values.</summary>
    public const string LayoutSideBySide = "sideBySide";

    public const string LayoutInline = "inline";

    private readonly GitService? _git;
    private bool _sideBySide = true;
    private string _summary = string.Empty;
    private double _zoom = 1;
    private int _colorsVersion;
    private readonly Dictionary<int, (IReadOnlyList<(int Start, int Length)> Old, IReadOnlyList<(int Start, int Length)> New)> _words = [];

    public DiffViewModel(GitService git, string path, bool staged)
    {
        _git = git;
        Path = path;
        Staged = staged;
        OldPath = path;
        (OldSide, NewSide) = (Blank(path), Blank(path));
    }

    /// <summary>Two files, side by side (<c>codalus diff left right</c>): no Git, no staging.</summary>
    public DiffViewModel(string leftPath, string rightPath)
    {
        LeftPath = leftPath;
        Path = rightPath;
        OldPath = leftPath;
        (OldSide, NewSide) = (Blank(leftPath), Blank(rightPath));
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
        OldPath = path;
        (OldSide, NewSide) = (Blank(path), Blank(path));
    }

    private static DiffSide Blank(string path) => new(path, string.Empty, null, "SPC", "LF", "UTF-8");

    private readonly string? _oldTitle;
    private readonly string? _newTitle;
    private readonly Func<Task<string>>? _oldProvider;
    private readonly Func<Task<string>>? _newProvider;

    /// <summary>The shell's services: colors, languages and open documents (null in a test: plain text).</summary>
    public DiffServices? Services { get; set; }

    /// <summary>A command's key as menus show it (<c>F8</c>), for tooltips; null when unbound.</summary>
    public Func<string, string?>? ShortcutFor { get; set; }

    /// <summary>Saves the layout to the settings (the gear menu changed it).</summary>
    public Action<DiffViewModel>? SaveLayout { get; set; }

    /// <summary>Opens a file at a 1-based line (Open in the gear menu and the context menu).</summary>
    public Func<string, int, Task>? OpenFile { get; set; }

    /// <summary>A revision comparison's document key; null otherwise.</summary>
    public string? Key { get; }

    public bool IsRevisionComparison => _oldProvider is not null;

    public string Path { get; }

    /// <summary>The left file of a file comparison; null for Git diffs.</summary>
    public string? LeftPath { get; }

    public bool IsFileComparison => LeftPath is not null;

    public bool Staged { get; }

    /// <summary>Whether it compares two versions in Git (staging applies), rather than files or revisions.</summary>
    public bool IsGitDiff => _git is not null;

    public FileDiff? Diff { get; private set; }

    public string OldText { get; private set; } = string.Empty;

    public string NewText { get; private set; } = string.Empty;

    public IReadOnlyList<string> OldLines { get; private set; } = [];

    public IReadOnlyList<string> NewLines { get; private set; } = [];

    /// <summary>Aligned rows of both texts (side by side, and in that order for the inline view).</summary>
    public IReadOnlyList<DiffRow> Rows { get; private set; } = [];

    /// <summary>The changes: runs of changed rows.</summary>
    public IReadOnlyList<DiffChange> Changes { get; private set; } = [];

    /// <summary>Whether the texts have been read once (the view shows the first change then, and only then).</summary>
    public bool IsLoaded { get; private set; }

    public DiffSide OldSide { get; private set; }

    public DiffSide NewSide { get; private set; }

    /// <summary>Lines removed (the old side's changed lines) and added (the new side's).</summary>
    public int RemovedLines { get; private set; }

    public int AddedLines { get; private set; }

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
                SaveLayout?.Invoke(this);
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool Inline
    {
        get => !_sideBySide;
        set => SideBySide = !value;
    }

    /// <summary>The <c>git.diff.layout</c> value.</summary>
    public string Layout => _sideBySide ? LayoutSideBySide : LayoutInline;

    /// <summary>The text size of both sides, as a factor of the editor font's (the status bar's zoom box).</summary>
    public double Zoom
    {
        get => _zoom;
        set => SetField(ref _zoom, Math.Clamp(value, 0.25, 4));
    }

    /// <summary>The left side's share of the width, kept while the document is open (the splitter; double-click: half).</summary>
    public double SplitRatio { get; set; } = 0.5;

    /// <summary>Where each pane's caret and scroll position were, kept across tab switches (the view saves them).</summary>
    public Dictionary<DiffPaneSide, (int Row, int Column, Avalonia.Vector Offset)> PaneState { get; } = [];

    public string Summary
    {
        get => _summary;
        private set => SetField(ref _summary, value);
    }

    public string OldTitle => _oldTitle ?? LeftPath ?? (Staged ? "HEAD" : "Index");

    public string NewTitle => _newTitle ?? (IsFileComparison ? Path : Staged ? "Index (staged)" : "Working tree");

    public bool CanStage => !Staged && IsGitDiff;

    public bool CanUnstage => Staged;

    /// <summary>The texts or rows changed; views rebuild.</summary>
    public event EventHandler? Changed;

    /// <summary>The sides' colors changed; views draw again.</summary>
    public event EventHandler? ColorsChanged;

    /// <summary>The path of the old side's file: the left file, a staged rename's original, else the file.</summary>
    public string OldPath { get; private set; }

    /// <summary>Sets the layout from the <c>git.diff.layout</c> setting, without saving it back.</summary>
    public void LoadLayout(string layout) => _sideBySide = layout != LayoutInline;

    public async Task LoadAsync()
    {
        OldPath = LeftPath ?? Path;
        if (_oldProvider is not null && _newProvider is not null)
        {
            var oldText = await _oldProvider().ConfigureAwait(true);
            var newText = await _newProvider().ConfigureAwait(true);
            await SetTextsAsync(Text(oldText), Text(newText)).ConfigureAwait(true);
            SetSides(_oldTitle ?? string.Empty, null, _newTitle ?? string.Empty, null);
            Summary = AddedLines + RemovedLines == 0 ? "The files are identical." : $"+{AddedLines} −{RemovedLines}";
            Loaded();
            return;
        }

        if (_git is null)
        {
            var oldFile = await ReadAsync(LeftPath!).ConfigureAwait(true);
            var newFile = await ReadAsync(Path).ConfigureAwait(true);
            await SetTextsAsync(oldFile, newFile).ConfigureAwait(true);
            SetSides(Folder(LeftPath!), null, Folder(Path), null);
            Summary = AddedLines + RemovedLines == 0 ? "The files are identical." : $"+{AddedLines} −{RemovedLines}";
            Loaded();
            return;
        }

        var status = _git.Status?.Files.FirstOrDefault(f => Workbench.Projects.SolutionService.PathComparer.Equals(f.Path, Path));
        if (Staged && status?.OriginalPath is { } original)
        {
            OldPath = original; // a staged rename: HEAD has the file under its old name
        }

        var oldBytes = await _git.ShowBytesAsync(OldPath, Staged ? "HEAD" : ":").ConfigureAwait(true);
        var old = oldBytes is null ? Text(string.Empty) : TextFiles.Decode(oldBytes);
        (string Text, TextFileFormat Format) now;
        if (Staged)
        {
            var newBytes = await _git.ShowBytesAsync(Path, ":").ConfigureAwait(true);
            now = newBytes is null ? Text(string.Empty) : TextFiles.Decode(newBytes);
        }
        else
        {
            now = await ReadAsync(Path).ConfigureAwait(true);
        }

        Diff = await _git.DiffAsync(Path, Staged).ConfigureAwait(true);
        await SetTextsAsync(old, now).ConfigureAwait(true);
        string? newNote = null;
        if (Staged)
        {
            newNote = OldPath != Path
                ? Localizer.Format("diff.renamedFrom", "renamed from {0}", System.IO.Path.GetFileName(OldPath))
                : status is { WorkTree: not '.' } ? Localizer.Get("diff.workingTreeHasMoreEdits", "the working tree has more edits") : null;
        }
        else if (!File.Exists(Path))
        {
            newNote = Localizer.Get("diff.deleted", "deleted");
        }

        var index = Localizer.Get("diff.index", "Index");
        SetSides(Staged ? "HEAD" : index, null, Staged ? index : Localizer.Get("diff.workingTree", "Working tree"), newNote);
        var changes = Changes.Count;
        Summary = Diff is null ? "No changes." : Diff.IsBinary ? "Binary file." : $"{changes} change{(changes == 1 ? string.Empty : "s")}: +{AddedLines} −{RemovedLines}";
        Loaded();
    }

    private void Loaded()
    {
        Changed?.Invoke(this, EventArgs.Empty);
        IsLoaded = true;
        _ = ColorAsync();
    }

    private static (string Text, TextFileFormat Format) Text(string text) =>
        (text, new TextFileFormat(new System.Text.UTF8Encoding(false), false, TextFiles.DetectLineEnding(text)));

    private static async Task<(string Text, TextFileFormat Format)> ReadAsync(string path)
    {
        try
        {
            return File.Exists(path) ? await TextFiles.ReadAsync(path, CancellationToken.None).ConfigureAwait(true) : Text(string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Text(string.Empty);
        }
    }

    /// <summary>A file comparison's header: the file's folder.</summary>
    private static string Folder(string path) => System.IO.Path.GetDirectoryName(path) ?? string.Empty;

    private (TextFileFormat Old, TextFileFormat New) _formats = (TextFileFormat.Default, TextFileFormat.Default);

    private async Task SetTextsAsync((string Text, TextFileFormat Format) old, (string Text, TextFileFormat Format) now)
    {
        var oldLines = LineDiff.Lines(old.Text);
        var newLines = LineDiff.Lines(now.Text);
        var rows = await Task.Run(() => DiffRows.Align(LineDiff.Diff(oldLines, newLines))).ConfigureAwait(true);
        if (old.Text != OldText)
        {
            OldColors = null;
        }

        if (now.Text != NewText)
        {
            NewColors = null;
        }

        OldText = old.Text;
        NewText = now.Text;
        OldLines = oldLines;
        NewLines = newLines;
        Rows = rows;
        Changes = DiffRows.Changes(rows);
        RemovedLines = rows.Count(r => r.IsChange && r.OldLine is not null);
        AddedLines = rows.Count(r => r.IsChange && r.NewLine is not null);
        _formats = (old.Format, now.Format);
        _words.Clear();
    }

    private void SetSides(string oldLabel, string? oldNote, string newLabel, string? newNote)
    {
        OldSide = new DiffSide(OldPath, oldLabel, oldNote, TextFiles.IndentationName(OldText), _formats.Old.LineEndingName, _formats.Old.EncodingName);
        NewSide = new DiffSide(Path, newLabel, newNote, TextFiles.IndentationName(NewText), _formats.New.LineEndingName, _formats.New.EncodingName);
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

            (OldColors, NewColors, OpenSideIsOld) = (oldColors, newColors, OpenSide(services, oldText, newText));
            ColorsChanged?.Invoke(this, EventArgs.Empty);

            // The open document's side: its language service's tokens, as the editor colors it.
            if (OpenSideIsOld is { } isOld && await services.SemanticTokensAsync(isOld ? OldPath : Path, isOld ? oldText : newText, token).ConfigureAwait(true) is { } data
                && version == _colorsVersion)
            {
                var semantic = SourceTokens.FromSemantic(data, (isOld ? OldLines : NewLines).Count);
                if (isOld)
                {
                    OldColors = semantic;
                }
                else
                {
                    NewColors = semantic;
                }

                ColorsChanged?.Invoke(this, EventArgs.Empty);
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

    /// <summary>
    /// The side whose text is the open document's (D45 question 1): the new side when it is the file's open text, else
    /// the old side when it is (a revision equal to the buffer); null when neither is, or the file isn't open.
    /// </summary>
    private bool? OpenSide(DiffServices services, string oldText, string newText) =>
        newText.Length > 0 && services.IsOpenText(Path, newText) ? false
        : oldText.Length > 0 && services.IsOpenText(OldPath, oldText) ? true
        : null;

    /// <summary>The index of the change a row is in, or -1.</summary>
    public int ChangeIndexAt(int row)
    {
        for (var i = 0; i < Changes.Count; i++)
        {
            if (Changes[i].Contains(row))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The first row of the next (or previous) change from <paramref name="row"/>, for F8 and Shift+F8.</summary>
    public int? NextChange(int row, int direction)
    {
        if (direction > 0)
        {
            foreach (var change in Changes)
            {
                if (change.FirstRow > row)
                {
                    return change.FirstRow;
                }
            }

            return null;
        }

        var current = ChangeIndexAt(row);
        for (var i = Changes.Count - 1; i >= 0; i--)
        {
            if (Changes[i].FirstRow < row && i != current)
            {
                return Changes[i].FirstRow;
            }
        }

        return null;
    }

    /// <summary>The toolbar's count: "1 change", "Change 3 of 4" with the caret in a change, "4 changes".</summary>
    public string CountText(int caretRow)
    {
        if (Changes.Count == 0)
        {
            return Localizer.Get("diff.noChanges", "No changes");
        }

        if (Changes.Count == 1)
        {
            return Localizer.Get("diff.oneChange", "1 change");
        }

        var index = ChangeIndexAt(caretRow);
        return index >= 0
            ? Localizer.Format("diff.changeOf", "Change {0} of {1}", index + 1, Changes.Count)
            : Localizer.Format("diff.changes", "{0} changes", Changes.Count);
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

/// <summary>
/// A diff document tab, titled by what it compares (D45 question 9): "Startup.cs (Working Tree)", "Startup.cs (Staged)",
/// "Startup.cs (3f2a1c9 → 8b7e4d2)", "a.json ↔ b.json" for two files; with the compare icon.
/// </summary>
public sealed class DiffDocument : ShellDocument
{
    public DiffDocument(DiffViewModel viewModel, EditorFont font, DiffServices? services = null)
    {
        ViewModel = viewModel;
        Font = font;
        viewModel.Services ??= services;
        Id = viewModel.Key is { } key ? "diff:revisions:" + key : viewModel.LeftPath is { } left ? IdFor(left, viewModel.Path) : IdFor(viewModel.Path, viewModel.Staged);
        Title = viewModel.IsRevisionComparison
            ? $"{System.IO.Path.GetFileName(viewModel.Path)} ({viewModel.OldTitle} → {viewModel.NewTitle})"
            : viewModel.LeftPath is { } leftPath
                ? $"{System.IO.Path.GetFileName(leftPath)} ↔ {System.IO.Path.GetFileName(viewModel.Path)}"
                : $"{System.IO.Path.GetFileName(viewModel.Path)} ({(viewModel.Staged ? "Staged" : "Working Tree")})";
    }

    public static string IdFor(string path, bool staged) => $"diff:{(staged ? "staged" : "worktree")}:{path}";

    public static string IdFor(string left, string right) => $"diff:files:{left}|{right}";

    public override string Kind => "diff";

    public override string? TabIcon => "compare";

    public DiffViewModel ViewModel { get; }

    /// <summary>The view showing the document now (its commands act on it), or null.</summary>
    internal Views.DiffView? View { get; set; }

    public EditorFont Font { get; }
}
