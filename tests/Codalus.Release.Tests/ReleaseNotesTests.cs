namespace Codalus.Release.Tests;

public sealed class ReleaseNotesTests
{
    private const string Repository = "owner/codalus";

    [Fact]
    public void Commits_are_grouped_by_milestone_and_linked_to_its_checkpoints()
    {
        var notes = ReleaseNotes.Build(new ReleaseNotesInput(
            "0.2.0",
            Repository,
            "v0.2.0",
            "v0.1.0",
            [
                new Commit("aaaaaaa", "M9 and M10: packages, project management, terminal"),
                new Commit("bbbbbbb", "Fix a flaky test"),
                new Commit("ccccccc", "M16d: web front-end depth, completing M16"),
                new Commit("ddddddd", "M9 checkpoint: macOS results"),
                new Commit("eeeeeee", "M16d checkpoint: final CI run"),
            ],
            ["m9a.md", "m9b.md", "m10.md", "m16d.md", "m16.md.bak"],
            null));

        Assert.Equal(
            """
            ## Codalus IDE 0.2.0

            ### M9 and M10: packages, project management, terminal

            Checkpoint: [M10](https://github.com/owner/codalus/blob/v0.2.0/docs/checkpoints/m10.md), [M9A](https://github.com/owner/codalus/blob/v0.2.0/docs/checkpoints/m9a.md), [M9B](https://github.com/owner/codalus/blob/v0.2.0/docs/checkpoints/m9b.md)

            - M9 and M10: packages, project management, terminal (aaaaaaa)
            - M9 checkpoint: macOS results (ddddddd)

            ### M16d: web front-end depth, completing M16

            Checkpoint: [M16D](https://github.com/owner/codalus/blob/v0.2.0/docs/checkpoints/m16d.md)

            - M16d: web front-end depth, completing M16 (ccccccc)
            - M16d checkpoint: final CI run (eeeeeee)

            ### Other changes

            - Fix a flaky test (bbbbbbb)

            **All changes:** https://github.com/owner/codalus/compare/v0.1.0...v0.2.0

            """.ReplaceLineEndings("\n"),
            notes);
    }

    [Fact]
    public void A_hand_written_changelog_section_comes_first_and_a_nightly_uses_its_release_s_section()
    {
        const string changelog = """
            # Changelog

            ## [0.3.0]

            Not this one.

            ## 0.2.0 - 2026-10-20

            The first public preview.

            - Installers for every platform.

            ## 0.1.0

            Older.
            """;

        var notes = ReleaseNotes.Build(new ReleaseNotesInput(
            "0.2.0-nightly.20261011.3", Repository, "v0.2.0-nightly.20261011.3", null, [new Commit("abc1234", "M31: release cycle")], [], changelog));

        Assert.StartsWith(
            "## Codalus IDE 0.2.0-nightly.20261011.3\n\nThe first public preview.\n\n- Installers for every platform.\n\n### M31: release cycle\n",
            notes,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Not this one", notes, StringComparison.Ordinal);
        Assert.DoesNotContain("All changes", notes, StringComparison.Ordinal); // the first release has nothing to compare with
    }

    [Theory]
    [InlineData("M16d: web", "M16d")]
    [InlineData("M16d checkpoint: CI", "M16d")]
    [InlineData("M9 and M10: packages", "M9")]
    [InlineData("Milestones: none", null)]
    [InlineData("Fix M16d's test", null)]
    public void The_milestone_is_the_one_a_subject_starts_with(string subject, string? milestone)
    {
        Assert.Equal(milestone, ReleaseNotes.MilestoneOf(subject));
    }

    [Fact]
    public void A_release_with_no_commits_says_so()
    {
        var notes = ReleaseNotes.Build(new ReleaseNotesInput("0.2.0", Repository, "v0.2.0", "v0.1.9", [], [], null));

        Assert.Contains("No changes since the previous release.", notes, StringComparison.Ordinal);
    }
}
