using System.ComponentModel;
using System.Text;
using AvaloniaEdit.Document;
using Codalus.Shell.Docking;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Git;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>
/// A conflict in the result text: the whole marked region (<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> to
/// <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> line, offsets), each side's text, and the common ancestor's when git wrote it
/// (<c>merge.conflictStyle=diff3</c> or <c>zdiff3</c>).
/// </summary>
public sealed record MergeConflict(int Start, int Length, string Ours, string? Base, string Theirs, string OursLabel, string TheirsLabel);

/// <summary>A side of a merge: the current version (ours, the branch you're on) or the incoming one (theirs).</summary>
public enum MergeSide
{
    Current,
    Incoming,
}

/// <summary>What a conflict's block in the result holds.</summary>
public enum MergeResolution
{
    /// <summary>The merge base's text, highlighted: still to resolve.</summary>
    Unresolved,
    Current,
    Incoming,

    /// <summary>The current block, then the incoming one.</summary>
    Both,

    /// <summary>The merge base's text, kept on purpose.</summary>
    Base,

    /// <summary>Text typed into the block.</summary>
    Edited,
}

/// <summary>A range of characters in one of the merge editor's texts.</summary>
public readonly record struct MergeRange(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>
/// One conflict of the merge editor: its block in each pane (the incoming and current files and the result), and what
/// the result holds for it. Checking a side's box takes that side's block; checking both takes both (current first).
/// </summary>
public sealed class MergeConflictBlock : ObservableObject
{
    private readonly MergeEditorViewModel _owner;
    private MergeResolution _resolution;

    internal MergeConflictBlock(MergeEditorViewModel owner, int index, MergeConflict conflict, string @base, MergeRange current, MergeRange incoming)
    {
        _owner = owner;
        Index = index;
        Conflict = conflict;
        Base = @base;
        Current = current;
        Incoming = incoming;
    }

    public int Index { get; }

    public MergeConflict Conflict { get; }

    /// <summary>The current side's text (ours).</summary>
    public string Ours => Conflict.Ours;

    /// <summary>The incoming side's text (theirs).</summary>
    public string Theirs => Conflict.Theirs;

    /// <summary>The merge base's text for this conflict (empty when both sides added it).</summary>
    public string Base { get; }

    /// <summary>The block in <see cref="MergeEditorViewModel.CurrentText"/>.</summary>
    public MergeRange Current { get; }

    /// <summary>The block in <see cref="MergeEditorViewModel.IncomingText"/>.</summary>
    public MergeRange Incoming { get; }

    /// <summary>The block in <see cref="MergeEditorViewModel.Result"/>, kept up to date as the result changes.</summary>
    public MergeRange Result => new(ResultStart, ResultLength);

    internal int ResultStart { get; set; }

    internal int ResultLength { get; set; }

    public MergeResolution Resolution
    {
        get => _resolution;
        internal set
        {
            if (SetField(ref _resolution, value))
            {
                OnPropertyChanged(nameof(TakeCurrent));
                OnPropertyChanged(nameof(TakeIncoming));
                OnPropertyChanged(nameof(IsResolved));
            }
        }
    }

    /// <summary>The current side's check box: whether the result takes the current block.</summary>
    public bool TakeCurrent
    {
        get => _resolution is MergeResolution.Current or MergeResolution.Both;
        set => _owner.SetTake(this, MergeSide.Current, value);
    }

    /// <summary>The incoming side's check box: whether the result takes the incoming block.</summary>
    public bool TakeIncoming
    {
        get => _resolution is MergeResolution.Incoming or MergeResolution.Both;
        set => _owner.SetTake(this, MergeSide.Incoming, value);
    }

    public bool IsResolved => _resolution != MergeResolution.Unresolved;

    public bool Takes(MergeSide side) => side == MergeSide.Current ? TakeCurrent : TakeIncoming;

    public MergeRange RangeIn(MergeSide side) => side == MergeSide.Current ? Current : Incoming;

    /// <summary>The text the result holds for <paramref name="resolution"/> (not <see cref="MergeResolution.Edited"/>).</summary>
    internal string TextFor(MergeResolution resolution) => resolution switch
    {
        MergeResolution.Unresolved or MergeResolution.Base => Base,
        MergeResolution.Current => Ours,
        MergeResolution.Incoming => Theirs,
        MergeResolution.Both => MergeEditorViewModel.ReplacementFor(Conflict, "both"),
        _ => throw new ArgumentOutOfRangeException(nameof(resolution)),
    };
}

/// <summary>
/// The 3-way merge editor (M13, redesigned 2026-10-19 after a classic IDE's): the incoming and current versions side by
/// side, each conflict's block highlighted with a check box beside it, and the editable result, which shows the merge
/// base's text for every conflict still to resolve instead of git's markers. Checking a block's box takes that side
/// into the result, checking both takes both (current first), unchecking takes it out again, and a header's box takes
/// every conflict from its side. Typing in a block resolves it with the typed text. Accept Merge saves the result and
/// marks the file resolved (or, for <c>codalus merge</c>, writes the merged file) once no conflict is left.
/// </summary>
public sealed class MergeEditorViewModel : ObservableObject
{
    public const string LayoutSourcesAbove = "sourcesAbove";
    public const string LayoutThreeColumns = "threeColumns";

    private readonly Func<string, Task> _accept;
    private readonly List<MergeConflictBlock> _blocks = [];
    private MergeConflictBlock? _applying;
    private int _remaining;
    private int _caretOffset;
    private double _zoom = 1;
    private string _layout = LayoutSourcesAbove;
    private bool _incomingOnLeft = true;

    public MergeEditorViewModel(
        string path,
        string result,
        string? ours,
        string? theirs,
        string? @base,
        Func<string, Task> accept,
        string? currentName = null,
        string? incomingName = null,
        TextFileFormat? format = null)
    {
        Path = path;
        InitialResult = result;
        Ours = ours ?? string.Empty;
        Theirs = theirs ?? string.Empty;
        Base = @base;
        _accept = accept;
        LineEnding = format?.LineEnding ?? TextFiles.DetectLineEnding(result);
        EncodingName = format?.EncodingName ?? "UTF-8";
        Encoding = format?.Encoding;

        var conflicts = Parse(result);
        CurrentName = Name(currentName, conflicts.Count > 0 ? conflicts[0].OursLabel : null);
        IncomingName = Name(incomingName, conflicts.Count > 0 ? conflicts[0].TheirsLabel : null);
        var (display, currentText, incomingText) = Build(result, conflicts);
        CurrentText = currentText;
        IncomingText = incomingText;
        IndentationName = DetectIndentation(result);

        Result = new TextDocument(display);
        Result.UndoStack.ClearAll();
        Result.UndoStack.MarkAsOriginalFile();
        Result.Changed += OnResultChanged;
        ((INotifyPropertyChanged)Result.UndoStack).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UndoStack.IsOriginalFile))
            {
                OnPropertyChanged(nameof(IsModified));
            }
        };
        Recount();
    }

    public string Path { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>The file as git left it, with its conflict markers.</summary>
    public string InitialResult { get; }

    /// <summary>The current version from the index (stage 2), or empty.</summary>
    public string Ours { get; }

    /// <summary>The incoming version from the index (stage 3), or empty.</summary>
    public string Theirs { get; }

    /// <summary>The merge base from the index (stage 1), or null.</summary>
    public string? Base { get; }

    /// <summary>The current pane's text: the current version, or the result with every current block taken.</summary>
    public string CurrentText { get; }

    /// <summary>The incoming pane's text: the incoming version, or the result with every incoming block taken.</summary>
    public string IncomingText { get; }

    /// <summary>The editable result: git's file with the merge base's text for each conflict still to resolve.</summary>
    public TextDocument Result { get; }

    public IReadOnlyList<MergeConflictBlock> Conflicts => _blocks;

    /// <summary>The branch or ref the current version is from (<c>develop</c>), or null.</summary>
    public string? CurrentName { get; }

    /// <summary>The branch or ref the incoming version is from (<c>master</c>), or null.</summary>
    public string? IncomingName { get; }

    public string CurrentTitle => CurrentName is { } name
        ? Localizer.Format("mergeEditor.currentTitle", "Current: {0}", name)
        : Localizer.Get("mergeEditor.current", "Current");

    public string IncomingTitle => IncomingName is { } name
        ? Localizer.Format("mergeEditor.incomingTitle", "Incoming: {0}", name)
        : Localizer.Get("mergeEditor.incoming", "Incoming");

    public string ResultTitle => Localizer.Format("mergeEditor.resultTitle", "Result: {0}", FileName);

    public string TitleFor(MergeSide side) => side == MergeSide.Current ? CurrentTitle : IncomingTitle;

    public string TextFor(MergeSide side) => side == MergeSide.Current ? CurrentText : IncomingText;

    /// <summary>The shell's services, which color the panes like the editor (#86); null in a test (plain text).</summary>
    public Codalus.Shell.Editor.DiffServices? Services { get; set; }

    /// <summary>The file's line ending (<c>\r\n</c>, <c>\n</c>), which the merge base's text takes in the result.</summary>
    public string LineEnding { get; }

    public string LineEndingName => LineEnding switch
    {
        "\r\n" => "CRLF",
        "\r" => "CR",
        _ => "LF",
    };

    /// <summary>The file's encoding as the status bar names it (<c>UTF-8</c>, <c>UTF-8 with BOM</c>).</summary>
    public string EncodingName { get; }

    /// <summary>The file's encoding (with its byte order mark, if it has one), which Accept Merge keeps; null for UTF-8.</summary>
    public Encoding? Encoding { get; }

    /// <summary><c>SPC</c> or <c>TABS</c>: how the file indents.</summary>
    public string IndentationName { get; }

    public int Total => _blocks.Count;

    /// <summary>Conflicts still to resolve.</summary>
    public int Remaining
    {
        get => _remaining;
        private set
        {
            if (SetField(ref _remaining, value))
            {
                OnPropertyChanged(nameof(CanAccept));
            }

            OnPropertyChanged(nameof(Summary));
        }
    }

    public bool CanAccept => _remaining == 0;

    /// <summary>The toolbar's count: <c>1 Conflict (1 Remaining)</c>.</summary>
    public string Summary => Total switch
    {
        0 => Localizer.Get("mergeEditor.noConflicts", "No Conflicts"),
        1 => Localizer.Format("mergeEditor.oneConflict", "1 Conflict ({0} Remaining)", _remaining),
        _ => Localizer.Format("mergeEditor.conflicts", "{0} Conflicts ({1} Remaining)", Total, _remaining),
    };

    /// <summary>Whether the result differs from what the editor opened with (the tab's <c>*</c>).</summary>
    public bool IsModified => !Result.UndoStack.IsOriginalFile;

    /// <summary>A header's check box: true when every conflict takes that side, false when none does, null between.</summary>
    public bool? AllTake(MergeSide side) =>
        _blocks.Count == 0 ? false : _blocks.All(b => b.Takes(side)) ? true : _blocks.Any(b => b.Takes(side)) ? null : false;

    /// <summary>The result's caret (the view keeps it up to date): the conflict commands act on the conflict there.</summary>
    public int CaretOffset
    {
        get => _caretOffset;
        set => _caretOffset = Math.Clamp(value, 0, Result.TextLength);
    }

    /// <summary>Asks the view to show a conflict (move the result's caret there and scroll to it).</summary>
    public event EventHandler<MergeConflictBlock>? NavigateRequested;

    /// <summary>The panes' zoom (1 is 100 %).</summary>
    public double Zoom
    {
        get => _zoom;
        set => SetField(ref _zoom, Math.Clamp(Math.Round(value, 2), 0.2, 4));
    }

    /// <summary><see cref="LayoutSourcesAbove"/> (the two versions above the result) or <see cref="LayoutThreeColumns"/>.</summary>
    public string Layout
    {
        get => _layout;
        set
        {
            if (SetField(ref _layout, value == LayoutThreeColumns ? LayoutThreeColumns : LayoutSourcesAbove))
            {
                SaveLayout?.Invoke(this);
            }
        }
    }

    /// <summary>Whether the incoming version is on the left and the current one on the right (the default).</summary>
    public bool IncomingOnLeft
    {
        get => _incomingOnLeft;
        set
        {
            if (SetField(ref _incomingOnLeft, value))
            {
                SaveLayout?.Invoke(this);
            }
        }
    }

    /// <summary>The side shown in the left pane.</summary>
    public MergeSide LeftSide => _incomingOnLeft ? MergeSide.Incoming : MergeSide.Current;

    public MergeSide RightSide => _incomingOnLeft ? MergeSide.Current : MergeSide.Incoming;

    /// <summary>Saves the layout choices (set by the shell: the <c>git.mergeEditor.*</c> settings).</summary>
    public Action<MergeEditorViewModel>? SaveLayout { get; set; }

    /// <summary>A command's shortcut as menus show it, for tooltips (set by the shell).</summary>
    public Func<string, string?>? ShortcutFor { get; set; }

    /// <summary>Sets the layout from the settings without saving it back.</summary>
    public void LoadLayout(string layout, bool incomingOnLeft)
    {
        var save = SaveLayout;
        SaveLayout = null;
        Layout = layout;
        IncomingOnLeft = incomingOnLeft;
        SaveLayout = save;
    }

    // ------------------------------------------------------------------ resolving

    /// <summary>Checks or unchecks a side's box on a conflict.</summary>
    public void SetTake(MergeConflictBlock block, MergeSide side, bool take)
    {
        var current = side == MergeSide.Current ? take : block.TakeCurrent;
        var incoming = side == MergeSide.Incoming ? take : block.TakeIncoming;
        Resolve(block, Combine(current, incoming));
    }

    /// <summary>A header's check box: checks or unchecks that side's box on every conflict, as one undo step.</summary>
    public void TakeAll(MergeSide side, bool take) =>
        ResolveMany(_blocks.Select(b => (b, Combine(side == MergeSide.Current ? take : b.TakeCurrent, side == MergeSide.Incoming ? take : b.TakeIncoming))));

    /// <summary>Puts <paramref name="resolution"/>'s text in the conflict's block (an undo step).</summary>
    public void Resolve(MergeConflictBlock block, MergeResolution resolution) => ResolveMany([(block, resolution)]);

    /// <summary>
    /// Resolves the conflict at the caret (or the next one after it): <c>current</c>, <c>incoming</c>, <c>both</c>
    /// (current then incoming) or <c>base</c>, then shows the next conflict still to resolve.
    /// </summary>
    public void Accept(string choice)
    {
        if (ConflictAt(_caretOffset) is { } block)
        {
            Resolve(block, ResolutionFor(choice));
            if ((_blocks.FirstOrDefault(b => b.Index > block.Index && !b.IsResolved) ?? _blocks.FirstOrDefault(b => !b.IsResolved)) is { } next)
            {
                Show(next);
            }
        }
    }

    /// <summary>Resolves every conflict the same way (one undo step).</summary>
    public void AcceptAll(string choice)
    {
        var resolution = ResolutionFor(choice);
        ResolveMany(_blocks.Select(b => (b, resolution)));
    }

    /// <summary>Unchecks every box and puts the merge base's text back in every block (one undo step).</summary>
    public void ResetAll() => ResolveMany(_blocks.Select(b => (b, MergeResolution.Unresolved)));

    private static MergeResolution ResolutionFor(string choice) => choice switch
    {
        "current" => MergeResolution.Current,
        "incoming" => MergeResolution.Incoming,
        "both" => MergeResolution.Both,
        "base" => MergeResolution.Base,
        _ => throw new ArgumentException($"Unknown choice '{choice}'.", nameof(choice)),
    };

    private static MergeResolution Combine(bool current, bool incoming) => (current, incoming) switch
    {
        (true, true) => MergeResolution.Both,
        (true, false) => MergeResolution.Current,
        (false, true) => MergeResolution.Incoming,
        _ => MergeResolution.Unresolved,
    };

    private void ResolveMany(IEnumerable<(MergeConflictBlock Block, MergeResolution Resolution)> changes)
    {
        var list = changes.Where(c => c.Block.Resolution != c.Resolution || Result.GetText(c.Block.ResultStart, c.Block.ResultLength) != c.Block.TextFor(c.Resolution)).ToList();
        if (list.Count == 0)
        {
            return;
        }

        // The blocks' places and states are put back by the undo stack itself: an undo of the text alone would leave
        // them where the edits moved them (an operation before the edits restores them on undo, one after on redo).
        var before = Snapshot();
        Result.BeginUpdate();
        try
        {
            Result.UndoStack.Push(new BlockStates(this, before, null));
            foreach (var (block, resolution) in list.OrderByDescending(c => c.Block.ResultStart))
            {
                _applying = block;
                try
                {
                    Result.Replace(block.ResultStart, block.ResultLength, block.TextFor(resolution));
                }
                finally
                {
                    _applying = null;
                }

                block.Resolution = resolution;
            }

            Result.UndoStack.Push(new BlockStates(this, null, Snapshot()));
        }
        finally
        {
            Result.EndUpdate();
        }

        Recount();
    }

    /// <summary>The conflict whose block holds <paramref name="offset"/>, else the next one after it, else the last.</summary>
    public MergeConflictBlock? ConflictAt(int offset) =>
        _blocks.FirstOrDefault(b => offset >= b.ResultStart && offset <= b.ResultStart + b.ResultLength)
        ?? _blocks.FirstOrDefault(b => b.ResultStart >= offset)
        ?? (_blocks.Count > 0 ? _blocks[^1] : null);

    /// <summary>Moves to the next (1) or previous (-1) conflict after or before the caret, round the end.</summary>
    public MergeConflictBlock? GoTo(int direction)
    {
        if (_blocks.Count == 0)
        {
            return null;
        }

        var caret = _caretOffset;
        var target = direction > 0
            ? _blocks.FirstOrDefault(b => b.ResultStart > caret) ?? _blocks[0]
            : _blocks.LastOrDefault(b => b.ResultStart < caret && !(caret <= b.ResultStart + b.ResultLength)) ?? _blocks[^1];
        Show(target);
        return target;
    }

    private void Show(MergeConflictBlock block)
    {
        _caretOffset = block.ResultStart;
        NavigateRequested?.Invoke(this, block);
    }

    /// <summary>Saves the result and marks the file resolved; every conflict must be resolved first.</summary>
    public Task AcceptAsync() =>
        _remaining > 0
            ? throw new InvalidOperationException(Localizer.Get("msg.mergeEditor.resolveEveryConflictFirst", "Resolve every conflict first: check a side's box, or edit its block in the result."))
            : _accept(Result.Text);

    // ------------------------------------------------------------------ the blocks as the result changes

    private void OnResultChanged(object? sender, DocumentChangeEventArgs e)
    {
        int o = e.Offset, removed = e.RemovalLength, inserted = e.InsertionLength;
        List<MergeConflictBlock>? edited = null;
        foreach (var block in _blocks)
        {
            if (block == _applying)
            {
                block.ResultLength += inserted - removed;
                continue;
            }

            int s = block.ResultStart, end = s + block.ResultLength;
            var insideInsert = removed == 0 && o == s && (block.ResultLength > 0 || !block.IsResolved);
            if (!insideInsert && o + removed <= s)
            {
                block.ResultStart += inserted - removed; // before the block
            }
            else if (!insideInsert && o >= end)
            {
                // after it
            }
            else
            {
                var newStart = Math.Min(s, o);
                var newEnd = end > o + removed ? end - removed + inserted : o + inserted;
                block.ResultStart = newStart;
                block.ResultLength = newEnd - newStart;
                (edited ??= []).Add(block);
            }
        }

        if (edited is not null)
        {
            foreach (var block in edited)
            {
                block.Resolution = Classify(block);
            }

            Recount();
        }
    }

    /// <summary>What a block's text says about it after an edit: a side's text, both, the base (unresolved), or typed.</summary>
    private MergeResolution Classify(MergeConflictBlock block)
    {
        var text = Result.GetText(block.ResultStart, block.ResultLength);
        if (block.Resolution != MergeResolution.Edited && text == block.TextFor(block.Resolution))
        {
            return block.Resolution;
        }

        foreach (var resolution in (ReadOnlySpan<MergeResolution>)[MergeResolution.Unresolved, MergeResolution.Current, MergeResolution.Incoming, MergeResolution.Both])
        {
            if (text == block.TextFor(resolution))
            {
                return resolution;
            }
        }

        return MergeResolution.Edited;
    }

    private void Recount()
    {
        Remaining = _blocks.Count(b => !b.IsResolved);
        OnPropertyChanged(nameof(Conflicts));
    }

    private (int Start, int Length, MergeResolution Resolution)[] Snapshot() =>
        [.. _blocks.Select(b => (b.ResultStart, b.ResultLength, b.Resolution))];

    private void Restore((int Start, int Length, MergeResolution Resolution)[] states)
    {
        for (var i = 0; i < _blocks.Count && i < states.Length; i++)
        {
            _blocks[i].ResultStart = states[i].Start;
            _blocks[i].ResultLength = states[i].Length;
            _blocks[i].Resolution = states[i].Resolution;
        }

        Recount();
    }

    /// <summary>Puts the blocks back as they were before (undo) or after (redo) a resolution.</summary>
    private sealed class BlockStates(
        MergeEditorViewModel owner,
        (int Start, int Length, MergeResolution Resolution)[]? onUndo,
        (int Start, int Length, MergeResolution Resolution)[]? onRedo) : IUndoableOperation
    {
        public void Undo()
        {
            if (onUndo is not null)
            {
                owner.Restore(onUndo);
            }
        }

        public void Redo()
        {
            if (onRedo is not null)
            {
                owner.Restore(onRedo);
            }
        }
    }

    // ------------------------------------------------------------------ building the panes

    private static string? Name(string? given, string? label) =>
        !string.IsNullOrWhiteSpace(given) ? given.Trim() : !string.IsNullOrWhiteSpace(label) ? label.Trim() : null;

    /// <summary>
    /// The result to show (the merge base's text for each conflict) and the two panes: each side's file from the index
    /// when every conflict's block can be found in it, else the result with that side's blocks taken.
    /// </summary>
    private (string Display, string Current, string Incoming) Build(string result, IReadOnlyList<MergeConflict> conflicts)
    {
        if (conflicts.Count == 0)
        {
            return (result, Ours.Length > 0 ? Ours : result, Theirs.Length > 0 ? Theirs : result);
        }

        // Each side's projection (the result with that side's blocks), and each block's lines in it.
        var oursProjection = new StringBuilder();
        var theirsProjection = new StringBuilder();
        var oursLines = new (int Start, int End)[conflicts.Count];
        var theirsLines = new (int Start, int End)[conflicts.Count];
        int oursLine = 0, theirsLine = 0, previous = 0;
        for (var i = 0; i < conflicts.Count; i++)
        {
            var common = result[previous..conflicts[i].Start];
            var commonLines = CountLines(common);
            oursProjection.Append(common).Append(conflicts[i].Ours);
            theirsProjection.Append(common).Append(conflicts[i].Theirs);
            oursLine += commonLines;
            theirsLine += commonLines;
            oursLines[i] = (oursLine, oursLine += CountLines(conflicts[i].Ours));
            theirsLines[i] = (theirsLine, theirsLine += CountLines(conflicts[i].Theirs));
            previous = conflicts[i].Start + conflicts[i].Length;
        }

        oursProjection.Append(result[previous..]);
        theirsProjection.Append(result[previous..]);

        var (current, currentRanges, currentFileLines) = Locate(Ours, oursProjection.ToString(), oursLines);
        var (incoming, incomingRanges, incomingFileLines) = Locate(Theirs, theirsProjection.ToString(), theirsLines);

        // The merge base's text of each conflict: from git's diff3 section, else found through a side's file.
        LineMap? oursToBase = Base is not null && currentFileLines is not null ? new LineMap(LineDiff.Lines(Ours), LineDiff.Lines(Base)) : null;
        LineMap? theirsToBase = Base is not null && incomingFileLines is not null && oursToBase is null ? new LineMap(LineDiff.Lines(Theirs), LineDiff.Lines(Base)) : null;
        var baseLines = Base is null ? [] : LineDiff.Lines(Base);

        var display = new StringBuilder();
        previous = 0;
        for (var i = 0; i < conflicts.Count; i++)
        {
            var conflict = conflicts[i];
            display.Append(result, previous, conflict.Start - previous);
            string @base;
            if (conflict.Base is not null)
            {
                @base = conflict.Base;
            }
            else if (oursToBase is not null && oursToBase.Map(currentFileLines![i].Start, currentFileLines[i].End) is var (b0, b1))
            {
                @base = Join(baseLines, b0, b1, LineEnding);
            }
            else if (theirsToBase is not null && theirsToBase.Map(incomingFileLines![i].Start, incomingFileLines[i].End) is var (t0, t1))
            {
                @base = Join(baseLines, t0, t1, LineEnding);
            }
            else
            {
                @base = string.Empty;
            }

            var block = new MergeConflictBlock(this, i, conflict, @base, currentRanges[i], incomingRanges[i]) { ResultStart = display.Length, ResultLength = @base.Length };
            _blocks.Add(block);
            display.Append(@base);
            previous = conflict.Start + conflict.Length;
        }

        display.Append(result, previous, result.Length - previous);
        return (display.ToString(), current, incoming);
    }

    /// <summary>
    /// A side's pane: its file from the index when each block's lines are found in it (by a line diff against the
    /// projection), else the projection. Returns the text, each block's characters in it, and its lines in the file.
    /// </summary>
    private static (string Text, MergeRange[] Ranges, (int Start, int End)[]? FileLines) Locate(string file, string projection, (int Start, int End)[] lines)
    {
        if (file.Length > 0)
        {
            var map = new LineMap(LineDiff.Lines(projection), LineDiff.Lines(file));
            var found = new (int Start, int End)[lines.Length];
            var ok = true;
            for (var i = 0; i < lines.Length && ok; i++)
            {
                if (map.MapExactly(lines[i].Start, lines[i].End) is { } range && (i == 0 || range.Start >= found[i - 1].End))
                {
                    found[i] = range;
                }
                else
                {
                    ok = false;
                }
            }

            if (ok)
            {
                return (file, Ranges(file, found), found);
            }
        }

        return (projection, Ranges(projection, lines), null);
    }

    private static MergeRange[] Ranges(string text, (int Start, int End)[] lines)
    {
        var starts = LineStarts(text);
        return [.. lines.Select(l => new MergeRange(starts[Math.Min(l.Start, starts.Count - 1)], starts[Math.Min(l.End, starts.Count - 1)] - starts[Math.Min(l.Start, starts.Count - 1)]))];
    }

    /// <summary>The offset where each line starts, then the text's length.</summary>
    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        if (starts[^1] != text.Length)
        {
            starts.Add(text.Length);
        }

        return starts;
    }

    private static int CountLines(string text)
    {
        var count = 0;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                count++;
            }
        }

        return text.Length > 0 && text[^1] != '\n' ? count + 1 : count;
    }

    private static string Join(string[] lines, int start, int end, string lineEnding)
    {
        var text = new StringBuilder();
        for (var i = start; i < end && i < lines.Length; i++)
        {
            text.Append(lines[i]).Append(lineEnding);
        }

        return text.ToString();
    }

    private static string DetectIndentation(string text)
    {
        int tabs = 0, spaces = 0;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith('\t'))
            {
                tabs++;
            }
            else if (line.StartsWith("  ", StringComparison.Ordinal))
            {
                spaces++;
            }
        }

        return tabs > spaces ? "TABS" : "SPC";
    }

    /// <summary>A line diff from A to B, mapping ranges of A's lines to B's.</summary>
    private sealed class LineMap
    {
        private readonly int[] _equal; // A line → B line, or -1
        private readonly int[] _hunkOf; // A line → its change hunk, or -1
        private readonly List<(int AStart, int AEnd, int BStart, int BEnd)> _hunks = [];
        private readonly int _aCount;
        private readonly int _bCount;

        public LineMap(string[] a, string[] b)
        {
            _aCount = a.Length;
            _bCount = b.Length;
            _equal = new int[a.Length];
            _hunkOf = new int[a.Length];
            Array.Fill(_equal, -1);
            Array.Fill(_hunkOf, -1);
            int aPos = 0, bPos = 0, hunkA = -1, hunkB = -1;
            foreach (var op in LineDiff.Diff(a, b))
            {
                if (op.Kind == ' ')
                {
                    if (hunkA >= 0)
                    {
                        _hunks.Add((hunkA, aPos, hunkB, bPos));
                        hunkA = -1;
                    }

                    _equal[op.OldIndex] = op.NewIndex;
                    aPos++;
                    bPos++;
                    continue;
                }

                if (hunkA < 0)
                {
                    (hunkA, hunkB) = (aPos, bPos);
                }

                if (op.Kind == '-')
                {
                    _hunkOf[op.OldIndex] = _hunks.Count;
                    aPos++;
                }
                else
                {
                    bPos++;
                }
            }

            if (hunkA >= 0)
            {
                _hunks.Add((hunkA, aPos, hunkB, bPos));
            }
        }

        /// <summary>A's lines [start, end) in B, where those lines are B's own (unchanged); null when they aren't.</summary>
        public (int Start, int End)? MapExactly(int start, int end)
        {
            if (start < end)
            {
                return start < _aCount && end <= _aCount && _equal[start] >= 0 && _equal[end - 1] >= 0 && _equal[end - 1] >= _equal[start]
                    ? (_equal[start], _equal[end - 1] + 1)
                    : null;
            }

            var at = Boundary(start);
            return at is { } b ? (b, b) : null;
        }

        /// <summary>A's lines [start, end) in B, widened to the changes they touch (the B text they replaced).</summary>
        public (int Start, int End)? Map(int start, int end)
        {
            if (start >= end)
            {
                // Lines A removed: the change at this place, if there is one.
                foreach (var hunk in _hunks)
                {
                    if (hunk.AStart == start && hunk.AEnd == start)
                    {
                        return (hunk.BStart, hunk.BEnd);
                    }
                }

                return Boundary(start) is { } b ? (b, b) : null;
            }

            if (end > _aCount)
            {
                return null;
            }

            var from = _equal[start] >= 0 ? _equal[start] : _hunkOf[start] >= 0 ? _hunks[_hunkOf[start]].BStart : -1;
            var to = _equal[end - 1] >= 0 ? _equal[end - 1] + 1 : _hunkOf[end - 1] >= 0 ? _hunks[_hunkOf[end - 1]].BEnd : -1;
            return from >= 0 && to >= from ? (from, to) : null;
        }

        /// <summary>The place between lines in B that the place before A's line <paramref name="line"/> maps to.</summary>
        private int? Boundary(int line)
        {
            if (line > 0 && line - 1 < _aCount && _equal[line - 1] >= 0)
            {
                return _equal[line - 1] + 1;
            }

            if (line < _aCount && _equal[line] >= 0)
            {
                return _equal[line];
            }

            return line == 0 ? 0 : line >= _aCount ? _bCount : null;
        }
    }

    // ------------------------------------------------------------------ git's markers

    /// <summary>The conflicts in <paramref name="text"/>, in order.</summary>
    public static IReadOnlyList<MergeConflict> Parse(string text)
    {
        var conflicts = new List<MergeConflict>();
        var offset = 0;
        var lines = SplitKeepingEnds(text);
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith("<<<<<<<", StringComparison.Ordinal))
            {
                offset += lines[i].Length;
                continue;
            }

            var start = offset;
            var oursLabel = lines[i][7..].Trim();
            var ours = new StringBuilder();
            StringBuilder? @base = null;
            var theirs = new StringBuilder();
            var section = 0; // 0 ours, 1 base, 2 theirs
            var length = lines[i].Length;
            var closed = false;
            var j = i + 1;
            for (; j < lines.Count; j++)
            {
                length += lines[j].Length;
                if (lines[j].StartsWith("|||||||", StringComparison.Ordinal) && section == 0)
                {
                    section = 1;
                    @base = new StringBuilder();
                }
                else if (lines[j].StartsWith("=======", StringComparison.Ordinal) && section < 2)
                {
                    section = 2;
                }
                else if (lines[j].StartsWith(">>>>>>>", StringComparison.Ordinal) && section == 2)
                {
                    conflicts.Add(new MergeConflict(start, length, ours.ToString(), @base?.ToString(), theirs.ToString(), oursLabel, lines[j][7..].Trim()));
                    closed = true;
                    break;
                }
                else
                {
                    (section == 0 ? ours : section == 1 ? @base! : theirs).Append(lines[j]);
                }
            }

            if (!closed)
            {
                break;
            }

            offset = start + length;
            i = j;
        }

        return conflicts;
    }

    private static List<string> SplitKeepingEnds(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines.Add(text[start..(i + 1)]);
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    /// <summary>What replaces a conflict: <c>current</c>, <c>incoming</c>, <c>both</c> (current then incoming) or <c>base</c>.</summary>
    public static string ReplacementFor(MergeConflict conflict, string choice) => choice switch
    {
        "current" => conflict.Ours,
        "incoming" => conflict.Theirs,
        "both" => conflict.Ours + (conflict.Ours.Length > 0 && !conflict.Ours.EndsWith('\n') ? "\n" : string.Empty) + conflict.Theirs,
        "base" => conflict.Base ?? string.Empty,
        _ => throw new ArgumentException($"Unknown choice '{choice}'.", nameof(choice)),
    };
}

/// <summary>A merge editor tab: the file's name, with <c>*</c> while the result has changes.</summary>
public sealed class MergeEditorDocument : ShellDocument
{
    public MergeEditorDocument(MergeEditorViewModel viewModel, EditorFont font)
    {
        ViewModel = viewModel;
        Font = font;
        Id = IdFor(viewModel.Path);
        UpdateTitle();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MergeEditorViewModel.IsModified))
            {
                UpdateTitle();
            }
        };
    }

    public override string Kind => "mergeEditor";

    public override string? FilePath => ViewModel.Path;

    public MergeEditorViewModel ViewModel { get; }

    public EditorFont Font { get; }

    // Closing the tab leaves the file as git left it, as before: nothing is written until Accept Merge.
    protected override bool NeedsSaveOnClose => false;

    public static string IdFor(string path) => "merge:" + path;

    // The mark is in the title ("Greeter.cs*"); Dock's IsModified stays false, or its tab would add a second " *".
    private void UpdateTitle() => Title = ViewModel.FileName + (ViewModel.IsModified ? "*" : string.Empty);
}
