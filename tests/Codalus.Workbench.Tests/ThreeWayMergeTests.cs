using Codalus.Workbench.Git;

namespace Codalus.Workbench.Tests;

/// <summary>
/// The merge editor's own 3-way merge (ADR 0020, amended 2026-11-18): diff3's chunks, each side aligned with the base
/// ignoring whitespace, and a change decided on the exact text.
/// </summary>
public sealed class ThreeWayMergeTests
{
    [Fact]
    public void A_change_only_one_side_made_is_taken_and_changes_to_different_lines_merge()
    {
        const string Base = "a\nb\nc\nd\ne\n";
        Assert.Equal("a\nB\nc\nd\ne\n", ThreeWayMerge.Merge(Base, "a\nB\nc\nd\ne\n", Base));
        Assert.Equal("a\nb\nc\nd\nE\n", ThreeWayMerge.Merge(Base, Base, "a\nb\nc\nd\nE\n"));
        Assert.Equal("a\nB\nc\nd\nE\n", ThreeWayMerge.Merge(Base, "a\nB\nc\nd\ne\n", "a\nb\nc\nd\nE\n"));

        // The same change on both sides is taken once; a line both deleted is gone.
        Assert.Equal("a\nX\nc\nd\ne\n", ThreeWayMerge.Merge(Base, "a\nX\nc\nd\ne\n", "a\nX\nc\nd\ne\n"));
        Assert.Equal("a\nc\nd\ne\n", ThreeWayMerge.Merge(Base, "a\nc\nd\ne\n", "a\nc\nd\ne\n"));
    }

    [Fact]
    public void Different_changes_to_the_same_lines_are_a_conflict_with_diff3_markers()
    {
        Assert.Equal(
            "a\n<<<<<<< HEAD\nmine\n||||||| base\nold\n=======\ntheirs\n>>>>>>> feature\nz\n",
            ThreeWayMerge.Merge("a\nold\nz\n", "a\nmine\nz\n", "a\ntheirs\nz\n", "HEAD", "base", "feature"));

        // Changes to neighboring lines conflict, as in git, and become one conflict.
        Assert.Equal(
            "a\n<<<<<<< ours\nB\nc\n||||||| base\nb\nc\n=======\nb\nC\n>>>>>>> theirs\nd\n",
            ThreeWayMerge.Merge("a\nb\nc\nd\n", "a\nB\nc\nd\n", "a\nb\nC\nd\n"));

        // A side without a final line break still leaves each marker on a line of its own.
        Assert.Equal(
            "a\n<<<<<<< ours\nmine\n||||||| base\nold\n=======\ntheirs\n>>>>>>> theirs\n",
            ThreeWayMerge.Merge("a\nold\n", "a\nmine", "a\ntheirs\n"));
    }

    [Fact]
    public void A_re_indented_block_lines_up_with_its_old_self_and_conflicts_only_on_the_line_the_other_side_edited()
    {
        // The current side wrapped a button in a new container (re-indenting it) and changed its icon; the incoming side
        // changed its title. Exact lines match nothing in the block, so a merge on exact lines pairs it with whatever
        // matches; ignoring whitespace, the button lines up with itself.
        const string Base = """
            <div class="toolbar">
                <Button Icon="send"
                        Theme="Primary"
                        Title="Send"
                        OnClick="SendAsync" />
            </div>

            """;
        const string Ours = """
            <div class="toolbar">
                <div class="slot">
                    <Button Icon="arrow-up"
                            Theme="Primary"
                            Title="Send"
                            OnClick="SendAsync" />
                </div>
            </div>

            """;
        const string Theirs = """
            <div class="toolbar">
                <Button Icon="send"
                        Theme="Primary"
                        Title="@SendText"
                        OnClick="SendAsync" />
            </div>

            """;

        Assert.Equal(
            """
            <div class="toolbar">
                <div class="slot">
                    <Button Icon="arrow-up"
                            Theme="Primary"
            <<<<<<< HEAD
                            Title="Send"
            ||||||| base
                        Title="Send"
            =======
                        Title="@SendText"
            >>>>>>> master
                            OnClick="SendAsync" />
                </div>
            </div>

            """,
            ThreeWayMerge.Merge(Base, Ours, Theirs, "HEAD", "base", "master"));

        // Re-indenting alone merges with an edit of another line.
        Assert.Equal("  x\n  y\nZ\n", ThreeWayMerge.Merge("x\ny\nz\n", "  x\n  y\nz\n", "x\ny\nZ\n"));
    }

    [Fact]
    public void Remerge_takes_the_labels_and_line_endings_of_gits_conflicted_file()
    {
        const string Git = "a\r\n<<<<<<< HEAD\r\nmine\r\n||||||| parent of abc123\r\nold\r\n=======\r\ntheirs\r\n>>>>>>> master\r\nz\r\n";
        Assert.Equal(
            "a\r\n<<<<<<< HEAD\r\nmine\r\n||||||| parent of abc123\r\nold\r\n=======\r\ntheirs\r\n>>>>>>> master\r\nz\r\n",
            ThreeWayMerge.Remerge("a\nold\nz\n", "a\nmine\nz\n", "a\ntheirs\nz\n", Git));

        // git's file without a base section: diff3 markers all the same, the base named "base".
        Assert.Equal(
            "a\n<<<<<<< HEAD\nmine\n||||||| base\nold\n=======\ntheirs\n>>>>>>> master\nz\n",
            ThreeWayMerge.Remerge("a\nold\nz\n", "a\nmine\nz\n", "a\ntheirs\nz\n", "a\n<<<<<<< HEAD\nmine\n=======\ntheirs\n>>>>>>> master\nz\n"));

        Assert.Null(ThreeWayMerge.Remerge("a\n", "b\n", "c\n", "no markers\n"));
        Assert.Equal(("HEAD", (string?)null, "feature"), ThreeWayMerge.ConflictLabels("x\n<<<<<<< HEAD\r\n1\n=======\n2\n>>>>>>> feature\n"));
    }
}
