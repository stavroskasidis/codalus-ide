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

/// <summary>What a diff document shows: its lines, or a message instead (D45 E1–E6).</summary>
public enum DiffState
{
    /// <summary>The two versions' lines, aligned.</summary>
    Lines,

    /// <summary>Nothing differs (E1, E6), or the files are identical.</summary>
    NoChanges,

    /// <summary>Git reports a change, but every line's text is the same: line endings, the encoding, the file mode (E5).</summary>
    OnlyFormat,

    /// <summary>A binary file: each side's size (E2).</summary>
    Binary,

    /// <summary>Over 10 MB or 100,000 lines: waits for Compare Anyway (E3).</summary>
    TooLarge,
}

/// <summary>What a staging command does to the lines it acts on.</summary>
public enum DiffAction
{
    /// <summary>The working tree's lines into the index.</summary>
    Stage,

    /// <summary>The index's lines back to HEAD's.</summary>
    Unstage,

    /// <summary>The working tree's lines back to the index's.</summary>
    Discard,
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
    private int _reloadVersion;
    private bool _showStagingControls;
    private bool _isBusy;
    private string? _busyText;
    private byte[] _oldBytes = [];
    private byte[] _newBytes = [];
    private bool _isSummary;
    private bool _isLoading;
    private bool _compareAnyway;
    private readonly HashSet<int> _expandedFolds = [];
    private (int Bytes, int Lines) _largest;

    /// <summary>A side larger than this (bytes) or longer (lines) waits for Compare Anyway (D45 question 13).</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    public const int MaxLines = 100_000;
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

    /// <summary>Saves the staging controls' toggle to the settings.</summary>
    public Action<DiffViewModel>? SaveStagingControls { get; set; }

    /// <summary>
    /// Whether the staging column shows each change's Stage and Discard (or Unstage) buttons (D45 question 5, the
    /// <c>git.diff.showStagingControls</c> setting, off by default). Hidden, the commands are still on the context menu.
    /// </summary>
    public bool ShowStagingControls
    {
        get => _showStagingControls;
        set
        {
            if (SetField(ref _showStagingControls, value))
            {
                SaveStagingControls?.Invoke(this);
            }
        }
    }

    /// <summary>Sets the staging controls' toggle from the setting, without saving it back.</summary>
    public void LoadStagingControls(bool shown) => _showStagingControls = shown;

    /// <summary>While git stages, unstages or discards: the actions wait, and the toolbar says so.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    /// <summary>What the toolbar says while busy ("Staging…"), or null.</summary>
    public string? BusyText
    {
        get => _busyText;
        private set => SetField(ref _busyText, value);
    }

    /// <summary>Whether the working tree's side has edits in its editor that aren't saved (they're shown; actions save them first).</summary>
    public bool HasUnsavedEdits => !Staged && IsGitDiff && Services?.OpenDocument(Path) is { IsModified: true };

    /// <summary>
    /// Summary (D45 question 12, F1, off by default): unchanged runs folded to three lines of context, both sides in
    /// step; a folded run opens on a click or Enter.
    /// </summary>
    public bool IsSummary
    {
        get => _isSummary;
        set
        {
            if (SetField(ref _isSummary, value))
            {
                _expandedFolds.Clear();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The folded runs shown now: Summary's, less those opened.</summary>
    public IReadOnlyList<DiffFold> Folds => _isSummary && State == DiffState.Lines ? [.. DiffRows.Folds(Rows).Where(f => !_expandedFolds.Contains(f.FirstRow))] : [];

    /// <summary>Opens a folded run (its first row).</summary>
    public void ExpandFold(int firstRow)
    {
        if (_expandedFolds.Add(firstRow))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A folded run's line: "⋯ 12 unchanged lines, 7–18", in a side's own numbers.</summary>
    public string FoldLabel(DiffFold fold, bool old)
    {
        int? first = old ? Rows[fold.FirstRow].OldLine : Rows[fold.FirstRow].NewLine;
        int? last = old ? Rows[fold.LastRow].OldLine : Rows[fold.LastRow].NewLine;
        return Localizer.Format("diff.foldedLines", "⋯ {0} unchanged lines, {1}–{2}", fold.Count, first ?? 0, last ?? 0);
    }

    /// <summary>While the texts are read and compared.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    /// <summary>What the document shows: its lines, or a message (no changes, only the line endings, binary, too large).</summary>
    public DiffState State { get; private set; }

    /// <summary>The message shown instead of the lines, or null.</summary>
    public string? StateMessage { get; private set; }

    /// <summary>The line under the message, or null.</summary>
    public string? StateDetail { get; private set; }

    /// <summary>The toolbar's count while there are no lines to compare ("No changes", "Line endings only"…).</summary>
    public string? StateCount { get; private set; }

    /// <summary>A binary file's sizes, each side's ("14.2 KB").</summary>
    public (string Old, string New) Sizes { get; private set; } = (string.Empty, string.Empty);

    /// <summary>The working tree matches the index, but the file has staged changes: offer the staged diff (E1).</summary>
    public bool OffersStagedChanges { get; private set; }

    /// <summary>Opens the file's staged diff (E1's Open Staged Changes).</summary>
    public Func<string, Task>? OpenStaged { get; set; }

    /// <summary>A file too large to compare is compared anyway (E3).</summary>
    public Task CompareAnywayAsync()
    {
        _compareAnyway = true;
        return LoadAsync();
    }

    /// <summary>Whether the old side's file exists (an untracked file's index side doesn't).</summary>
    public bool OldExists { get; private set; } = true;

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
        IsLoading = true;
        try
        {
            await LoadCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadCoreAsync()
    {
        OldPath = LeftPath ?? Path;
        if (_oldProvider is not null && _newProvider is not null)
        {
            var oldText = await _oldProvider().ConfigureAwait(true);
            var newText = await _newProvider().ConfigureAwait(true);
            SetSides(_oldTitle ?? string.Empty, null, _newTitle ?? string.Empty, null);
            var revisionsGated = Gate(oldText.Length, newText.Length, Count(oldText), Count(newText), oldText.Contains('\0') || newText.Contains('\0'));
            await SetTextsAsync(revisionsGated ? Text(string.Empty) : Text(oldText), revisionsGated ? Text(string.Empty) : Text(newText)).ConfigureAwait(true);
            SetSides(_oldTitle ?? string.Empty, null, _newTitle ?? string.Empty, null);
            SetState(revisionsGated);
            Summary = AddedLines + RemovedLines == 0 ? "The files are identical." : $"+{AddedLines} −{RemovedLines}";
            Loaded();
            return;
        }

        if (_git is null)
        {
            var oldFile = File.Exists(LeftPath!) ? await ReadBytesAsync(LeftPath!).ConfigureAwait(true) : [];
            var newFile = File.Exists(Path) ? await ReadBytesAsync(Path).ConfigureAwait(true) : [];
            SetSides(Folder(LeftPath!), null, Folder(Path), null);
            (_oldBytes, _newBytes) = (oldFile, newFile);
            var filesGated = Gate(oldFile.Length, newFile.Length, Count(oldFile), Count(newFile), IsBinary(oldFile) || IsBinary(newFile));
            await SetTextsAsync(filesGated ? Text(string.Empty) : TextFiles.Decode(oldFile), filesGated ? Text(string.Empty) : TextFiles.Decode(newFile)).ConfigureAwait(true);
            SetSides(Folder(LeftPath!), null, Folder(Path), null);
            SetState(filesGated);
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
        OldExists = oldBytes is not null;
        var old = oldBytes is null ? Text(string.Empty) : TextFiles.Decode(oldBytes);
        (string Text, TextFileFormat Format) now;
        byte[] nowBytes;
        var buffer = Staged ? null : Services?.OpenDocument(Path);
        if (Staged)
        {
            nowBytes = await _git.ShowBytesAsync(Path, ":").ConfigureAwait(true) ?? [];
            now = nowBytes.Length == 0 ? Text(string.Empty) : TextFiles.Decode(nowBytes);
        }
        else if (buffer is not null)
        {
            // The working tree's side is the open buffer, unsaved edits included (D45 question 11).
            now = (buffer.Text.Text, buffer.Format);
            nowBytes = TextFiles.Encode(now.Text, now.Format);
        }
        else
        {
            nowBytes = File.Exists(Path) ? await ReadBytesAsync(Path).ConfigureAwait(true) : [];
            now = nowBytes.Length == 0 ? Text(string.Empty) : TextFiles.Decode(nowBytes);
        }

        Diff = await _git.DiffAsync(Path, Staged).ConfigureAwait(true);
        var index0 = Localizer.Get("diff.index", "Index");
        SetSides(Staged ? "HEAD" : index0, null, Staged ? index0 : Localizer.Get("diff.workingTree", "Working tree"), null);
        var gated = Gate(oldBytes?.Length ?? 0, nowBytes.Length, Count(oldBytes ?? []), Count(nowBytes), Diff is { IsBinary: true } || IsBinary(oldBytes ?? []) || IsBinary(nowBytes));
        await SetTextsAsync(gated ? Text(string.Empty) : old, gated ? Text(string.Empty) : now).ConfigureAwait(true);
        (_oldBytes, _newBytes) = (oldBytes ?? [], nowBytes);
        string? newNote = buffer is { IsModified: true } ? Localizer.Get("diff.unsavedEditsIncluded", "unsaved edits included") : null;
        if (Staged)
        {
            newNote = OldPath != Path
                ? Localizer.Format("diff.renamedFrom", "renamed from {0}", System.IO.Path.GetFileName(OldPath))
                : status is { WorkTree: not '.' } ? Localizer.Get("diff.workingTreeHasMoreEdits", "the working tree has more edits") : null;
        }
        else if (!File.Exists(Path) && buffer is null)
        {
            newNote = Localizer.Get("diff.deleted", "deleted");
        }

        var index = Localizer.Get("diff.index", "Index");
        SetSides(Staged ? "HEAD" : index, null, Staged ? index : Localizer.Get("diff.workingTree", "Working tree"), newNote);
        OffersStagedChanges = !Staged && status is { IsStaged: true };
        SetState(gated);
        var changes = Changes.Count;
        Summary = Diff is null ? "No changes." : Diff.IsBinary ? "Binary file." : $"{changes} change{(changes == 1 ? string.Empty : "s")}: +{AddedLines} −{RemovedLines}";
        Loaded();
    }

    /// <summary>
    /// Before comparing (E2, E3): a binary file shows its sizes; a side over 10 MB or 100,000 lines waits for Compare
    /// Anyway. True when the lines aren't compared.
    /// </summary>
    private bool Gate(int oldLength, int newLength, int oldLines, int newLines, bool binary)
    {
        Sizes = (Size(oldLength), Size(newLength));
        _largest = (Math.Max(oldLength, newLength), Math.Max(oldLines, newLines));
        if (binary)
        {
            State = DiffState.Binary;
            return true;
        }

        if (!_compareAnyway && (Math.Max(oldLength, newLength) > MaxBytes || Math.Max(oldLines, newLines) > MaxLines))
        {
            State = DiffState.TooLarge;
            return true;
        }

        State = DiffState.Lines;
        return false;
    }

    /// <summary>The message instead of the lines, when there are none to compare (E1–E6).</summary>
    private void SetState(bool gated)
    {
        var name = System.IO.Path.GetFileName(Path);
        (StateMessage, StateDetail, StateCount) = (null, null, null);
        if (gated && State == DiffState.Binary)
        {
            (StateMessage, StateCount) = (Localizer.Get("diff.binaryFile", "Binary file"), Localizer.Get("diff.binaryFile", "Binary file"));
            return;
        }

        if (gated)
        {
            StateMessage = Localizer.Format("diff.tooLarge", "{0} is too large to compare here ({1}, {2} lines).", name, Size(_largest.Bytes), _largest.Lines.ToString("N0", System.Globalization.CultureInfo.CurrentCulture));
            StateDetail = Localizer.Get("diff.tooLargeDetail", "Comparing it could keep Codalus busy for a while.");
            StateCount = Localizer.Get("diff.notCompared", "Not compared");
            return;
        }

        if (Changes.Count > 0)
        {
            State = DiffState.Lines;
            return;
        }

        if (IsGitDiff && Diff is not null)
        {
            // Git reports a change, but every line's text is the same (E5).
            State = DiffState.OnlyFormat;
            var (old, now) = (Where(OldSide), Where(NewSide));
            var modes = Diff.Header.Select(l => l.Split(' ')).Where(p => p.Length == 3 && p[1] == "mode" && p[0] is "old" or "new").ToDictionary(p => p[0], p => p[2]);
            if (modes.TryGetValue("old", out var oldMode) && modes.TryGetValue("new", out var newMode))
            {
                StateMessage = Localizer.Format("diff.onlyMode", "Only the file mode differs: {0} in {1}, {2} in {3}.", oldMode, old, newMode, now);
                StateCount = Localizer.Get("diff.modeOnly", "File mode only");
            }
            else if (OldSide.Encoding != NewSide.Encoding)
            {
                StateMessage = Localizer.Format("diff.onlyEncoding", "Only the encoding differs: {0} in {1}, {2} in {3}.", OldSide.Encoding, old, NewSide.Encoding, now);
                StateCount = Localizer.Get("diff.encodingOnly", "Encoding only");
            }
            else if (OldSide.LineEnding != NewSide.LineEnding || _oldBytes.AsSpan().IndexOf((byte)'\r') >= 0 != _newBytes.AsSpan().IndexOf((byte)'\r') >= 0)
            {
                StateMessage = Localizer.Format("diff.onlyLineEndings", "Only the line endings differ: {0} in {1}, {2} in {3}.", OldSide.LineEnding, old, NewSide.LineEnding, now);
                StateDetail = CanStage ? Localizer.Format("diff.onlyLineEndingsDetail", "Every line's text is the same. Stage All records the new line endings; Discard All… puts {0} back.", OldSide.LineEnding) : null;
                StateCount = Localizer.Get("diff.lineEndingsOnly", "Line endings only");
            }
            else
            {
                StateMessage = Localizer.Get("diff.onlyLastNewline", "Only the newline at the end of the file differs.");
                StateCount = Localizer.Get("diff.lastNewlineOnly", "Last newline only");
            }

            return;
        }

        State = DiffState.NoChanges;
        StateCount = Localizer.Get("diff.noChanges", "No changes");
        if (!IsGitDiff)
        {
            StateMessage = Localizer.Get("diff.identical", "The files are identical.");
        }
        else if (Staged)
        {
            StateMessage = Localizer.Get("diff.indexMatchesHead", "No changes: the index matches HEAD.");
        }
        else if (OffersStagedChanges)
        {
            StateMessage = Localizer.Get("diff.workingTreeMatchesIndex", "No changes: the working tree matches the index.");
            StateDetail = Localizer.Format("diff.hasStagedChanges", "{0} has staged changes.", name);
        }
        else
        {
            StateMessage = Localizer.Format("diff.noChangesAtAll", "{0} has no changes.", name);
            StateDetail = Localizer.Get("diff.allMatch", "The working tree, the index and HEAD all match.");
        }
    }

    /// <summary>A side named in a sentence: "the index", "the working tree", "HEAD", a revision.</summary>
    private static string Where(DiffSide side) => side.Label switch
    {
        "Index" => Localizer.Get("diff.theIndex", "the index"),
        "Working tree" => Localizer.Get("diff.theWorkingTree", "the working tree"),
        var label => label,
    };

    private static int Count(ReadOnlySpan<byte> text) => text.Count((byte)'\n') + (text.Length > 0 && text[^1] != (byte)'\n' ? 1 : 0);

    private static int Count(string text) => text.AsSpan().Count('\n') + (text.Length > 0 && text[^1] != '\n' ? 1 : 0);

    /// <summary>Git's test: a NUL byte in the first 8,000 bytes.</summary>
    private static bool IsBinary(ReadOnlySpan<byte> bytes) => bytes[..Math.Min(bytes.Length, 8000)].IndexOf((byte)0) >= 0;

    /// <summary>A size as the document shows it: "15.0 KB", "18.4 MB".</summary>
    private static string Size(long bytes) => bytes switch
    {
        < 1024 => Localizer.Format("diff.bytes", "{0} bytes", bytes),
        < 1024 * 1024 => Localizer.Format("diff.kilobytes", "{0:0.0} KB", bytes / 1024.0),
        _ => Localizer.Format("diff.megabytes", "{0:0.0} MB", bytes / 1024.0 / 1024.0),
    };

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

    private static async Task<byte[]> ReadBytesAsync(string path)
    {
        try
        {
            return await File.ReadAllBytesAsync(path).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
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
        if (IsLoading && !IsLoaded)
        {
            return Localizer.Get("diff.comparing", "Comparing…");
        }

        if (State != DiffState.Lines && StateCount is { } count)
        {
            return count;
        }

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

    /// <summary>The rows of the change at a row (none on an unchanged row).</summary>
    public IReadOnlyList<int> ChangeRows(int row) =>
        ChangeIndexAt(row) is var index and >= 0 ? [.. Enumerable.Range(Changes[index].FirstRow, Changes[index].LastRow - Changes[index].FirstRow + 1)] : [];

    /// <summary>Whether an action applies to this document: Stage and Discard to the working tree's diff, Unstage to the staged one.</summary>
    public bool Allows(DiffAction action) => action == DiffAction.Unstage ? CanUnstage : CanStage;

    /// <summary>
    /// Reads the texts again after a pause (an edit in the open document, a save, the index or the status changed),
    /// so the diff refreshes by itself; a later request replaces an earlier one.
    /// </summary>
    public void RequestReload(TimeSpan delay) => _ = ReloadAfterAsync(delay);

    private async Task ReloadAfterAsync(TimeSpan delay)
    {
        var version = ++_reloadVersion;
        await Task.Delay(delay).ConfigureAwait(true);
        if (version != _reloadVersion || IsBusy)
        {
            return;
        }

        try
        {
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is GitException or InvalidOperationException or IOException)
        {
            // The next change reloads it again; a git error here has no one to tell.
        }
    }

    /// <summary>
    /// Stages, unstages or discards the changed lines among <paramref name="rows"/> (D45 questions 7 and 11): the patch is
    /// built from these rows over the old side's bytes and the new side's, and applied by git. In the working tree's diff
    /// the open document is saved first, as Stage Active File does; once the diff has read the file again, the saved
    /// text must be the text drawn (format on save can change it), else nothing is applied and the user is told.
    /// False when nothing was applied.
    /// </summary>
    public async Task<bool> ApplyAsync(DiffAction action, IReadOnlyCollection<int> rows)
    {
        if (_git is null || IsBusy || !Allows(action) || Diff is { IsBinary: true })
        {
            return false;
        }

        var chosen = rows.Where(r => (uint)r < (uint)Rows.Count && Rows[r].IsChange).ToHashSet();
        if (chosen.Count == 0)
        {
            return false;
        }

        IsBusy = true;
        BusyText = action switch
        {
            DiffAction.Stage => Localizer.Get("diff.staging", "Staging…"),
            DiffAction.Unstage => Localizer.Get("diff.unstaging", "Unstaging…"),
            _ => Localizer.Get("diff.discarding", "Discarding…"),
        };
        try
        {
            if (!await SaveFirstAsync().ConfigureAwait(true))
            {
                return false;
            }

            var patch = DiffPatch.Build(_git.RelativePath(OldPath), _git.RelativePath(Path), OldExists, _oldBytes, _newBytes, Rows, chosen, reverse: action != DiffAction.Stage);
            if (patch is null)
            {
                return false;
            }

            await _git.ApplyPatchAsync(patch, cached: action != DiffAction.Discard, reverse: action != DiffAction.Stage).ConfigureAwait(true);
            if (action == DiffAction.Discard && Services?.OpenDocument(Path) is { IsModified: false } model)
            {
                await model.ReloadAsync(CancellationToken.None).ConfigureAwait(true); // the file changed under its saved buffer
            }

            return true;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
            await LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Stage All: the whole file into the index, the open document saved first.</summary>
    public Task<bool> StageAllAsync() => WholeFileAsync(DiffAction.Stage, Localizer.Get("diff.staging", "Staging…"), git => git.StageAsync([Path]));

    /// <summary>Unstage All: the whole file out of the index (a staged rename: both its paths).</summary>
    public Task<bool> UnstageAllAsync() =>
        WholeFileAsync(DiffAction.Unstage, Localizer.Get("diff.unstaging", "Unstaging…"), git => git.UnstageAsync(OldPath != Path ? [Path, OldPath] : [Path]));

    /// <summary>Discard All…: the shell's confirmation (it says when the unsaved edits go too), then the file back to the index.</summary>
    public async Task<bool> DiscardAllAsync()
    {
        if (Services is not { } services || !CanStage || IsBusy)
        {
            return false;
        }

        var discarded = await services.DiscardAllAsync(Path, HasUnsavedEdits).ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
        return discarded;
    }

    private async Task<bool> WholeFileAsync(DiffAction action, string busy, Func<GitService, Task> run)
    {
        if (_git is null || !Allows(action) || IsBusy)
        {
            return false;
        }

        IsBusy = true;
        BusyText = busy;
        try
        {
            if (!await SaveFirstAsync().ConfigureAwait(true))
            {
                return false;
            }

            await run(_git).ConfigureAwait(true);
            return true;
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
            await LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Saves the working tree's open document when it has unsaved edits, reads the diff again, and checks that the saved
    /// text is the one drawn. False (and the user told why) when it couldn't save, or saving changed the text.
    /// </summary>
    public async Task<bool> SaveFirstAsync()
    {
        if (Staged || Services is not { } services || services.OpenDocument(Path) is not { IsModified: true } model)
        {
            return true;
        }

        var drawn = NewText;
        if (!await services.SaveAsync(model).ConfigureAwait(true))
        {
            return false;
        }

        await LoadAsync().ConfigureAwait(true);
        if (NewText != drawn || (File.Exists(Path) && TextFiles.Decode(await ReadBytesAsync(Path).ConfigureAwait(true)).Text != drawn))
        {
            services.ShowMessage(Localizer.Format("diff.changedOnSave", "{0} changed as it was saved (format on save, for example), so nothing was applied. Check the diff, then try again.", System.IO.Path.GetFileName(Path)));
            return false;
        }

        return true;
    }

    /// <summary>The 1-based lines of a side the rows show, as a range for a title ("47–48"), or null when that side has none.</summary>
    public string? LinesText(IReadOnlyCollection<int> rows, bool old)
    {
        var lines = rows.Where(r => (uint)r < (uint)Rows.Count && Rows[r].IsChange).Select(r => old ? Rows[r].OldLine : Rows[r].NewLine).OfType<int>().Order().ToList();
        return lines.Count == 0 ? null : lines[0] == lines[^1] ? lines[0].ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{lines[0]}–{lines[^1]}";
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
