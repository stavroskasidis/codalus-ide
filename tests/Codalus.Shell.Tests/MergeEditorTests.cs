using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>
/// The merge editor (M13, redesigned 2026-10-19): check boxes beside each conflict's block and in the headers, the merge
/// base's text in the result, the count, the layout, the commands, and the panes scrolling together.
/// </summary>
public sealed class MergeEditorTests
{
    private const string Base = "a\nold\nz\n";
    private const string Ours = "a\nmine\nz\n";
    private const string Theirs = "a\ntheirs\nz\n";
    private const string Merged = "a\n<<<<<<< HEAD\nmine\n=======\ntheirs\n>>>>>>> feature\nz\n";

    // Two conflicts, and a change only the incoming side made (q → Q), which git merged on its own.
    private const string Base2 = "a\nb1\nc\nd1\ne\nq\n";
    private const string Ours2 = "a\nB1\nc\nD1\ne\nq\n";
    private const string Theirs2 = "a\nX1\nc\nY1\ne\nQ\n";
    private const string Merged2 = "a\n<<<<<<< HEAD\nB1\n=======\nX1\n>>>>>>> feature\nc\n<<<<<<< HEAD\nD1\n=======\nY1\n>>>>>>> feature\ne\nQ\n";

    private static MergeEditorViewModel ViewModel(string merged = Merged, string? ours = Ours, string? theirs = Theirs, string? @base = Base, Func<string, Task>? accept = null) =>
        new("Directory.Build.props", merged, ours, theirs, @base, accept ?? (_ => Task.CompletedTask), "develop");

    [AvaloniaFact]
    public void The_result_shows_the_base_text_of_each_unresolved_conflict_instead_of_markers()
    {
        var vm = ViewModel();
        Assert.Equal(Base, vm.Result.Text);
        var block = Assert.Single(vm.Conflicts);
        Assert.Equal(("old\n", MergeResolution.Unresolved), (vm.Result.GetText(block.Result.Start, block.Result.Length), block.Resolution));

        // The panes are the two versions from the index, each block found in them.
        Assert.Equal(Ours, vm.CurrentText);
        Assert.Equal(Theirs, vm.IncomingText);
        Assert.Equal("mine\n", vm.CurrentText.Substring(block.Current.Start, block.Current.Length));
        Assert.Equal("theirs\n", vm.IncomingText.Substring(block.Incoming.Start, block.Incoming.Length));

        // The base from diff3 markers when git wrote them; with no base at all (both added), the block is empty.
        var diff3 = ViewModel("a\n<<<<<<< HEAD\nmine\n||||||| base\nancient\n=======\ntheirs\n>>>>>>> feature\nz\n", @base: null);
        Assert.Equal("a\nancient\nz\n", diff3.Result.Text);
        var added = ViewModel(@base: null);
        Assert.Equal("a\nz\n", added.Result.Text);
        Assert.Equal(0, added.Conflicts[0].Result.Length);

        // Each conflict's own base, with a change git merged on its own kept in the result, and the current pane the
        // current file as it is (without that change).
        var two = ViewModel(Merged2, Ours2, Theirs2, Base2);
        Assert.Equal("a\nb1\nc\nd1\ne\nQ\n", two.Result.Text);
        Assert.Equal(Ours2, two.CurrentText);
        Assert.Equal(["B1\n", "D1\n"], two.Conflicts.Select(b => two.CurrentText.Substring(b.Current.Start, b.Current.Length)));
        Assert.Equal(["X1\n", "Y1\n"], two.Conflicts.Select(b => two.IncomingText.Substring(b.Incoming.Start, b.Incoming.Length)));

        // A CRLF file: the base (from the index, LF) takes the file's line endings.
        var crlf = ViewModel(Merged.Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.Equal(Base.Replace("\n", "\r\n", StringComparison.Ordinal), crlf.Result.Text);
        Assert.Equal("CRLF", crlf.LineEndingName);
    }

    [AvaloniaFact]
    public void Checking_a_side_takes_its_block_checking_both_takes_both_and_unchecking_takes_it_out()
    {
        var vm = ViewModel();
        var block = vm.Conflicts[0];

        block.TakeIncoming = true;
        Assert.Equal("a\ntheirs\nz\n", vm.Result.Text);
        Assert.Equal((MergeResolution.Incoming, 0), (block.Resolution, vm.Remaining));

        // Both: current first, then incoming, whichever was checked first.
        block.TakeCurrent = true;
        Assert.Equal("a\nmine\ntheirs\nz\n", vm.Result.Text);
        Assert.Equal(MergeResolution.Both, block.Resolution);
        var other = ViewModel();
        other.Conflicts[0].TakeCurrent = true;
        other.Conflicts[0].TakeIncoming = true;
        Assert.Equal(vm.Result.Text, other.Result.Text);

        // Unchecking one leaves the other; unchecking both puts the base back, unresolved.
        block.TakeIncoming = false;
        Assert.Equal("a\nmine\nz\n", vm.Result.Text);
        block.TakeCurrent = false;
        Assert.Equal(Base, vm.Result.Text);
        Assert.Equal((MergeResolution.Unresolved, 1), (block.Resolution, vm.Remaining));

        // Undo and redo bring the text and the boxes back together.
        vm.Result.UndoStack.Undo();
        Assert.Equal(("a\nmine\nz\n", true, false), (vm.Result.Text, block.TakeCurrent, block.TakeIncoming));
        vm.Result.UndoStack.Undo();
        Assert.Equal(("a\nmine\ntheirs\nz\n", MergeResolution.Both), (vm.Result.Text, block.Resolution));
        vm.Result.UndoStack.Redo();
        Assert.Equal(("a\nmine\nz\n", MergeResolution.Current), (vm.Result.Text, block.Resolution));
    }

    [AvaloniaFact]
    public void A_headers_box_takes_every_conflict_from_its_side()
    {
        var vm = ViewModel(Merged2, Ours2, Theirs2, Base2);
        Assert.Equal(false, vm.AllTake(MergeSide.Incoming));

        vm.Conflicts[1].TakeIncoming = true;
        Assert.Null(vm.AllTake(MergeSide.Incoming)); // some: the box is mixed

        vm.TakeAll(MergeSide.Incoming, true);
        Assert.Equal("a\nX1\nc\nY1\ne\nQ\n", vm.Result.Text);
        Assert.Equal((true, false, 0), (vm.AllTake(MergeSide.Incoming), vm.AllTake(MergeSide.Current), vm.Remaining));

        // The other header adds its side to every conflict (both, current first); unchecking it takes it out again.
        vm.TakeAll(MergeSide.Current, true);
        Assert.Equal("a\nB1\nX1\nc\nD1\nY1\ne\nQ\n", vm.Result.Text);
        vm.TakeAll(MergeSide.Current, false);
        vm.TakeAll(MergeSide.Incoming, false);
        Assert.Equal("a\nb1\nc\nd1\ne\nQ\n", vm.Result.Text);
        Assert.Equal(2, vm.Remaining);

        // One undo step per header click.
        vm.Result.UndoStack.Undo();
        Assert.Equal("a\nX1\nc\nY1\ne\nQ\n", vm.Result.Text);
    }

    [AvaloniaFact]
    public void The_count_says_how_many_conflicts_remain_and_typing_in_a_block_resolves_it()
    {
        var vm = ViewModel(Merged2, Ours2, Theirs2, Base2);
        Assert.Equal(("2 Conflicts (2 Remaining)", false), (vm.Summary, vm.CanAccept));
        Assert.Equal("1 Conflict (1 Remaining)", ViewModel().Summary);

        vm.Conflicts[0].TakeCurrent = true;
        Assert.Equal(("2 Conflicts (1 Remaining)", false), (vm.Summary, vm.CanAccept));

        // Typing in the second block's base text resolves it with the typed text.
        var second = vm.Conflicts[1];
        vm.Result.Insert(second.Result.Start + 2, "-edited");
        Assert.Equal((MergeResolution.Edited, "d1-edited\n"), (second.Resolution, vm.Result.GetText(second.Result.Start, second.Result.Length)));
        Assert.Equal(("2 Conflicts (0 Remaining)", true), (vm.Summary, vm.CanAccept));

        // Undoing the typing makes it unresolved again; typing outside the blocks changes nothing.
        vm.Result.UndoStack.Undo();
        Assert.Equal((MergeResolution.Unresolved, 1), (second.Resolution, vm.Remaining));
        vm.Result.Insert(0, "// top\n");
        Assert.Equal(1, vm.Remaining);
        Assert.Equal("d1\n", vm.Result.GetText(second.Result.Start, second.Result.Length));
        Assert.True(vm.IsModified);
    }

    [AvaloniaFact]
    public async Task Accept_merge_writes_the_result_once_every_conflict_is_resolved()
    {
        string? written = null;
        var vm = ViewModel(accept: text =>
        {
            written = text;
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<InvalidOperationException>(vm.AcceptAsync);
        Assert.Null(written);

        vm.Accept("both");
        await vm.AcceptAsync();
        Assert.Equal("a\nmine\ntheirs\nz\n", written);
    }

    [AvaloniaFact]
    public async Task The_editor_has_check_boxes_branch_headers_a_compact_toolbar_and_a_status_bar()
    {
        await using var f = ShellFixture.Create();
        var (local, remote, @base, merged) = Files(f, Ours2, Theirs2, Base2, Merged2);
        var merging = f.Shell.MergeFilesAsync(local, remote, @base, merged);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<MergeEditorView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<MergeEditorView>().Single();
        var document = f.Shell.Layout.Documents.OfType<MergeEditorDocument>().Single();
        var vm = view.ViewModel;

        // The tab is the file's name; the headers name the sides by their branches (from git's markers here).
        Assert.Equal("merged.txt", document.Title);
        Assert.Equal(("Current: HEAD", "Incoming: feature", "Result: merged.txt"), (vm.CurrentTitle, vm.IncomingTitle, vm.ResultTitle));
        Assert.Equal("2 Conflicts (2 Remaining)", view.SummaryText);
        Assert.False(view.CanAcceptMerge);
        Assert.Equal("a\nb1\nc\nd1\ne\nQ\n", view.ResultText);

        // A gutter check box per block on each side: checking takes it, and the tab shows the change.
        var incoming = view.CheckBoxesFor(MergeSide.Incoming);
        Assert.Equal(2, incoming.Count);
        Assert.Equal(2, view.CheckBoxesFor(MergeSide.Current).Count);
        ShellFixture.Pump();
        Assert.All(incoming, box => Assert.True(box.IsEffectivelyVisible && box.Bounds.Height > 0, "the gutter's check boxes show"));
        Assert.True(incoming[0].Bounds.Y < incoming[1].Bounds.Y, "each check box is beside its block");
        Click(incoming[0]);
        Assert.Equal("a\nX1\nc\nd1\ne\nQ\n", view.ResultText);
        Assert.True(incoming[0].IsChecked);
        Assert.Equal("merged.txt*", document.Title);
        Assert.Equal("2 Conflicts (1 Remaining)", view.SummaryText);

        // The header's box: mixed, then every conflict from that side.
        Assert.Null(view.HeaderFor(MergeSide.Incoming).IsChecked);
        Click(view.HeaderFor(MergeSide.Incoming));
        Assert.Equal("a\nX1\nc\nY1\ne\nQ\n", view.ResultText);
        Assert.True(view.HeaderFor(MergeSide.Incoming).IsChecked);
        Assert.True(incoming[1].IsChecked);
        Assert.True(view.CanAcceptMerge);

        // The status bar: zoom, caret, indentation, line endings, encoding.
        var status = f.Window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("SPC", status);
        Assert.Contains("LF", status);
        Assert.Contains("UTF-8", status);
        Assert.Contains(status, t => t is not null && t.StartsWith("Ln: ", StringComparison.Ordinal));
        vm.Zoom = 1.5;
        Assert.Equal(f.Shell.EditorFont.Size * 1.5, view.Result.FontSize);

        // Every pane scrolls sideways.
        Assert.All([view.Result, view.EditorFor(MergeSide.Current), view.EditorFor(MergeSide.Incoming)], e => Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Visible, e.HorizontalScrollBarVisibility));

        await view.AcceptMergeAsync();
        Assert.True(await merging);
        Assert.Equal("a\nX1\nc\nY1\ne\nQ\n", File.ReadAllText(merged));
    }

    [AvaloniaFact]
    public async Task The_commands_move_between_conflicts_and_take_sides_and_the_layout_is_kept()
    {
        await using var f = ShellFixture.Create();
        var (local, remote, @base, merged) = Files(f, Ours2, Theirs2, Base2, Merged2);
        _ = f.Shell.MergeFilesAsync(local, remote, @base, merged);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<MergeEditorView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<MergeEditorView>().Single();
        var vm = view.ViewModel;
        ShellFixture.Pump();

        // The editor opens on the first conflict; Next Conflict (F8) moves to the second, and the take commands act there.
        Assert.Equal(vm.Conflicts[0].Result.Start, view.Result.CaretOffset);
        f.Shell.Run("git.mergeEditor.nextConflict");
        await ShellFixture.PumpUntil(() => view.Result.CaretOffset == vm.Conflicts[1].Result.Start);
        f.Shell.Run("git.mergeEditor.takeCurrent");
        await ShellFixture.PumpUntil(() => vm.Conflicts[1].Resolution == MergeResolution.Current);
        Assert.Equal(MergeResolution.Unresolved, vm.Conflicts[0].Resolution);

        // Taking a side moves on to the next conflict still to resolve (round the end: the first).
        await ShellFixture.PumpUntil(() => view.Result.CaretOffset == vm.Conflicts[0].Result.Start);
        f.Shell.Run("git.mergeEditor.previousConflict");
        await ShellFixture.PumpUntil(() => view.Result.CaretOffset == vm.Conflicts[1].Result.Start);
        f.Shell.Run("git.mergeEditor.nextConflict");
        await ShellFixture.PumpUntil(() => view.Result.CaretOffset == vm.Conflicts[0].Result.Start);
        f.Shell.Run("git.mergeEditor.takeBoth");
        await ShellFixture.PumpUntil(() => vm.Conflicts[0].Resolution == MergeResolution.Both);
        f.Shell.Run("git.mergeEditor.takeAllIncoming");
        await ShellFixture.PumpUntil(() => vm.Conflicts.All(b => b.TakeIncoming));
        Assert.Equal("a\nB1\nX1\nc\nD1\nY1\ne\nQ\n", view.ResultText);
        Assert.Equal("F8", f.Shell.Workbench.Keybindings.GetBinding("git.mergeEditor.nextConflict")?.ToLabel(f.Shell.Workbench.Platform));

        // The gear menu's layout: saved to the settings, and the panes swap or go into three columns.
        Assert.Equal((MergeEditorViewModel.LayoutSourcesAbove, true, MergeSide.Incoming), (vm.Layout, vm.IncomingOnLeft, vm.LeftSide));
        vm.IncomingOnLeft = false;
        vm.Layout = MergeEditorViewModel.LayoutThreeColumns;
        ShellFixture.Pump();
        Assert.Equal(MergeSide.Current, vm.LeftSide);
        var result = view.Result.FindAncestorOfType<DockPanel>()!;
        Assert.Equal(2, Grid.GetColumn(result));
        Assert.Equal(0, Grid.GetColumn(view.EditorFor(MergeSide.Current).FindAncestorOfType<DockPanel>()!));
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Settings.GetString(BuiltIns.MergeEditorLayoutSetting) == MergeEditorViewModel.LayoutThreeColumns
                                           && !f.Shell.Workbench.Settings.GetBoolean(BuiltIns.MergeEditorIncomingOnLeftSetting, true));
    }

    [AvaloniaFact]
    public async Task The_panes_scroll_together_with_their_conflicts_lined_up()
    {
        // Lines only the current side has before the conflict (and the result, which git merged them into): the panes
        // line up at the conflict, not by line number.
        var common = string.Concat(Enumerable.Range(0, 80).Select(i => $"line {i} with some text to scroll sideways through, wider than the pane is at its default size\n"));
        var ours = "extra 1\nextra 2\nextra 3\nstart\nmine\n" + common;
        var theirs = "start\ntheirs\n" + common;
        var @base = "start\nold\n" + common;
        var merged = "extra 1\nextra 2\nextra 3\nstart\n<<<<<<< HEAD\nmine\n=======\ntheirs\n>>>>>>> feature\n" + common;
        await using var f = ShellFixture.Create();
        var (l, r, b, m) = Files(f, ours, theirs, @base, merged);
        _ = f.Shell.MergeFilesAsync(l, r, b, m);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<MergeEditorView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<MergeEditorView>().Single();
        ShellFixture.Pump();

        var result = view.Result.TextArea.TextView;
        var incoming = view.EditorFor(MergeSide.Incoming).TextArea.TextView;
        var lineHeight = result.DefaultLineHeight;
        Assert.Equal("extra 1\nextra 2\nextra 3\nstart\nold\n" + common, view.ResultText);
        var current = view.EditorFor(MergeSide.Current).TextArea.TextView;
        ((Avalonia.Controls.Primitives.IScrollable)result).Offset = new Avalonia.Vector(0, 4 * lineHeight); // the result's conflict line
        ShellFixture.Pump();
        Assert.Equal(1 * lineHeight, incoming.ScrollOffset.Y, 1); // the incoming side's conflict line (it has no extra lines)
        Assert.Equal(4 * lineHeight, current.ScrollOffset.Y, 1);

        // Sideways too: the narrow source panes (the wide result has nothing to scroll here).
        ((Avalonia.Controls.Primitives.IScrollable)incoming).Offset = new Avalonia.Vector(40, 1 * lineHeight);
        ShellFixture.Pump();
        Assert.Equal(40, current.ScrollOffset.X, 1);
        Assert.Equal(4 * lineHeight, result.ScrollOffset.Y, 1);

        Assert.Equal(1.5, MergeEditorView.MapLine([0, 2, 4, 10], [0, 1, 3, 9], 2.5), 3);
    }

    private static (string Local, string Remote, string Base, string Merged) Files(ShellFixture f, string ours, string theirs, string @base, string merged)
    {
        string Write(string name, string text)
        {
            var path = Path.Combine(f.UserData, name);
            File.WriteAllText(path, text);
            return path;
        }

        return (Write("LOCAL.txt", ours), Write("REMOTE.txt", theirs), Write("BASE.txt", @base), Write("merged.txt", merged));
    }

    /// <summary>Clicks a check box as a pointer would: toggles it and raises Click.</summary>
    private static void Click(CheckBox box)
    {
        box.IsChecked = box.IsChecked != true;
        box.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }
}
