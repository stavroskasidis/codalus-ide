using System.Diagnostics;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Codalus.Workbench.Ai.Ide;
using Codalus.Workbench.Ai.Mcp;
using Codalus.Workbench.Git;

namespace Codalus.Shell.Tests;

/// <summary>
/// M60's Git tools for agents in the shell, on a repository made in a temp folder: status, diffs, history and blame read
/// through Git Changes' service; staging, commits (the user's identity, no trailers), stashes, branches and conflicts
/// changed through it, so Git Changes follows; files excluded from AI left out and never changed; and nothing that
/// pushes, pulls, fetches, resets, rebases or amends.
/// </summary>
public sealed class IdeToolsGitShellTests
{
    private const string Secret = "src/Greetings/secret-notes.txt";

    private static string Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
        return output;
    }

    private static async Task<IdeToolResult> CallAsync(ShellFixture f, string tool, string json = "{}") =>
        await f.Shell.IdeToolAsync(tool, JsonElement.Parse(json), "Claude", CancellationToken.None);

    private static async Task<string> OkAsync(ShellFixture f, string tool, string json = "{}")
    {
        var result = await CallAsync(f, tool, json);
        Assert.False(result.IsError, $"{tool}: {result.Text}");
        return result.Text;
    }

    private static async Task<string> ErrorAsync(ShellFixture f, string tool, string json)
    {
        var result = await CallAsync(f, tool, json);
        Assert.True(result.IsError, $"{tool} should fail: {result.Text}");
        return result.Text;
    }

    /// <summary>
    /// BuildAndRun as a repository: one commit, then Greeter.cs changed, a new untracked file, and a tracked file that
    /// <c>.codalusignore</c> excludes from AI, changed too.
    /// </summary>
    private static async Task<(ShellFixture Fixture, string Folder)> OpenRepositoryAsync()
    {
        var f = ShellFixture.Create();
        var folder = f.CopySample("BuildAndRun");
        Git(folder, "init", "-q", "-b", "main");
        Git(folder, "config", "user.name", "Ada Tester");
        Git(folder, "config", "user.email", "ada@example.com");
        Git(folder, "config", "commit.gpgsign", "false");
        Git(folder, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(folder, ".gitignore"), "bin/\nobj/\n");
        File.WriteAllText(Path.Combine(folder, ".codalusignore"), Secret + "\n");
        File.WriteAllText(Path.Combine(folder, Secret), "the launch codes\n");
        Git(folder, "add", ".");
        Git(folder, "commit", "-q", "-m", "initial");

        var greeter = Path.Combine(folder, "src", "Greetings", "Greeter.cs");
        File.WriteAllText(greeter, File.ReadAllText(greeter).Replace("Hello from", "Howdy from", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "todo\n");
        File.WriteAllText(Path.Combine(folder, Secret), "the new launch codes\n");

        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "BuildAndRun.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Git.Status is { Branch: "main" } && Equals(f.Shell.Workbench.Context.GetValue("gitRepository"), true));
        return (f, folder);
    }

    [AvaloniaFact]
    public async Task Agents_read_the_status_diffs_history_and_blame_without_the_excluded_files()
    {
        var (f, folder) = await OpenRepositoryAsync();
        await using var fixture = f;

        // The status as Git Changes has it, the excluded file counted but not named.
        var status = await OkAsync(f, "git_status");
        Assert.Contains("On branch main, no upstream.", status, StringComparison.Ordinal);
        Assert.Contains("Not staged:\n  modified: src/Greetings/Greeter.cs\n", status.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("untracked: notes.txt", status, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 change excluded from AI (or outside the workspace) isn't shown.", status, StringComparison.Ordinal);

        // Diffs: every change (the untracked file's content too), one file, never the excluded one.
        var diff = await OkAsync(f, "git_diff");
        Assert.Contains("-    public static string Greet(string name) => $\"Hello from {name}\";", diff, StringComparison.Ordinal);
        Assert.Contains("+    public static string Greet(string name) => $\"Howdy from {name}\";", diff, StringComparison.Ordinal);
        Assert.Contains("+todo", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("launch codes", diff, StringComparison.Ordinal);
        Assert.Contains("excluded from AI", diff, StringComparison.Ordinal);
        Assert.Equal("No changes.", (await OkAsync(f, "git_diff", """{"staged":true}""")).TrimEnd());
        Assert.Contains("Howdy from", await OkAsync(f, "git_diff", """{"path":"src/Greetings/Greeter.cs"}"""), StringComparison.Ordinal);
        Assert.Equal($"'{Secret}' is excluded from AI.", await ErrorAsync(f, "git_diff", $$"""{"path":"{{Secret}}"}"""));
        Assert.Contains("outside the workspace", await ErrorAsync(f, "git_diff", """{"path":"../../etc/passwd"}"""), StringComparison.Ordinal);

        // A commit's diff with its message; the excluded file it added isn't in it.
        var commit = await OkAsync(f, "git_diff", """{"commit":"HEAD"}""");
        Assert.StartsWith("commit " + Git(folder, "rev-parse", "HEAD").Trim(), commit, StringComparison.Ordinal);
        Assert.Contains("Author: Ada Tester <ada@example.com>", commit, StringComparison.Ordinal);
        Assert.Contains("    initial", commit, StringComparison.Ordinal);
        Assert.Contains("+    public static string Greet(string name) => $\"Hello from {name}\";", commit, StringComparison.Ordinal);
        Assert.DoesNotContain("launch codes", commit, StringComparison.Ordinal);
        Assert.Contains("1 file's diff excluded from AI", commit, StringComparison.Ordinal);
        Assert.Contains("isn't a revision", await ErrorAsync(f, "git_diff", """{"commit":"--output=/tmp/x"}"""), StringComparison.Ordinal);

        // History, a file's history and blame.
        var log = await OkAsync(f, "git_log");
        Assert.Matches(@"^[0-9a-f]{8} \d{4}-\d\d-\d\d Ada Tester: initial \(HEAD -> main\)", log);
        Assert.Contains("Ada Tester: initial", await OkAsync(f, "git_file_history", """{"path":"src/Greetings/Greeter.cs"}"""), StringComparison.Ordinal);
        Assert.Contains("Ada Tester: initial", await OkAsync(f, "git_file_history", """{"path":"src/Greetings/Greeter.cs","startLine":5,"endLine":5}"""), StringComparison.Ordinal);
        Assert.Contains("is excluded from AI", await ErrorAsync(f, "git_log", $$"""{"path":"{{Secret}}"}"""), StringComparison.Ordinal);

        var blame = (await OkAsync(f, "git_blame", """{"path":"src/Greetings/Greeter.cs","startLine":1,"endLine":5}""")).ReplaceLineEndings("\n");
        Assert.Contains("Ada Tester: initial\n  1: namespace Greetings;\n", blame, StringComparison.Ordinal);
        Assert.Contains("Not committed yet:\n  5:     public static string Greet(string name) => $\"Howdy from {name}\";\n", blame, StringComparison.Ordinal);
        Assert.Contains("is excluded from AI", await ErrorAsync(f, "git_blame", $$"""{"path":"{{Secret}}"}"""), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Agents_stage_commit_stash_and_check_out_branches_through_git_changes()
    {
        var (f, folder) = await OpenRepositoryAsync();
        await using var fixture = f;
        var greeter = Path.Combine(folder, "src", "Greetings", "Greeter.cs");

        // Staging shows in Git Changes; the excluded file can't be staged, and "all" leaves it out.
        Assert.StartsWith("Staged 1 file(s): src/Greetings/Greeter.cs", await OkAsync(f, "git_stage", """{"paths":["src/Greetings/Greeter.cs"]}"""), StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => f.Shell.GitChanges.Staged.Any(r => r.Status.RelativePath == "src/Greetings/Greeter.cs"));
        Assert.Equal($"'{Secret}' is excluded from AI: the tools can't change it.", await ErrorAsync(f, "git_stage", $$"""{"paths":["{{Secret}}"]}"""));
        var all = await OkAsync(f, "git_stage", """{"all":true}""");
        Assert.StartsWith("Staged 1 file(s): notes.txt", all, StringComparison.Ordinal);
        Assert.Contains("Those stay unstaged", all, StringComparison.Ordinal);
        Assert.Contains(f.Shell.Git.Status!.Unstaged, s => s.RelativePath == Secret);
        Assert.StartsWith("Unstaged 1 file(s): notes.txt", await OkAsync(f, "git_unstage", """{"paths":["notes.txt"]}"""), StringComparison.Ordinal);
        Assert.Equal("Give paths, or all: true.", await ErrorAsync(f, "git_stage", "{}"));

        // A commit of the staged change, with the user's identity and the message as given: no trailers.
        var committed = await OkAsync(f, "git_commit", """{"message":"Say howdy\n\nGreet more warmly."}""");
        Assert.Matches(@"^Committed [0-9a-f]{8} on main: Say howdy \(1 file\(s\)\)\. It is not pushed", committed);
        Assert.Equal("Say howdy\n\nGreet more warmly.\n", Git(folder, "log", "-1", "--format=%B").ReplaceLineEndings("\n").TrimEnd('\n') + "\n");
        Assert.Equal("Ada Tester <ada@example.com>", Git(folder, "log", "-1", "--format=%an <%ae>").Trim());
        await ShellFixture.PumpUntil(() => f.Shell.GitChanges.Staged.Count == 0);
        Assert.Equal("Nothing is staged: stage files with git_stage, or pass all: true.", await ErrorAsync(f, "git_commit", """{"message":"empty"}"""));

        // A staged excluded file stops a commit; "all" commits the rest and leaves it.
        Git(folder, "add", Secret);
        Assert.Contains("excluded from AI or outside the workspace, and the tools don't commit them", await ErrorAsync(f, "git_commit", """{"message":"leak"}"""), StringComparison.Ordinal);
        Git(folder, "restore", "--staged", Secret);
        var rest = await OkAsync(f, "git_commit", """{"message":"Add notes","all":true}""");
        Assert.Contains("1 change(s) excluded from AI or outside the workspace stay uncommitted.", rest, StringComparison.Ordinal);
        Assert.Equal("notes.txt", Git(folder, "show", "--name-only", "--format=", "HEAD").Trim());
        Assert.Equal($" M {Secret}", Git(folder, "status", "--porcelain").TrimEnd());

        // Stash: only what the tools may touch goes (a partial stash), and pops back.
        File.WriteAllText(greeter, File.ReadAllText(greeter).Replace("Howdy from", "Hi from", StringComparison.Ordinal));
        var stashed = await OkAsync(f, "git_stash", """{"action":"push","message":"wip"}""");
        Assert.StartsWith("Stashed 1 file(s) as stash@{0}: On main: wip.", stashed, StringComparison.Ordinal);
        Assert.Contains("Those stay in the working tree.", stashed, StringComparison.Ordinal);
        Assert.DoesNotContain("Hi from", File.ReadAllText(greeter), StringComparison.Ordinal);
        Assert.Contains("new launch codes", File.ReadAllText(Path.Combine(folder, Secret)), StringComparison.Ordinal);
        Assert.Equal("stash@{0}: On main: wip", (await OkAsync(f, "git_stash", """{"action":"list"}""")).TrimEnd());
        Assert.Equal("Popped stash@{0} (On main: wip) into the working tree.", await OkAsync(f, "git_stash", """{"action":"pop"}"""));
        Assert.Contains("Hi from", File.ReadAllText(greeter), StringComparison.Ordinal);
        Assert.Equal("No stashes.", await OkAsync(f, "git_stash"));

        // A stash that holds the excluded file isn't popped by the tools.
        Git(folder, "stash", "push", "-q", "-m", "secret", "--", Secret);
        Assert.Contains("changes files excluded from AI", await ErrorAsync(f, "git_stash", """{"action":"pop"}"""), StringComparison.Ordinal);
        Git(folder, "stash", "pop", "-q");

        // Branches: a new one, back to main, and one that doesn't exist.
        Assert.Equal("Created the branch agent/work from main and checked it out.", await OkAsync(f, "git_checkout", """{"branch":"agent/work","create":true}"""));
        Assert.Equal("agent/work", f.Shell.Git.Status!.Branch);
        Assert.Equal("Checked out main.", await OkAsync(f, "git_checkout", """{"branch":"main"}"""));
        Assert.Equal("main is already checked out.", await OkAsync(f, "git_checkout", """{"branch":"main"}"""));
        Assert.Contains("Local branches: agent/work, main", await ErrorAsync(f, "git_checkout", """{"branch":"nope"}"""), StringComparison.Ordinal);
        Assert.Contains("exists", await ErrorAsync(f, "git_checkout", """{"branch":"main","create":true}"""), StringComparison.Ordinal);
        Assert.Contains("isn't a branch name", await ErrorAsync(f, "git_checkout", """{"branch":"-D"}"""), StringComparison.Ordinal);
        Assert.Contains("agent/work", Git(folder, "branch", "--list"), StringComparison.Ordinal); // never deleted
    }

    [AvaloniaFact]
    public async Task Agents_resolve_conflicts_and_complete_a_merge_but_nothing_pushes_or_rewrites_history()
    {
        var (f, folder) = await OpenRepositoryAsync();
        await using var fixture = f;
        var greeter = Path.Combine(folder, "src", "Greetings", "Greeter.cs");
        Git(folder, "checkout", "-q", "--", "src/Greetings/Greeter.cs", Secret);
        File.Delete(Path.Combine(folder, "notes.txt"));
        File.WriteAllText(Path.Combine(folder, "a.txt"), "base\n");
        File.WriteAllText(Path.Combine(folder, "b.txt"), "base\n");
        Git(folder, "add", "a.txt", "b.txt");
        Git(folder, "commit", "-q", "-m", "two files");

        // feature and main change the same lines of three files.
        var original = File.ReadAllText(greeter);
        Git(folder, "switch", "-q", "-c", "feature");
        File.WriteAllText(greeter, original.Replace("Hello from", "Howdy from", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(folder, "a.txt"), "theirs a\n");
        File.WriteAllText(Path.Combine(folder, "b.txt"), "theirs b\n");
        Git(folder, "commit", "-q", "-am", "Feature side");
        Git(folder, "switch", "-q", "main");
        File.WriteAllText(greeter, original.Replace("Hello from", "Greetings from", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(folder, "a.txt"), "ours a\n");
        File.WriteAllText(Path.Combine(folder, "b.txt"), "ours b\n");
        Git(folder, "commit", "-q", "-am", "Main side");
        await f.Shell.Git.RefreshAsync();
        await Assert.ThrowsAsync<GitException>(() => f.Shell.Git.MergeAsync("feature"));

        // The merge and its conflicts, in the status and in git_conflicts; a file's three versions.
        Assert.Contains("A merge is in progress", await OkAsync(f, "git_status"), StringComparison.Ordinal);
        var conflicts = (await OkAsync(f, "git_conflicts")).ReplaceLineEndings("\n");
        Assert.Contains("  both modified: a.txt\n  both modified: b.txt\n  both modified: src/Greetings/Greeter.cs\n", conflicts, StringComparison.Ordinal);
        var versions = await OkAsync(f, "git_conflicts", """{"path":"src/Greetings/Greeter.cs"}""");
        Assert.Contains("===== ours (the current branch) =====", versions, StringComparison.Ordinal);
        Assert.Contains("Greetings from", versions, StringComparison.Ordinal);
        Assert.Contains("===== theirs (what comes in) =====", versions, StringComparison.Ordinal);
        Assert.Contains("Howdy from", versions, StringComparison.Ordinal);
        Assert.Contains("is in conflict: resolve it with git_resolve", await ErrorAsync(f, "git_stage", """{"paths":["a.txt"]}"""), StringComparison.Ordinal);
        Assert.Contains("Files are in conflict", await ErrorAsync(f, "git_commit", """{"message":"too early"}"""), StringComparison.Ordinal);

        // Theirs, both, and content the agent wrote (never with markers left in).
        Assert.Equal("Resolved src/Greetings/Greeter.cs (theirs) and staged it. 2 file(s) are still in conflict.", await OkAsync(f, "git_resolve", """{"path":"src/Greetings/Greeter.cs","take":"theirs"}"""));
        Assert.Contains("Howdy from", File.ReadAllText(greeter), StringComparison.Ordinal);
        await OkAsync(f, "git_resolve", """{"path":"a.txt","take":"both"}""");
        Assert.Equal("ours a\ntheirs a\n", File.ReadAllText(Path.Combine(folder, "a.txt")));
        Assert.Contains("still has conflict markers", await ErrorAsync(f, "git_resolve", """{"path":"b.txt","take":"content","content":"<<<<<<< HEAD\nx\n=======\ny\n>>>>>>> feature\n"}"""), StringComparison.Ordinal);
        Assert.EndsWith("No conflicts are left.", await OkAsync(f, "git_resolve", """{"path":"b.txt","take":"content","content":"merged b\n"}"""), StringComparison.Ordinal);
        Assert.Equal("merged b\n", File.ReadAllText(Path.Combine(folder, "b.txt")));
        Assert.Contains("isn't in conflict", await ErrorAsync(f, "git_resolve", """{"path":"b.txt","take":"ours"}"""), StringComparison.Ordinal);
        Assert.Contains("git_commit completes the merge", await OkAsync(f, "git_conflicts"), StringComparison.Ordinal);

        // git_commit completes the merge: a commit with both parents.
        Assert.StartsWith("Committed", await OkAsync(f, "git_commit", """{"message":"Merge feature"}"""), StringComparison.Ordinal);
        Assert.Equal(3, Git(folder, "rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' ').Length);

        // Nothing pushes, pulls, fetches, resets, rebases, amends, forces or deletes a branch.
        string[] forbidden = ["push", "pull", "fetch", "reset", "rebase", "amend", "force", "delete", "remote"];
        foreach (var tool in IdeGitTools.Tools)
        {
            Assert.DoesNotContain(forbidden, word => tool.Name.Contains(word, StringComparison.Ordinal));
            using var schema = JsonDocument.Parse(tool.Schema);
            Assert.DoesNotContain(schema.RootElement.GetProperty("properties").EnumerateObject(), p => forbidden.Any(word => p.Name.Contains(word, StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var tool in new[] { "git_push", "git_pull", "git_fetch", "git_reset", "git_rebase", "git_branch_delete" })
        {
            await Assert.ThrowsAsync<McpException>(() => CallAsync(f, tool));
        }

        Assert.Equal(["git_status", "git_diff", "git_log", "git_file_history", "git_blame", "git_conflicts"], IdeGitTools.Tools.Where(t => t.Kind == IdeToolKind.Read).Select(t => t.Name));
        Assert.Equal(["git_stage", "git_unstage", "git_commit", "git_stash", "git_checkout", "git_resolve"], IdeGitTools.Tools.Where(t => t.Kind == IdeToolKind.Action).Select(t => t.Name));
    }

    [Fact]
    public void Taking_both_sides_keeps_ours_then_theirs_and_drops_the_base()
    {
        Assert.Equal("a\nours\ntheirs\nb\n", ShellHost.GitTakeBothSides("a\n<<<<<<< HEAD\nours\n||||||| base\nold\n=======\ntheirs\n>>>>>>> feature\nb\n"));
        Assert.Equal("ours\r\ntheirs\r\n", ShellHost.GitTakeBothSides("<<<<<<< HEAD\r\nours\r\n=======\r\ntheirs\r\n>>>>>>> feature\r\n"));
        Assert.Null(ShellHost.GitTakeBothSides("no conflict\n"));
        Assert.Null(ShellHost.GitTakeBothSides("<<<<<<< HEAD\nours\n=======\n"));
    }
}
