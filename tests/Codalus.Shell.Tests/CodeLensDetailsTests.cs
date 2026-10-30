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

        // Turned off, it goes; the reference counts stay.
        await f.Shell.Workbench.Settings.SetAsync(EditorOptions.GitCodeLensKey, false, Workbench.Settings.SettingScope.User, TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => shapes.Insights.EndOfLineText(line) is { } eol && !eol.Contains("Ada Tester", StringComparison.Ordinal));
        Assert.Contains("references", shapes.Insights.EndOfLineText(line), StringComparison.Ordinal);
    }
}
