namespace Codalus.Release.Tests;

/// <summary>The notes' commit range and the tag check against a throwaway repository.</summary>
public sealed class ReleaseRepositoryTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("codalus-release-").FullName;
    private readonly Git _git;

    public ReleaseRepositoryTests()
    {
        _git = new Git(_folder);
        _git.Run("init", "-q", "-b", "develop");
        _git.Run("config", "user.email", "test@example.com");
        _git.Run("config", "user.name", "Test");
        _git.Run("config", "commit.gpgsign", "false");
        _git.Run("config", "tag.gpgsign", "false");
    }

    public void Dispose()
    {
        // git writes read-only object files; clear the attribute so the folder can be deleted.
        foreach (var file in Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Stable_notes_start_after_the_previous_stable_tag_and_nightlies_after_the_previous_nightly()
    {
        Commit("M30a: .NET 11");
        _git.Run("tag", "v0.1.0");
        Commit("M31: release cycle");
        _git.Run("tag", "v0.2.0-nightly.20261010.1");
        Commit("M32: self-update");
        Directory.CreateDirectory(Path.Combine(_folder, "docs", "checkpoints"));
        File.WriteAllText(Path.Combine(_folder, "docs", "checkpoints", "m32.md"), "# M32");
        Commit("M32 checkpoint: done");

        var stable = ReleaseNotesSource.Read(_git, ReleaseKind.Stable, "0.2.0", "o/r", "HEAD", since: null, changelogFile: null);
        var nightly = ReleaseNotesSource.Read(_git, ReleaseKind.Nightly, "0.2.0-nightly.20261011.2", "o/r", "HEAD", since: null, changelogFile: null);

        Assert.Equal("v0.1.0", stable.PreviousTag);
        Assert.Equal(["M31: release cycle", "M32: self-update", "M32 checkpoint: done"], stable.Commits.Select(c => c.Subject));
        Assert.Equal("v0.2.0-nightly.20261010.1", nightly.PreviousTag);
        Assert.Equal(["M32: self-update", "M32 checkpoint: done"], nightly.Commits.Select(c => c.Subject));
        Assert.Contains("m32.md", stable.Checkpoints);
        Assert.Contains("[M32](https://github.com/o/r/blob/v0.2.0/docs/checkpoints/m32.md)", ReleaseNotes.Build(stable), StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_release_lists_every_commit_and_a_released_version_s_tag_is_taken()
    {
        Commit("M0: scaffold");
        Commit("M1: shell");

        var first = ReleaseNotesSource.Read(_git, ReleaseKind.Stable, "0.1.0", "o/r", "HEAD", since: null, changelogFile: null);
        Assert.Null(first.PreviousTag);
        Assert.Equal(2, first.Commits.Count);

        Assert.False(_git.TagExists("v0.1.0"));
        _git.Run("tag", "v0.1.0");
        Assert.True(_git.TagExists("v0.1.0"));

        // Re-running the notes of a release that has its tag already doesn't compare it with itself.
        Assert.Null(ReleaseNotesSource.PreviousTag(_git, ReleaseKind.Stable, "HEAD", excluding: "v0.1.0"));
    }

    [Fact]
    public void A_hotfix_copied_onto_develop_is_not_listed_again_in_the_next_release()
    {
        Commit("M1: shell");
        _git.Run("tag", "v0.1.0");
        _git.Run("branch", "master");
        Commit("M2: editor");

        // The hotfix is made on master and released, and the same change is copied onto develop with another hash.
        _git.Run("checkout", "-q", "master");
        Commit("Fix the crash on start", file: "fix.txt");
        _git.Run("tag", "v0.1.1");
        _git.Run("checkout", "-q", "develop");
        _git.Run("cherry-pick", "master");
        _git.Run("merge", "-q", "--no-edit", "master");
        Commit("M3: debugger");

        var notes = ReleaseNotesSource.Read(_git, ReleaseKind.Stable, "0.2.0", "o/r", "HEAD", since: null, changelogFile: null);

        Assert.Equal("v0.1.1", notes.PreviousTag);
        Assert.Equal(["M2: editor", "M3: debugger"], notes.Commits.Select(c => c.Subject));
    }

    private void Commit(string subject, string file = "log.txt")
    {
        File.AppendAllText(Path.Combine(_folder, file), subject + "\n");
        _git.Run("add", "-A");
        _git.Run("commit", "-q", "-m", subject);
    }
}
