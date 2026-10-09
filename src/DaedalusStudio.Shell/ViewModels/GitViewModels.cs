using System.Collections.ObjectModel;
using System.Text;
using DaedalusStudio.Shell.Docking;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Git;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>A file in the Changes view.</summary>
public sealed class GitFileRow(GitFileStatus status, bool staged)
{
    public GitFileStatus Status { get; } = status;

    public bool Staged { get; } = staged;

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

    public string AccessibleName => $"{Name} in {Folder}, {Description}{(Staged ? ", staged" : string.Empty)}";
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

    public void Refresh()
    {
        var status = _git.Status;
        Replace(Staged, status?.Staged.Select(f => new GitFileRow(f, staged: true)));
        Replace(Changes, status?.Unstaged.Select(f => new GitFileRow(f, staged: false)));
        Replace(Conflicts, status?.Conflicts.Select(f => new GitFileRow(f, staged: false)));
        foreach (var name in new[] { nameof(HasRepository), nameof(HasNoRepository), nameof(BranchText), nameof(IsMerging), nameof(HasStaged), nameof(HasChanges), nameof(HasConflicts), nameof(StagedHeader), nameof(ChangesHeader), nameof(ConflictsHeader) })
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

    /// <summary>Two files, side by side (<c>daedalus diff left right</c>): no Git, no staging.</summary>
    public DiffViewModel(string leftPath, string rightPath)
    {
        LeftPath = leftPath;
        Path = rightPath;
    }

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

    public string OldTitle => LeftPath ?? (Staged ? "HEAD" : "Index");

    public string NewTitle => IsFileComparison ? Path : Staged ? "Index (staged)" : "Working tree";

    public bool CanStage => !Staged && !IsFileComparison;

    public bool CanUnstage => Staged;

    /// <summary>The texts or rows changed; views rebuild.</summary>
    public event EventHandler? Changed;

    public async Task LoadAsync()
    {
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

        OldText = await ReadAsync(LeftPath!).ConfigureAwait(true);
        NewText = await ReadAsync(Path).ConfigureAwait(true);
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
        Id = viewModel.LeftPath is { } left ? IdFor(left, viewModel.Path) : IdFor(viewModel.Path, viewModel.Staged);
        Title = viewModel.LeftPath is { } leftPath
            ? $"{System.IO.Path.GetFileName(leftPath)} ↔ {System.IO.Path.GetFileName(viewModel.Path)}"
            : $"{System.IO.Path.GetFileName(viewModel.Path)} ({(viewModel.Staged ? "Staged" : "Working Tree")})";
    }

    public static string IdFor(string path, bool staged) => $"diff:{(staged ? "staged" : "worktree")}:{path}";

    public static string IdFor(string left, string right) => $"diff:files:{left}|{right}";

    public override string Kind => "diff";

    public DiffViewModel ViewModel { get; }

    public EditorFont Font { get; }
}
