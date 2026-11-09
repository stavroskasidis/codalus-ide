using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.Editor;
using Codalus.Shell.Editor.IntelliSense;
using Codalus.Shell.Views;
using Codalus.Workbench.Git;

namespace Codalus.Shell.Tests;

/// <summary>
/// The maintainer's report: a diff opened from Git Changes had no Quick Info and no CodeLens, which the classic IDE's
/// diff has. The working-tree side is the file's document (the diff holds it when no editor has it open), so its
/// language service answers: Quick Info on its lines and the other side's unchanged lines, its CodeLens rows above its
/// declarations and a blank row opposite. On samples/LanguageFeatures with the real C# host.
/// </summary>
public sealed class DiffInsightsTests
{
    [AvaloniaFact]
    public async Task A_working_tree_diff_has_Quick_Info_and_CodeLens_without_the_file_open()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1400;
        f.Window.Height = 900;
        var folder = await f.OpenLanguageFeaturesAsync();
        DiffDocumentTests.Git(folder, "init", "-q", "-b", "main");
        DiffDocumentTests.Git(folder, "config", "user.name", "Ada Tester");
        DiffDocumentTests.Git(folder, "config", "user.email", "ada@example.com");
        DiffDocumentTests.Git(folder, "config", "commit.gpgsign", "false");
        DiffDocumentTests.Git(folder, "config", "core.autocrlf", "false");
        DiffDocumentTests.Git(folder, "add", ".");
        DiffDocumentTests.Git(folder, "commit", "-q", "-m", "initial");
        Assert.True(await f.Shell.Git.OpenAsync(folder, CancellationToken.None));

        // An edit in Circle: a removed and an added line; IShape above it is unchanged.
        var shapes = Path.Combine(folder, "src", "Lib", "Shapes.cs");
        var original = await File.ReadAllTextAsync(shapes, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(shapes, original.Replace("public double Radius { get; } = radius;", "public double Radius { get; init; } = radius;", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(f.Shell.Layout.Documents, d => d is CodeEditorDocument);
        var document = await f.Shell.OpenDiffAsync(shapes, staged: false);
        Assert.NotNull(document);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<DiffView>().Any(v => v.ViewModel == document.ViewModel) && document.ViewModel.Insights is not null);
        var view = f.Window.GetVisualDescendants().OfType<DiffView>().Single(v => v.ViewModel == document.ViewModel);
        var right = view.EditorOf(DiffPaneSide.New).Text;
        int Line(string text, string contains) => Array.FindIndex(text.Split('\n'), l => l.Contains(contains, StringComparison.Ordinal)) + 1;

        // CodeLens: the service's counts above IShape on the working-tree side, a blank row as tall opposite.
        var shapeLine = Line(right, "public interface IShape");
        await ShellFixture.PumpUntil(
            () => view.LensRowAbove(DiffPaneSide.New, shapeLine) is { } row && row.Contains("references", StringComparison.Ordinal),
            seconds: 90,
            describe: () => $"row '{view.LensRowAbove(DiffPaneSide.New, shapeLine)}', lens lines {string.Join(", ", document.ViewModel.Insights?.LensLines ?? [])}");
        Assert.Equal(string.Empty, view.LensRowAbove(DiffPaneSide.Old, shapeLine));
        var textView = view.EditorOf(DiffPaneSide.New).TextArea.TextView;
        var oldView = view.EditorOf(DiffPaneSide.Old).TextArea.TextView;
        view.EditorOf(DiffPaneSide.New).ScrollToHome(); // the diff opened at its first change, in Circle
        await ShellFixture.PumpUntil(() => oldView.VerticalOffset == 0 && textView.VerticalOffset == 0);
        textView.EnsureVisualLines();
        oldView.EnsureVisualLines();
        Assert.Equal(2, textView.GetVisualLine(shapeLine - 1)!.TextLines.Count); // the line before ends, then the row
        Assert.Equal(textView.GetVisualLine(shapeLine - 1)!.Height, oldView.GetVisualLine(shapeLine - 1)!.Height, 1); // the sides stay aligned
        Assert.Equal(textView.GetVisualTopByDocumentLine(shapeLine), oldView.GetVisualTopByDocumentLine(shapeLine), 1);
        Assert.True(
            Math.Abs(textView.TranslatePoint(default, f.Window)!.Value.Y - oldView.TranslatePoint(default, f.Window)!.Value.Y) < 0.5,
            $"the sides' code starts at {oldView.TranslatePoint(default, f.Window)} and {textView.TranslatePoint(default, f.Window)}: rows {view.RowHeights(DiffPaneSide.Old)} and {view.RowHeights(DiffPaneSide.New)}");

        // Quick Info on the working-tree side.
        var areaLine = Line(right, "double Area();");
        await view.ShowQuickInfoAtAsync(DiffPaneSide.New, areaLine, right.Split('\n')[areaLine - 1].IndexOf("Area", StringComparison.Ordinal));
        await ShellFixture.PumpUntil(() => view.IsQuickInfoOpen && view.LastQuickInfo?.Symbol is not null, seconds: 60);
        Assert.Contains("Area", DescriptionView.TextOf(view.LastQuickInfo!.Symbol!.SignatureBlock!), StringComparison.Ordinal);
        NavigationDepthTests.Screenshot(f, "diff-quick-info");


        // And on the old side's unchanged lines, asked about as the working tree's same line.
        var left = view.EditorOf(DiffPaneSide.Old).Text;
        var shapeOnLeft = Line(left, "public interface IShape");
        await view.ShowQuickInfoAtAsync(DiffPaneSide.Old, shapeOnLeft, left.Split('\n')[shapeOnLeft - 1].IndexOf("IShape", StringComparison.Ordinal));
        await ShellFixture.PumpUntil(() => view.IsQuickInfoOpen && view.LastQuickInfo?.Symbol is { } symbol && DescriptionView.TextOf(symbol.SignatureBlock!).Contains("IShape", StringComparison.Ordinal), seconds: 60);

        // Not on a removed line: that text is only in the index.
        var removed = Line(left, "{ get; } = radius;");
        var quickInfo = view.LastQuickInfo;
        await view.ShowQuickInfoAtAsync(DiffPaneSide.Old, removed, left.Split('\n')[removed - 1].IndexOf("Radius", StringComparison.Ordinal));
        Assert.Same(quickInfo, view.LastQuickInfo);

        // A changed declaration's row is drawn with its line: Radius's on the added line's fill, blank opposite on the removed one's.
        var radiusLine = Line(right, "public double Radius");
        view.ShowRow(document.ViewModel.NextChange(-1, 1)!.Value); // the change, in Circle
        await ShellFixture.PumpUntil(() => view.LensRowAbove(DiffPaneSide.New, radiusLine) is { Length: > 0 }, seconds: 60);
        NavigationDepthTests.Screenshot(f, "diff-codelens");

        // Inline: the rows above the working tree's declarations.
        document.ViewModel.SideBySide = false;
        await ShellFixture.PumpUntil(() => view.LensRowAbove(DiffPaneSide.Inline, Line(view.EditorOf(DiffPaneSide.Inline).Text, "public interface IShape")) is { } row && row.Contains("references", StringComparison.Ordinal));
        document.ViewModel.SideBySide = true;

        // Clicking a row runs its lens where its declaration is: the file opens there.
        var references = 0;
        f.Shell.Workbench.Commands.Executed += (_, e) => references += e.CommandId is "editor.action.showReferences" or "editor.action.referenceSearch" ? 1 : 0;
        view.ClickLensRow(DiffPaneSide.New, shapeLine);
        await ShellFixture.PumpUntil(() => references > 0 && f.Shell.ActiveEditor?.Model.Path == shapes, seconds: 60, describe: () => $"commands run: {references}, active {f.Shell.ActiveEditor?.Model.Path}");

        // Closing the diff with the editor open keeps the document; the editor holds it now.
        f.Shell.Layout.CloseDocument(document);
        Assert.NotNull(f.Shell.DiffServices.OpenDocument(shapes));
    }
}
