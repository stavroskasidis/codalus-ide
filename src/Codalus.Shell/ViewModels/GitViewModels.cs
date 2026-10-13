using System.Collections.ObjectModel;
using System.Text;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Git;

namespace Codalus.Shell.ViewModels;

/// <summary>A file in the Changes view.</summary>
public sealed class GitFileRow(GitFileStatus status, bool staged, GitLfsStatus? lfs = null)
{
    public GitFileStatus Status { get; } = status;

    public bool Staged { get; } = staged;

    private readonly GitLfsFile? _lfsFile = lfs?.FileOf(status.Path);
    private readonly GitLfsLock? _lock = lfs?.LockOf(status.Path);

    /// <summary>Git LFS (M13b): "LFS", "LFS pointer" (not downloaded), and who holds a lock.</summary>
    public string? Badge => _lock is { } held ? "locked: " + held.Owner : _lfsFile is { IsPointer: true } ? "LFS pointer" : _lfsFile is not null ? "LFS" : null;

    public bool HasBadge => Badge is not null;

    public string? BadgeTooltip => _lock is { } held
        ? $"Locked in Git LFS by {held.Owner}{(held.LockedAt is { } at ? " since " + at.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture) : string.Empty)}"
        : _lfsFile is { IsPointer: true } ? "Tracked by Git LFS; only its pointer is here (git-lfs isn't installed, or the content wasn't downloaded)."
        : _lfsFile is not null ? "Tracked by Git LFS." : null;

    public string Name => System.IO.Path.GetFileName(Status.Path);

    public string Folder => System.IO.Path.GetDirectoryName(Status.RelativePath)?.Replace('\\', '/') ?? string.Empty;

    /// <summary>One letter: M(odified), A(dded), D(eleted), R(enamed), U(ntracked or conflicted).</summary>
    public string Letter => Status.IsConflict ? "!" : Status.IsUntracked ? "U" : (Staged ? Status.Index : Status.WorkTree).ToString();

    public string Description => Status.IsConflict ? "conflict" : Status.IsUntracked ? "untracked" : (Staged ? Status.Index : Status.WorkTree) switch
    {
        'M' => "modified",
        'A' => "added",
        'D' => "deleted",
        'R' => "renamed",
        'C' => "copied",
        'T' => "type changed",
        _ => "changed",
    };

    public bool CanStage => !Staged;

    public bool CanUnstage => Staged;

    public string AccessibleName => $"{Name} in {Folder}, {Description}{(Staged ? ", staged" : string.Empty)}{(Badge is { } badge ? ", " + badge : string.Empty)}";
}

/// <summary>A repository of the solution (several repositories, M13b).</summary>
public sealed record GitRepositoryChoice(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));

    public override string ToString() => Name;
}

/// <summary>A completion in the commit message: an issue or work item (M13b).</summary>
public sealed record CommitCompletion(string Reference, string Title, string? Detail)
{
    public string Label => $"{Reference}  {Title}";
}

/// <summary>The Git Changes tool window (ADR 0008).</summary>
public sealed class GitChangesViewModel : ObservableObject
{
    public const string ToolWindowId = "git.changes";

    private readonly GitService _git;
    private readonly Func<string, bool, Task> _openDiff;
    private readonly Func<string, Task> _run;
    private string _message = string.Empty;
    private bool _amend;
    private string? _messageBeforeAmend;

    public GitChangesViewModel(GitService git, Func<string, bool, Task> openDiff, Func<string, Task> run)
    {
        _git = git;
        _openDiff = openDiff;
        _run = run;
        git.StatusChanged += (_, _) => Ui.Post(Refresh);
        Refresh();
    }

    public ObservableCollection<GitFileRow> Staged { get; } = [];

    public ObservableCollection<GitFileRow> Changes { get; } = [];

    public ObservableCollection<GitFileRow> Conflicts { get; } = [];

    public bool HasRepository => _git.Status is not null;

    public bool HasNoRepository => !HasRepository;

    public string BranchText
    {
        get
        {
            if (_git.Status is not { } status)
            {
                return string.Empty;
            }

            var name = status.Branch ?? $"(detached at {status.Head?[..Math.Min(7, status.Head.Length)]})";
            var sync = status.Upstream is null ? " (not published)" : status.Ahead > 0 || status.Behind > 0 ? $"  ↑{status.Ahead} ↓{status.Behind}" : string.Empty;
            return name + sync;
        }
    }

    public bool IsMerging => _git.Status?.IsMerging ?? false;

    private string? _operation;

    /// <summary>The merge, rebase, cherry-pick or revert in progress (M13); the banner offers Continue, Skip and Abort.</summary>
    public string? Operation
    {
        get => _operation;
        set
        {
            if (SetField(ref _operation, value))
            {
                OnPropertyChanged(nameof(HasOperation));
                OnPropertyChanged(nameof(OperationText));
                OnPropertyChanged(nameof(CanSkip));
            }
        }
    }

    public bool HasOperation => _operation is not null;

    public bool CanSkip => _operation is "rebase" or "cherry-pick" or "revert";

    public string OperationText => _operation switch
    {
        "merge" => "A merge is in progress.",
        "rebase" => "A rebase is in progress.",
        "cherry-pick" => "A cherry-pick is in progress.",
        "revert" => "A revert is in progress.",
        _ => string.Empty,
    } + (HasConflicts ? " Resolve the conflicts, then Continue." : " Continue when ready.");

    public bool HasStaged => Staged.Count > 0;

    public bool HasChanges => Changes.Count > 0;

    public bool HasConflicts => Conflicts.Count > 0;

    public string StagedHeader => $"Staged Changes ({Staged.Count})";

    public string ChangesHeader => $"Changes ({Changes.Count})";

    public string ConflictsHeader => $"Merge Conflicts ({Conflicts.Count})";

    public string Message
    {
        get => _message;
        set => SetField(ref _message, value ?? string.Empty);
    }

    /// <summary>Amend the last commit: fills in its message (and restores the draft when unchecked).</summary>
    public bool Amend
    {
        get => _amend;
        set
        {
            if (!SetField(ref _amend, value))
            {
                return;
            }

            if (value)
            {
                _messageBeforeAmend = Message;
                _ = FillLastMessageAsync();
            }
            else
            {
                Message = _messageBeforeAmend ?? string.Empty;
            }
        }
    }

    private async Task FillLastMessageAsync()
    {
        if (await _git.LastCommitMessageAsync().ConfigureAwait(true) is { } last && Amend && Message == (_messageBeforeAmend ?? string.Empty))
        {
            Message = last;
        }
    }

    // ------------------------------------------------------------------ M13b: repositories, CI status, pull request, LFS, completion

    private GitLfsStatus _lfs = GitLfsStatus.None;
    private bool _updatingRepositories;
    private GitRepositoryChoice? _selectedRepository;
    private string? _ciState;
    private string _ciText = string.Empty;
    private string? _ciTooltip;
    private string? _pullRequestText;

    /// <summary>The repository's Git LFS files and locks: the rows' badges.</summary>
    public GitLfsStatus Lfs
    {
        get => _lfs;
        set
        {
            _lfs = value;
            Refresh();
        }
    }

    /// <summary>The solution's repositories, when it spans several.</summary>
    public ObservableCollection<GitRepositoryChoice> Repositories { get; } = [];

    public bool HasSeveralRepositories => Repositories.Count > 1;

    /// <summary>Opens another of the solution's repositories (set by the shell).</summary>
    public Func<string, Task>? SwitchRepository { get; set; }

    public GitRepositoryChoice? SelectedRepository
    {
        get => _selectedRepository;
        set
        {
            if (SetField(ref _selectedRepository, value) && !_updatingRepositories && value is not null && SwitchRepository is { } switchTo)
            {
                _ = switchTo(value.Path);
            }
        }
    }

    public void SetRepositories(IReadOnlyList<string> roots, string? active)
    {
        _updatingRepositories = true;
        try
        {
            Repositories.Clear();
            foreach (var root in roots)
            {
                Repositories.Add(new GitRepositoryChoice(root));
            }

            SelectedRepository = Repositories.FirstOrDefault(r => active is not null && Workbench.Projects.SolutionService.PathComparer.Equals(r.Path, active));
            OnPropertyChanged(nameof(HasSeveralRepositories));
        }
        finally
        {
            _updatingRepositories = false;
        }
    }

    /// <summary>The branch's CI status: <c>success</c>, <c>failure</c>, <c>pending</c>, or null when unknown.</summary>
    public string? CiState => _ciState;

    public string CiText => _ciText;

    public string? CiTooltip => _ciTooltip;

    public bool HasCi => _ciState is not null and not "none";

    public string CiIcon => _ciState switch { "success" => "test-passed", "failure" => "test-failed", _ => "test-running" };

    public bool IsCiPassed => _ciState == "success";

    public bool IsCiFailed => _ciState == "failure";

    public bool IsCiPending => _ciState == "pending";

    public void SetCi(string? state, string text, string? tooltip)
    {
        _ciState = state;
        _ciText = text;
        _ciTooltip = tooltip;
        foreach (var name in new[] { nameof(CiState), nameof(CiText), nameof(CiTooltip), nameof(HasCi), nameof(CiIcon), nameof(IsCiPassed), nameof(IsCiFailed), nameof(IsCiPending) })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>"#1 Add the greeter": the open pull request of the current branch.</summary>
    public string? PullRequestText
    {
        get => _pullRequestText;
        set
        {
            if (SetField(ref _pullRequestText, value))
            {
                OnPropertyChanged(nameof(HasPullRequest));
            }
        }
    }

    public bool HasPullRequest => _pullRequestText is not null;

    /// <summary>Searches issues and work items for <c>#</c> completion in the message (set by the shell when signed in).</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<CommitCompletion>>>? CompletionSource { get; set; }

    /// <summary>The <c>#123</c> or <c>#words</c> being typed before <paramref name="caret"/>: where its <c>#</c> is, and what follows it.</summary>
    public static (int Start, string Query)? CompletionQuery(string text, int caret)
    {
        if (caret < 1 || caret > text.Length)
        {
            return null;
        }

        var start = caret - 1;
        while (start >= 0 && (char.IsLetterOrDigit(text[start]) || text[start] is '_' or '-'))
        {
            start--;
        }

        return start >= 0 && text[start] == '#' && (start == 0 || char.IsWhiteSpace(text[start - 1]) || text[start - 1] is '(' or '[')
            ? (start, text[(start + 1)..caret])
            : null;
    }

    /// <summary>The completions at the caret, or none when no <c>#</c> is being typed (or nobody is signed in).</summary>
    public async Task<IReadOnlyList<CommitCompletion>> CompleteAsync(string text, int caret, CancellationToken cancellationToken)
    {
        if (CompletionSource is not { } source || CompletionQuery(text, caret) is not { } query)
        {
            return [];
        }

        return await source(query.Query, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Replaces the <c>#…</c> before the caret with the reference and a space; returns the text and the new caret.</summary>
    public static (string Text, int Caret) ApplyCompletion(string text, int caret, string reference)
    {
        if (CompletionQuery(text, caret) is not { } query)
        {
            return (text, caret);
        }

        // A space after it, unless one follows already; the caret goes past it.
        var rest = text[caret..];
        var inserted = rest.Length > 0 && char.IsWhiteSpace(rest[0]) ? reference : reference + " ";
        return (text[..query.Start] + inserted + rest, query.Start + reference.Length + 1);
    }

    public void Refresh()
    {
        var status = _git.Status;
        Replace(Staged, status?.Staged.Select(f => new GitFileRow(f, staged: true, _lfs)));
        Replace(Changes, status?.Unstaged.Select(f => new GitFileRow(f, staged: false, _lfs)));
        Replace(Conflicts, status?.Conflicts.Select(f => new GitFileRow(f, staged: false, _lfs)));
        foreach (var name in new[] { nameof(HasRepository), nameof(HasNoRepository), nameof(BranchText), nameof(IsMerging), nameof(HasStaged), nameof(HasChanges), nameof(HasConflicts), nameof(StagedHeader), nameof(ChangesHeader), nameof(ConflictsHeader), nameof(OperationText) })
        {
            OnPropertyChanged(name);
        }
    }

    private static void Replace(ObservableCollection<GitFileRow> target, IEnumerable<GitFileRow>? rows)
    {
        target.Clear();
        foreach (var row in (rows ?? []).OrderBy(r => r.Status.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            target.Add(row);
        }
    }

    public Task StageAsync(GitFileRow row) => _git.StageAsync([row.Status.Path]);

    public Task UnstageAsync(GitFileRow row) => _git.UnstageAsync([row.Status.Path]);

    public Task StageAllAsync() => Changes.Count == 0 ? Task.CompletedTask : _git.StageAsync([.. Changes.Select(r => r.Status.Path)]);

    public Task UnstageAllAsync() => Staged.Count == 0 ? Task.CompletedTask : _git.UnstageAsync([.. Staged.Select(r => r.Status.Path)]);

    public Task OpenDiffAsync(GitFileRow row) => _openDiff(row.Status.Path, row.Staged);

    /// <summary>Commits (staged changes, or every tracked change with <paramref name="all"/>); clears the message on success.</summary>
    public async Task CommitAsync(bool all)
    {
        await _git.CommitAsync(Message, Amend, all).ConfigureAwait(true);
        _messageBeforeAmend = null;
        _amend = false;
        OnPropertyChanged(nameof(Amend));
        Message = string.Empty;
    }

    /// <summary>Runs a shell command (fetch, pull, push, discard…), which reports its own errors.</summary>
    public Task RunAsync(string command) => _run(command);
}

/// <summary>A row of a side-by-side diff: a line on either side (null = filler), and its change kind.</summary>
public sealed record DiffRow(int? OldLine, int? NewLine, char Kind);

/// <summary>A file's diff (work tree against the index, or the index against HEAD), inline or side by side.</summary>
public sealed class DiffViewModel : ObservableObject
{
    private readonly GitService? _git;
    private bool _sideBySide = true;
    private string _summary = string.Empty;

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
        OldText = oldText;
        NewText = newText;
        OldLines = LineDiff.Lines(oldText);
        NewLines = LineDiff.Lines(newText);
        var ops = await Task.Run(() => LineDiff.Diff(OldLines, NewLines)).ConfigureAwait(true);
        Rows = Align(ops);
        var hunks = Diff?.Hunks.Count ?? 0;
        Summary = Diff is null ? "No changes." : Diff.IsBinary ? "Binary file." : $"{hunks} change{(hunks == 1 ? string.Empty : "s")}: +{Diff.Hunks.Sum(h => h.Added)} −{Diff.Hunks.Sum(h => h.Removed)}";
        Changed?.Invoke(this, EventArgs.Empty);
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
        OldText = oldText;
        NewText = newText;
        OldLines = LineDiff.Lines(OldText);
        NewLines = LineDiff.Lines(NewText);
        var ops = await Task.Run(() => LineDiff.Diff(OldLines, NewLines)).ConfigureAwait(true);
        Rows = Align(ops);
        var added = ops.Count(o => o.Kind == '+');
        var removed = ops.Count(o => o.Kind == '-');
        Summary = added + removed == 0 ? "The files are identical." : $"+{added} −{removed}";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Pairs removed and added runs line by line ('~' rows), so side-by-side views align.</summary>
    private static List<DiffRow> Align(IReadOnlyList<DiffOp> ops)
    {
        var rows = new List<DiffRow>(ops.Count);
        var i = 0;
        while (i < ops.Count)
        {
            if (ops[i].Kind == ' ')
            {
                rows.Add(new DiffRow(ops[i].OldIndex + 1, ops[i].NewIndex + 1, ' '));
                i++;
                continue;
            }

            var removed = new List<int>();
            var added = new List<int>();
            while (i < ops.Count && ops[i].Kind != ' ')
            {
                if (ops[i].Kind == '-')
                {
                    removed.Add(ops[i].OldIndex + 1);
                }
                else
                {
                    added.Add(ops[i].NewIndex + 1);
                }

                i++;
            }

            for (var k = 0; k < Math.Max(removed.Count, added.Count); k++)
            {
                int? oldLine = k < removed.Count ? removed[k] : null;
                int? newLine = k < added.Count ? added[k] : null;
                rows.Add(new DiffRow(oldLine, newLine, oldLine is null ? '+' : newLine is null ? '-' : '~'));
            }
        }

        return rows;
    }

    /// <summary>The inline text: removed lines (old) and added lines (new) interleaved, with each row's kind.</summary>
    public (string Text, IReadOnlyList<char> Kinds) InlineText()
    {
        var text = new StringBuilder();
        var kinds = new List<char>();
        foreach (var row in Rows)
        {
            if (row.Kind == ' ')
            {
                text.Append(NewLines[row.NewLine!.Value - 1]).Append('\n');
                kinds.Add(' ');
                continue;
            }

            if (row.OldLine is { } oldLine)
            {
                text.Append(OldLines[oldLine - 1]).Append('\n');
                kinds.Add('-');
            }

            if (row.NewLine is { } newLine)
            {
                text.Append(NewLines[newLine - 1]).Append('\n');
                kinds.Add('+');
            }
        }

        return (text.ToString(), kinds);
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
    public DiffDocument(DiffViewModel viewModel, EditorFont font)
    {
        ViewModel = viewModel;
        Font = font;
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
