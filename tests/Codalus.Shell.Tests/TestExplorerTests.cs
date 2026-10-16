using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Testing;

namespace Codalus.Shell.Tests;

/// <summary>The Test Explorer, the test margin and test runs in the shell (ADR 0010), on samples/Testing.</summary>
public sealed class TestExplorerTests
{
    /// <summary>Writes every Output channel to the test output (on failure).</summary>
    private static bool Dump(ShellFixture f)
    {
        foreach (var channel in f.Shell.Workbench.Output.Channels)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"===== {channel.Name}");
            TestContext.Current.TestOutputHelper?.WriteLine(channel.GetText());
        }

        foreach (var run in f.Shell.Tests.Runs)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"run {run.Name} {run.RunId}: {run.Results.Count} results");
        }

        var states = f.Shell.Tests.Read(roots => string.Join(", ", roots.Single(r => r.Label == "XunitV2Tests").Leaves().Select(l => $"{l.Id}={l.Outcome}")));
        TestContext.Current.TestOutputHelper?.WriteLine("XunitV2Tests: " + states);
        return false;
    }

    [AvaloniaFact]
    public async Task Tests_appear_from_source_run_and_show_in_the_editor()
    {
        await using var f = ShellFixture.Create(builtInPlugins: ShellFixture.PluginBin("Codalus.Plugins.DotNetTest"));
        f.Window.Width = 1400;
        f.Window.Height = 900;
        var folder = f.CopySample("Testing");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Testing.slnx"));
        f.Shell.Layout.ShowToolWindow(TestExplorerViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<TestExplorerView>().Any());

        // Source discovery fills the tree before anything is built: every test project, expanded to classes.
        var explorer = f.Shell.TestExplorer;
        await ShellFixture.PumpUntil(() => explorer.Rows.Count(r => r.Depth == 0) == 7, seconds: 180);
        Assert.Contains(explorer.Rows, r => r.Label == "XunitV2Tests" && r.Depth == 0);
        Assert.Contains(explorer.Rows, r => r.Label == "CalculatorTests" && r.Depth == 2);
        Assert.Contains("0 passed", explorer.Summary, StringComparison.Ordinal);

        // Filtering keeps the matching tests and their ancestors.
        explorer.FilterText = "project:XunitV2Tests Fails";
        explorer.ApplyFilter();
        ShellFixture.Pump();
        Assert.Equal(["XunitV2Tests", "XunitV2Tests", "CalculatorTests", "Fails"], explorer.Rows.Select(r => r.Label));
        explorer.FilterText = string.Empty;
        explorer.ApplyFilter();

        // Running a class: outcomes in the tree, the failure's details with clickable frames.
        var cls = f.Shell.Tests.Read(roots => roots.Single(r => r.Label == "XunitV2Tests").SelfAndDescendants().Single(n => n.Label == "CalculatorTests"));
        await f.Shell.RunTestsAsync(TestRunKind.Run, [cls.Key]);
        try
        {
            await ShellFixture.PumpUntil(() => !f.Shell.Tests.IsRunning && cls.Children.Any(c => c.Label == "Fails" && c.Outcome == TestOutcome.Failed), seconds: 240);
        }
        catch (Exception) when (Dump(f))
        {
            throw;
        }

        await ShellFixture.PumpUntil(() => explorer.Rows.Single(r => r.Group.Node?.Key == cls.Key).Style.IsFailed);
        explorer.Reveal(cls.Children.Single(c => c.Label == "Fails").Key);
        ShellFixture.Pump();
        Assert.Equal("Fails", explorer.Selected?.Label);
        Assert.True(explorer.Selected!.Style.IsFailed);
        Assert.Contains("Assert.Equal() Failure", explorer.DetailsMessage, StringComparison.Ordinal);
        Assert.Contains(explorer.DetailsFrames, frame => frame.HasLocation && frame.File!.EndsWith("CalculatorTests.cs", StringComparison.Ordinal));
        Assert.Contains("passed", f.Shell.StatusBar.Left.Single(i => i.Item.Id == "codalus.tests").Text, StringComparison.Ordinal);
        explorer.Grouping = TestGrouping.Outcome;
        Assert.Equal("Failed", explorer.Rows[0].Label);
        explorer.Grouping = TestGrouping.Hierarchy;

        // The editor shows glyphs on the tests' lines, with the outcomes.
        var file = Path.Combine(folder, "tests", "XunitV2Tests", "CalculatorTests.cs");
        var editor = await f.Shell.OpenFileAsync(file);
        await ShellFixture.PumpUntil(() => editor?.Controller?.TestMargin.Glyphs.Count >= 7);
        var margin = editor!.Controller!.TestMargin;
        var failsLine = Array.FindIndex(File.ReadAllLines(file), l => l.Contains("void Fails()", StringComparison.Ordinal));
        var glyph = margin.Glyphs.Single(g => g.Value.Label == "Fails");
        Assert.InRange(glyph.Key, failsLine - 1, failsLine + 1);
        Assert.Equal(TestOutcome.Failed, glyph.Value.Outcome);
        await ShellFixture.PumpUntil(() => editor.Controller.Insights.EndOfLineText(glyph.Key)?.Contains("✗ Failed", StringComparison.Ordinal) == true); // CodeLens too

        // Run at cursor finds the test around the caret.
        Assert.Equal("Fails", f.Shell.Tests.FindAt(DocumentUris.FromPath(file), glyph.Key - 1)?.Label); // glyphs are by one-based line
        Assert.Equal("CalculatorTests", f.Shell.Tests.FindAt(DocumentUris.FromPath(file), glyph.Key - 2)?.Label);

        if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is { } snapshots)
        {
            f.Shell.Layout.ShowToolWindow(TestExplorerViewModel.ToolWindowId);
            explorer.Reveal(cls.Children.Single(c => c.Label == "Fails").Key);
            for (var i = 0; i < 10; i++)
            {
                ShellFixture.Pump();
            }

            using var frame = f.Window.CaptureRenderedFrame();
            Directory.CreateDirectory(snapshots);
            frame!.Save(Path.Combine(snapshots, "test-explorer.png"), PngBitmapEncoderOptions.Default);
        }
    }
}
