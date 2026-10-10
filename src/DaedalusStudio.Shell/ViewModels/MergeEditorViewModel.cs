using System.Text;
using DaedalusStudio.Shell.Docking;
using DaedalusStudio.Shell.Infrastructure;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>
/// A conflict in the result text: the whole marked region (<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> to
/// <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> line, offsets), each side's text, and the common ancestor's when git wrote it
/// (<c>merge.conflictStyle=diff3</c> or <c>zdiff3</c>).
/// </summary>
public sealed record MergeConflict(int Start, int Length, string Ours, string? Base, string Theirs, string OursLabel, string TheirsLabel);

/// <summary>
/// The 3-way merge editor (M13): the current version (ours), the incoming one (theirs), and the editable result, which
/// starts as the file with git's conflict markers. Each conflict is resolved by accepting a side, both, or editing;
/// Accept Merge saves the result and marks the file resolved (or, for <c>daedalus merge</c>, writes the merged file).
/// </summary>
public sealed class MergeEditorViewModel : ObservableObject
{
    private readonly Func<string, Task> _accept;
    private int _remaining;

    public MergeEditorViewModel(string path, string result, string? ours, string? theirs, string? @base, Func<string, Task> accept, string oursTitle = "Current (ours)", string theirsTitle = "Incoming (theirs)")
    {
        Path = path;
        InitialResult = result;
        Ours = ours ?? string.Empty;
        Theirs = theirs ?? string.Empty;
        Base = @base;
        OursTitle = oursTitle;
        TheirsTitle = theirsTitle;
        _accept = accept;
        Remaining = Parse(result).Count;
    }

    public string Path { get; }

    public string InitialResult { get; }

    public string Ours { get; }

    public string Theirs { get; }

    public string? Base { get; }

    public string OursTitle { get; }

    public string TheirsTitle { get; }

    /// <summary>Conflicts left in the result (the view updates it as the text changes).</summary>
    public int Remaining
    {
        get => _remaining;
        set
        {
            if (SetField(ref _remaining, value))
            {
                OnPropertyChanged(nameof(Summary));
                OnPropertyChanged(nameof(CanAccept));
            }
        }
    }

    public bool CanAccept => _remaining == 0;

    public string Summary => _remaining == 0 ? "No conflicts left. Accept Merge to save and mark the file resolved." : _remaining == 1 ? "1 conflict left." : $"{_remaining} conflicts left.";

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

    /// <summary>Saves the result (it must have no conflicts left) and marks the file resolved.</summary>
    public Task AcceptAsync(string result) =>
        Parse(result).Count > 0
            ? throw new InvalidOperationException("Resolve every conflict first.")
            : _accept(result);
}

/// <summary>A merge editor tab.</summary>
public sealed class MergeEditorDocument : ShellDocument
{
    public MergeEditorDocument(MergeEditorViewModel viewModel, EditorFont font)
    {
        ViewModel = viewModel;
        Font = font;
        Id = IdFor(viewModel.Path);
        Title = "Merging " + System.IO.Path.GetFileName(viewModel.Path);
    }

    public override string Kind => "mergeEditor";

    public MergeEditorViewModel ViewModel { get; }

    public EditorFont Font { get; }

    public static string IdFor(string path) => "merge:" + path;
}
