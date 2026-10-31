using Avalonia.Headless.XUnit;
using Codalus.Shell.Editor;
using Codalus.Workbench.Git;

namespace Codalus.Shell.Tests;

/// <summary>
/// The editors' Git change markers after status refreshes (M56): a hidden editor works them out once it's shown, and the
/// file's index version is read again only when the status says it's another.
/// </summary>
public sealed class GitMarkersTests
{
    [Fact]
    public void The_status_names_each_listed_files_index_version()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo");
        var head = new string('1', 40);
        var staged = new string('2', 40);
        var index = new string('3', 40);
        var status = GitParsers.ParseStatus(root, string.Join('\0',
            $"# branch.oid {head}",
            "# branch.head main",
            $"1 .M N... 100644 100644 100644 {head} {index} Modified.cs",
            $"1 M. N... 100644 100644 100644 {head} {staged} Staged.cs",
            $"2 R. N... 100644 100644 100644 {head} {staged} R100 New.cs",
            "Old.cs",
            "? Untracked.cs",
            string.Empty), isMerging: false);

        Assert.Equal([index, staged, staged, null], status.Files.Select(f => f.IndexObject));
        string? Key(string name) => CodeEditorController.IndexKey(Path.Combine(root, name), status, status.Files.FirstOrDefault(f => f.RelativePath == name));
        Assert.Equal(Path.Combine(root, "Modified.cs") + "\n" + index, Key("Modified.cs"));
        Assert.Equal(Path.Combine(root, "Clean.cs") + "\nHEAD " + head, Key("Clean.cs")); // not listed: HEAD's version
        Assert.EndsWith("\nuntracked", Key("Untracked.cs"), StringComparison.Ordinal);
        Assert.Null(CodeEditorController.IndexKey(Path.Combine(root, "Clean.cs"), null, null)); // no status: it can't tell
    }

    [AvaloniaFact]
    public async Task Hidden_editors_wait_to_be_shown_and_an_unchanged_index_version_is_not_read_again()
    {
        var (f, folder, file) = await DiffDocumentTests.RepositoryAsync();
        await using var fixture = f;
        var other = Path.Combine(folder, "Other.cs");
        await File.WriteAllTextAsync(other, "public static class Other\n{\n}\n", TestContext.Current.CancellationToken);
        DiffDocumentTests.Git(folder, "add", ".");
        DiffDocumentTests.Git(folder, "commit", "-q", "-m", "other");
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);

        var behind = await f.Shell.OpenFileAsync(file);
        await ShellFixture.PumpUntil(() => behind?.Controller is not null);
        var hidden = behind!.Controller!;
        await ShellFixture.PumpUntil(() => hidden.GitMarkerWork.Updates > 0);
        var front = await f.Shell.OpenFileAsync(other);
        await ShellFixture.PumpUntil(() => front?.Controller is not null);
        var shown = front!.Controller!;
        await ShellFixture.PumpUntil(() => shown.GitMarkerWork.Updates > 0);
        await SettleAsync(f);

        // Status refreshes (a build's churn, a command's refresh) with the index as it was: the hidden editor does nothing,
        // the shown one works its markers out without reading the index version again.
        var hiddenWork = hidden.GitMarkerWork;
        var shownWork = shown.GitMarkerWork;
        for (var i = 0; i < 5; i++)
        {
            await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
            await WaitAsync(400);
        }

        await SettleAsync(f);
        Assert.Equal(hiddenWork, hidden.GitMarkerWork);
        Assert.True(shown.GitMarkerWork.Updates > shownWork.Updates, $"{shown.GitMarkerWork} after {shownWork}");
        Assert.Equal(shownWork.IndexReads, shown.GitMarkerWork.IndexReads);

        // Another version staged (the working tree left as it is): read once.
        var staged = Path.Combine(f.UserData, "staged.txt");
        await File.WriteAllTextAsync(staged, "public static class Other\n{\n    // staged\n}\n", TestContext.Current.CancellationToken);
        var blob = DiffDocumentTests.Git(folder, "hash-object", "-w", staged).Trim();
        DiffDocumentTests.Git(folder, "update-index", "--cacheinfo", $"100644,{blob},Other.cs");
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => shown.GitMarkerWork.IndexReads == shownWork.IndexReads + 1, describe: () => $"{shown.GitMarkerWork}");
        await SettleAsync(f);
        Assert.Equal(shownWork.IndexReads + 1, shown.GitMarkerWork.IndexReads);
        Assert.NotEmpty(shown.GitMargin.Changes); // the staged line isn't in the editor's text

        // Shown again, the editor behind catches up once.
        Assert.Equal(hiddenWork, hidden.GitMarkerWork);
        f.Shell.Layout.OpenDocument(behind);
        await ShellFixture.PumpUntil(() => hidden.GitMarkerWork.Updates == hiddenWork.Updates + 1, describe: () => $"{hidden.GitMarkerWork}");
        await SettleAsync(f);
        Assert.Equal(hiddenWork.Updates + 1, hidden.GitMarkerWork.Updates);
    }

    /// <summary>The watcher's status reads of what the test did, and the markers' timers after them, done.</summary>
    private static async Task SettleAsync(ShellFixture f)
    {
        await WaitAsync(500);
        await ShellFixture.PumpUntil(() => !f.Shell.Git.IsReadPending, seconds: 60);
        await WaitAsync(400);
    }

    private static async Task WaitAsync(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        await ShellFixture.PumpUntil(() => DateTime.UtcNow >= until, seconds: 60);
    }
}
