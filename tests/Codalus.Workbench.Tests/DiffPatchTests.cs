using System.Diagnostics;
using System.Text;
using Codalus.Workbench.Git;

namespace Codalus.Workbench.Tests;

/// <summary>
/// Patches built from the diff document's rows (#86, D45 question 7), applied by the real git to a temporary
/// repository: a change stages, unstages and discards on its own even when git puts it in one hunk with another.
/// </summary>
public sealed class DiffPatchTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-patch-" + Guid.NewGuid().ToString("N"));
    private readonly GitService _git = new(watch: false);

    private string File1 => Path.Combine(_root, "a.txt");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] s_lines = ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten"];

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Ada Tester");
        Git("config", "user.email", "ada@example.com");
        Git("config", "commit.gpgsign", "false");
        Git("config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(File1, string.Join('\n', s_lines) + "\n", Ct);
        Git("add", ".");
        Git("commit", "-q", "-m", "initial");
        Assert.True(await _git.OpenAsync(_root, Ct));
    }

    public ValueTask DisposeAsync()
    {
        _git.Dispose();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
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

    private static IReadOnlyList<DiffRow> Rows(byte[] old, byte[] now) =>
        DiffRows.Align(LineDiff.Diff(LineDiff.Lines(Encoding.UTF8.GetString(old)), LineDiff.Lines(Encoding.UTF8.GetString(now))));

    /// <summary>Stages (or unstages, or discards) the rows chosen by <paramref name="choose"/> of the diff between two versions.</summary>
    private async Task ApplyAsync(byte[] old, byte[] now, Func<IReadOnlyList<DiffRow>, IEnumerable<int>> choose, bool cached, bool reverse, bool oldExists = true, string path = "a.txt")
    {
        var rows = Rows(old, now);
        var patch = DiffPatch.Build(path, path, oldExists, old, now, rows, choose(rows).ToHashSet(), reverse);
        Assert.NotNull(patch);
        await _git.ApplyPatchAsync(patch, cached, reverse, Ct);
    }

    private async Task<byte[]> IndexAsync(string path = "a.txt") => await _git.ShowBytesAsync(Path.Combine(_root, path), ":", Ct) ?? [];

    [Fact]
    public async Task Two_changes_in_one_git_hunk_stage_on_their_own()
    {
        // "two" and "five" changed: three lines apart, one hunk at git's three lines of context.
        var edited = "one\nTWO\nthree\nfour\nFIVE\nsix\nseven\neight\nnine\nten\n";
        await File.WriteAllTextAsync(File1, edited, Ct);
        Assert.Single(Git("diff").Split('\n'), l => l.StartsWith("@@", StringComparison.Ordinal));
        var index = await IndexAsync();
        var rows = Rows(index, Encoding.UTF8.GetBytes(edited));
        var changes = DiffRows.Changes(rows);
        Assert.Equal(2, changes.Count);

        // Stage Change on the second: only FIVE goes to the index.
        await ApplyAsync(index, Encoding.UTF8.GetBytes(edited), _ => [changes[1].FirstRow], cached: true, reverse: false);
        Assert.Equal("one\ntwo\nthree\nfour\nFIVE\nsix\nseven\neight\nnine\nten\n", Encoding.UTF8.GetString(await IndexAsync()));

        // Unstage it again (the staged diff: HEAD against the index, applied in reverse to the index).
        var head = await _git.ShowBytesAsync(File1, "HEAD", Ct) ?? [];
        index = await IndexAsync();
        await ApplyAsync(head, index, rows => DiffRows.Changes(rows).Select(c => c.FirstRow), cached: true, reverse: true);
        Assert.Equal(string.Join('\n', s_lines) + "\n", Encoding.UTF8.GetString(await IndexAsync()));

        // Discard the first in the working tree: TWO goes back to two, FIVE stays.
        await ApplyAsync(index, Encoding.UTF8.GetBytes(edited), rows => [DiffRows.Changes(rows)[0].FirstRow], cached: false, reverse: true);
        Assert.Equal("one\ntwo\nthree\nfour\nFIVE\nsix\nseven\neight\nnine\nten\n", await File.ReadAllTextAsync(File1, Ct));
    }

    [Fact]
    public async Task Selected_lines_of_a_change_stage_and_discard_alone()
    {
        // Two lines added and one changed in one run.
        var edited = "one\ntwo\nadded 1\nadded 2\nTHREE\nfour\nfive\nsix\nseven\neight\nnine\nten\n";
        await File.WriteAllTextAsync(File1, edited, Ct);
        var index = await IndexAsync();
        var rows = Rows(index, Encoding.UTF8.GetBytes(edited));
        var added2 = rows.Select((r, i) => (r, i)).Single(x => x.r.NewLine == 4).i;

        await ApplyAsync(index, Encoding.UTF8.GetBytes(edited), _ => [added2], cached: true, reverse: false);
        var staged = Encoding.UTF8.GetString(await IndexAsync());
        Assert.Contains("added 2", staged, StringComparison.Ordinal);
        Assert.DoesNotContain("added 1", staged, StringComparison.Ordinal);
        Assert.Contains("\nthree\n", staged, StringComparison.Ordinal); // the changed line, not chosen, stays as it was

        // Discard Selected Lines on "added 1" in the working tree.
        index = await IndexAsync();
        rows = Rows(index, Encoding.UTF8.GetBytes(edited));
        var added1 = rows.Select((r, i) => (r, i)).Single(x => x.r.NewLine == 3).i;
        await ApplyAsync(index, Encoding.UTF8.GetBytes(edited), _ => [added1], cached: false, reverse: true);
        var file = await File.ReadAllTextAsync(File1, Ct);
        Assert.DoesNotContain("added 1", file, StringComparison.Ordinal);
        Assert.Contains("added 2\nTHREE\n", file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Line_endings_a_byte_order_mark_and_a_last_line_without_a_newline_pass_unchanged()
    {
        // A CRLF file with a byte order mark and no newline at its end.
        byte[] bom = [0xEF, 0xBB, 0xBF];
        var original = (byte[])[.. bom, .. Encoding.UTF8.GetBytes("alpha\r\nbeta\r\ngamma")];
        await File.WriteAllBytesAsync(File1, original, Ct);
        Git("add", ".");
        Git("commit", "-q", "-m", "crlf");

        // Lines added after the last one, and the first changed: stage only the added lines at the end.
        var edited = (byte[])[.. bom, .. Encoding.UTF8.GetBytes("ALPHA\r\nbeta\r\ngamma\r\ndelta\r\n")];
        await File.WriteAllBytesAsync(File1, edited, Ct);
        var index = await IndexAsync();
        var rows = Rows(index, edited);
        await ApplyAsync(index, edited, rows => rows.Select((r, i) => (r, i)).Where(x => x.r.NewLine is 4).Select(x => x.i), cached: true, reverse: false);
        Assert.Equal((byte[])[.. bom, .. Encoding.UTF8.GetBytes("alpha\r\nbeta\r\ngamma\r\ndelta\r\n")], await IndexAsync());

        // Then the first line: the byte order mark stays on it.
        index = await IndexAsync();
        await ApplyAsync(index, edited, rows => [DiffRows.Changes(rows)[0].FirstRow], cached: true, reverse: false);
        Assert.Equal(edited, await IndexAsync());
    }

    [Fact]
    public async Task A_new_file_stages_line_by_line()
    {
        var path = Path.Combine(_root, "b.txt");
        var text = Encoding.UTF8.GetBytes("first\nsecond\n");
        await File.WriteAllBytesAsync(path, text, Ct);
        await ApplyAsync([], text, rows => [rows.Select((r, i) => (r, i)).Single(x => x.r.NewLine == 2).i], cached: true, reverse: false, oldExists: false, path: "b.txt");
        Assert.Equal("second\n", Encoding.UTF8.GetString(await IndexAsync("b.txt")));
    }
}
