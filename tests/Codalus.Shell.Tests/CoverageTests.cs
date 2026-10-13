using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.LanguageServices.Protocol;
using Codalus.Plugins.Protocol;
using Codalus.Shell.Editor;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Testing;

namespace Codalus.Shell.Tests;

/// <summary>Coverage in the shell (ADR 0011): the editor's coloring and the Coverage Results window, fed a report directly.</summary>
public sealed class CoverageTests
{
    [AvaloniaFact]
    public async Task Coverage_colors_the_editor_and_fills_the_results_window()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1400;
        f.Window.Height = 900;
        var file = Path.Combine(f.UserData, "Calculator.cs");
        await File.WriteAllTextAsync(file, """
            namespace Calc;

            public static class Arithmetic
            {
                public static int Add(int a, int b) => a + b;

                public static int Divide(int a, int b)
                {
                    if (b == 0)
                    {
                        throw new System.DivideByZeroException();
                    }

                    return a / b;
                }
            }
            """, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-5));
        var editor = await f.Shell.OpenFileAsync(file);
        await ShellFixture.PumpUntil(() => editor?.Controller is not null);
        var margin = editor!.Controller!.CoverageMargin;
        Assert.Empty(margin.Lines);
        Assert.False(f.Shell.Workbench.Context.GetValue("coverageAvailable") is true);

        // A coverage run reports lines (zero-based) and methods.
        var uri = DocumentUris.FromPath(file);
        f.Shell.Tests.RunCoverage(new TestRunCoverageParams(
            "run-1",
            [new FileCoverageDto(uri, [new(4, 3, 0, 0), new(7, 2, 0, 0), new(8, 2, 1, 2), new(10, 0, 0, 0), new(13, 2, 0, 0)])],
            [
                new MethodCoverageDto("Calc", "Calc", "Arithmetic", "Add(int, int)", uri, 4, 1, 1, 0, 0),
                new MethodCoverageDto("Calc", "Calc", "Arithmetic", "Divide(int, int)", uri, 7, 3, 4, 1, 2),
            ]));
        await ShellFixture.PumpUntil(() => margin.Lines.Count == 5);
        Assert.Equal(LineCoverageState.Covered, margin.Lines[5].State);
        Assert.Equal(LineCoverageState.Partial, margin.Lines[9].State);
        Assert.Equal(LineCoverageState.Uncovered, margin.Lines[11].State);
        Assert.False(margin.IsStale);
        Assert.Equal("Partly covered (2 hits), 1 of 2 branches", CoverageMargin.Describe(margin.Lines[9]));
        Assert.True(f.Shell.Workbench.Context.GetValue("coverageAvailable") is true);

        // The results window opens by itself, down to the classes, with line and branch percentages.
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<CoverageResultsView>().Any());
        var results = f.Shell.CoverageResults;
        Assert.Equal(["Calc", "Calc", "Arithmetic"], results.Rows.Select(r => r.Label));
        var cls = results.Rows.Single(r => r.Label == "Arithmetic");
        Assert.Equal(("80 %", "4 / 5", "50 %"), (cls.LinePercent, cls.Lines, cls.BranchPercent));
        Assert.StartsWith("Lines 80 % (4 of 5)", results.Summary, StringComparison.Ordinal);
        results.SetExpanded(cls, true);
        Assert.Equal(["Add(int, int)", "Divide(int, int)"], results.Rows.Where(r => r.Depth == 3).Select(r => r.Label));
        results.FilterText = "Divide";
        Assert.Equal(["Calc", "Calc", "Arithmetic", "Divide(int, int)"], results.Rows.Select(r => r.Label));
        results.FilterText = string.Empty;

        // The export is a Cobertura report with one-based lines.
        var cobertura = f.Shell.Tests.Coverage.ToCobertura();
        Assert.Equal("0.8", cobertura.Root!.Attribute("line-rate")!.Value);
        Assert.Equal("50% (1/2)", cobertura.Descendants("class").Single().Element("lines")!.Elements("line").Single(l => l.Attribute("number")!.Value == "9").Attribute("condition-coverage")!.Value);

        // Editing the document fades its coverage; the toggle hides it; Clear removes it.
        editor.Controller.Editor.Document.Insert(0, "// edited\n");
        ShellFixture.Pump();
        Assert.True(margin.IsStale);
        await f.Shell.Workbench.Commands.ExecuteAsync("testing.toggleCoverageColoring", CancellationToken.None);
        await ShellFixture.PumpUntil(() => margin.Lines.Count == 0);
        Assert.False(results.ShowColoring);
        results.ShowColoring = true;
        await ShellFixture.PumpUntil(() => margin.Lines.Count == 5);
        await f.Shell.Workbench.Commands.ExecuteAsync("testing.clearCoverage", CancellationToken.None);
        await ShellFixture.PumpUntil(() => margin.Lines.Count == 0 && results.IsEmpty);
        Assert.False(f.Shell.Workbench.Context.GetValue("coverageAvailable") is true);
    }
}
