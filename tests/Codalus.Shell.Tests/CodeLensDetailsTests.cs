using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Codalus.Shell.Editor;
using Codalus.Workbench.Git;

namespace Codalus.Shell.Tests;

/// <summary>CodeLens's Git history and test status (deferred from M12a).</summary>
public sealed class CodeLensDetailsTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_members_history_is_its_last_author_and_its_numbers_of_authors_and_changes()
    {
        IReadOnlyList<BlameRange> blame =
        [
            new(1, 3, "aaaaaaa1", "Ada", s_now.AddDays(-30), "first"),
            new(4, 2, "bbbbbbb2", "Grace", s_now.AddDays(-3), "second"),
            new(6, 1, "aaaaaaa1", "Ada", s_now.AddDays(-30), "first"),
            new(7, 1, new string('0', 40), "Not Committed Yet", s_now, "local"),
        ];

        Assert.Equal("Grace, 3 days ago | 2 authors, 2 changes", LensDetails.Git(blame, 1, 7, s_now));
        Assert.Equal("Ada, 30 days ago | 1 author, 1 change", LensDetails.Git(blame, 1, 3, s_now));
        Assert.Null(LensDetails.Git(blame, 7, 7, s_now)); // only uncommitted lines
        Assert.Null(LensDetails.Git(blame, 20, 30, s_now));
    }

    [Fact]
    public void The_blame_index_words_a_members_history_as_a_pass_over_every_range_did()
    {
        var random = new Random(56);
        for (var round = 0; round < 50; round++)
        {
            var blame = new List<BlameRange>();
            var line = 1;
            while (line < 400)
            {
                var count = random.Next(1, 12);
                var commit = random.Next(8) == 0 ? new string('0', 40) : $"{random.Next(6):x}{new string('c', 39)}";
                blame.Add(new BlameRange(line, count, commit, $"Author{random.Next(4)}", s_now.AddDays(-random.Next(400)), "change"));
                line += count;
            }

            var index = new GitLensIndex(blame);
            for (var lens = 0; lens < 100; lens++)
            {
                var start = random.Next(1, 420);
                var end = start + random.Next(0, 60);
                Assert.Equal(LensDetails.Git(blame, start, end, s_now), index.Text(start, end, s_now));
            }
        }
    }

    [Fact]
    public void Each_lens_searches_the_blame_rather_than_passing_over_it()
    {
        // A 100,000-line file blamed in 20,000 ranges, with a lens on each of 10,000 members of 10 lines: a pass over every
        // range for each lens looked at 200,000,000 of them after each pause in typing (M56).
        var blame = Enumerable.Range(0, 20_000)
            .Select(i => new BlameRange(1 + (i * 5), 5, i % 7 == 0 ? new string('0', 40) : $"{i % 97:x2}{new string('a', 38)}", $"Author{i % 9}", s_now.AddDays(-(i % 300)), "change"))
            .ToList();
        var index = new GitLensIndex(blame);

        for (var member = 0; member < 10_000; member++)
        {
            Assert.NotNull(index.Text(1 + (member * 10), 10 + (member * 10), s_now));
        }

        Assert.True(index.Visits < 10_000 * 24, $"{index.Visits} ranges looked at for 10,000 lenses");
    }

    [Theory]
    [InlineData(0.5, "just now")]
    [InlineData(90, "1 hour ago")]
    [InlineData(5, "5 minutes ago")]
    [InlineData(1500, "yesterday")]
    [InlineData(60 * 24 * 10, "10 days ago")]
    [InlineData(60 * 24 * 100, "3 months ago")]
    [InlineData(60 * 24 * 800, "2 years ago")]
    public void Ages_read_naturally(double minutes, string expected) =>
        Assert.Equal(expected, LensDetails.Ago(TimeSpan.FromMinutes(minutes)));

    private static void Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
    }

    [AvaloniaFact]
    public async Task A_members_codelens_shows_its_git_history()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("LanguageFeatures");
        Git(folder, "init", "-q", "-b", "main");
        Git(folder, "config", "user.name", "Ada Tester");
        Git(folder, "config", "user.email", "ada@example.com");
        Git(folder, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(folder, ".gitignore"), "bin/\nobj/\n");
        Git(folder, "add", ".");
        Git(folder, "commit", "-q", "-m", "initial");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "LanguageFeatures.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: "ready", Projects: 2 }, seconds: 180);

        var document = await f.Shell.OpenFileAsync(Path.Combine(folder, "src", "Lib", "Shapes.cs"));
        await ShellFixture.PumpUntil(() => document?.Controller is not null);
        var shapes = document!.Controller!;
        var line = shapes.Model.Text.GetLineByOffset(shapes.Model.Text.Text.IndexOf("interface IShape", StringComparison.Ordinal)).LineNumber;
        await ShellFixture.PumpUntil(() => shapes.Insights.EndOfLineText(line) is { } eol && eol.Contains("Ada Tester", StringComparison.Ordinal), seconds: 90);
        Assert.Contains("Ada Tester, just now | 1 author, 1 change", shapes.Insights.EndOfLineText(line), StringComparison.Ordinal);

        // Turned off, it goes; the reference counts stay. (The row stays too, empty while the counts are on their way: the
        // history can come first.)
        await f.Shell.Workbench.Settings.SetAsync(EditorOptions.GitCodeLensKey, false, Workbench.Settings.SettingScope.User, TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(
            () => shapes.Insights.EndOfLineText(line) is { } eol && !eol.Contains("Ada Tester", StringComparison.Ordinal) && eol.Contains("references", StringComparison.Ordinal),
            seconds: 60,
            describe: () => $"'{shapes.Insights.EndOfLineText(line)}'");
    }
}
