using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Shell.Editor;

namespace DaedalusStudio.Shell.Tests;

/// <summary>M12a in the editor: inlay hints, CodeLens, the inheritance margin and bracket colors on samples/LanguageFeatures.</summary>
public class NavigationDepthTests
{
    private static async Task<CodeEditorController> OpenAsync(ShellFixture f, string path)
    {
        var document = await f.Shell.OpenFileAsync(path);
        Assert.NotNull(document);
        await ShellFixture.PumpUntil(() => document.Controller is not null);
        return document.Controller!;
    }

    /// <summary>Saves the window when <c>DAEDALUS_UI_SNAPSHOTS</c> names a folder (for looking at the rendering).</summary>
    internal static void Screenshot(ShellFixture f, string name)
    {
        if (Environment.GetEnvironmentVariable("DAEDALUS_UI_SNAPSHOTS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            foreach (var visual in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(f.Window))
            {
                visual.InvalidateVisual();
            }

            ShellFixture.Pump();
            using var frame = f.Window.CaptureRenderedFrame();
            frame?.Save(Path.Combine(folder, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }

    /// <summary>The color of the first bracket on a one-based line.</summary>
    private static Avalonia.Media.Color BraceColor(CodeEditorController editor, int line)
    {
        var documentLine = editor.Model.Text.GetLineByNumber(line);
        var text = editor.Model.Text.GetText(documentLine);
        var column = text.IndexOfAny(['{', '(']);
        Assert.True(column >= 0, $"no bracket on line {line}: {text}");
        var visual = editor.Editor.TextArea.TextView.GetOrConstructVisualLine(documentLine);
        var element = visual.Elements.First(e => e.RelativeTextOffset <= column && column < e.RelativeTextOffset + Math.Max(1, e.DocumentLength));
        return Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(element.TextRunProperties.ForegroundBrush).Color;
    }

    [AvaloniaFact]
    public async Task Inline_information_shows_hints_lenses_and_inheritance_glyphs()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1400;
        f.Window.Height = 900;
        var folder = await f.OpenLanguageFeaturesAsync();

        // Inlay hints: parameter names before literals, not caret stops.
        var program = await OpenAsync(f, Path.Combine(folder, "src", "App", "Program.cs"));
        try
        {
            await ShellFixture.PumpUntil(() => program.Insights.Hints.Any(h => h.Label == "radius:"), seconds: 60);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var log = f.Shell.Workbench.Output.GetOrCreate("daedalus.languages").GetText();
            throw new InvalidOperationException($"No inlay hints ({program.Insights.Hints.Count}; client {f.Shell.Languages.RunningClient("csharp")?.Capabilities?.InlayHintProvider}). Log:\n{log[Math.Max(0, log.Length - 4000)..]}", ex);
        }

        var text = program.Model.Text.Text;
        Assert.Contains(program.Insights.Hints, h => h.Label == "factor:" && h.Offset == text.IndexOf("3)", StringComparison.Ordinal));
        Assert.DoesNotContain(program.Insights.Hints, h => h.Label.StartsWith(':')); // types are off by default

        var radius = program.Insights.Hints.Single(h => h.Label == "radius:").Offset;
        program.Editor.CaretOffset = radius;
        program.Editor.TextArea.Focus();
        f.Press(Key.Right);
        Assert.Equal(radius + 1, program.Editor.CaretOffset);
        f.Press(Key.Left);
        f.Press(Key.Left);
        Assert.Equal(radius - 1, program.Editor.CaretOffset);

        // CodeLens: counts at the end of declarations; clicking one runs Find All References.
        var shapes = await OpenAsync(f, Path.Combine(folder, "src", "Lib", "Shapes.cs"));
        var interfaceLine = shapes.Model.Text.GetLineByOffset(shapes.Model.Text.Text.IndexOf("interface IShape", StringComparison.Ordinal)).LineNumber;
        await ShellFixture.PumpUntil(() => shapes.Insights.EndOfLineText(interfaceLine) is { } eol && eol.Contains("implementations", StringComparison.Ordinal), seconds: 60);
        Assert.Contains("2 references", shapes.Insights.EndOfLineText(interfaceLine), StringComparison.Ordinal);

        // The inheritance margin: Circle.Area overrides and implements.
        var areaLine = shapes.Model.Text.GetLineByOffset(shapes.Model.Text.Text.IndexOf("override double Area() => System.Math.PI", StringComparison.Ordinal)).LineNumber;
        await ShellFixture.PumpUntil(() => shapes.Insights.Margin.Items.ContainsKey(areaLine), seconds: 60);
        Assert.Equal([InheritanceRelations.Implements, InheritanceRelations.Overrides], shapes.Insights.Margin.Items[areaLine].Relations.Order());

        // Bracket pair colors: the braces of nested blocks differ.
        var describeLine = shapes.Model.Text.GetLineByOffset(shapes.Model.Text.Text.IndexOf("virtual string Describe(int precision)", StringComparison.Ordinal)).LineNumber;
        Assert.NotEqual(BraceColor(shapes, interfaceLine + 1), BraceColor(shapes, describeLine)); // { at depth 0, ( at depth 1
        // The navigation bar: the document's types, and the members of the type at the caret.
        var shapesView = f.Shell.ActiveEditor!.View!;
        await ShellFixture.PumpUntil(() => shapesView.NavigationBar.Types.Count == 5, seconds: 30);
        Assert.Equal(["IShape", "Shape", "Circle", "Square", "Geometry"], shapesView.NavigationBar.Types.Select(t => t.Display));
        shapes.Editor.CaretOffset = shapes.Model.Text.Text.IndexOf("override double Area() => System.Math.PI", StringComparison.Ordinal) + "override double ".Length;
        await ShellFixture.PumpUntil(() => shapesView.NavigationBar.SelectedMember?.Symbol.Name == "Area");
        Assert.Equal("Circle", shapesView.NavigationBar.SelectedType!.Display);
        var geometry = shapesView.NavigationBar.Types.Single(t => t.Display == "Geometry");
        shapesView.NavigationBar.Pick(geometry, member: false);
        await ShellFixture.PumpUntil(() => shapesView.NavigationBar.SelectedType == geometry);
        Assert.Equal(2, shapesView.NavigationBar.Members.Count);

        // Sticky scroll: scrolled into Circle, its header stays pinned (with the file-scoped namespace).
        var circleLine = shapes.Model.Text.GetLineByOffset(shapes.Model.Text.Text.IndexOf("class Circle", StringComparison.Ordinal)).LineNumber;
        shapes.Editor.ScrollToVerticalOffset((circleLine + 2) * shapes.Editor.TextArea.TextView.DefaultLineHeight);
        await ShellFixture.PumpUntil(() => shapesView.StickyScroll.Shown.Contains(circleLine));
        Assert.Equal(1, shapesView.StickyScroll.Shown[0]);
        Assert.Equal(Avalonia.Media.Colors.White, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(shapesView.StickyScroll.Background).Color); // opaque, the editor's background

        // The overview ruler's health: no problems in Shapes.cs.
        Assert.Equal((0, 0), shapesView.OverviewRuler.Health);
        Screenshot(f, "m12a-inline-information");

        // CodeLens can be turned off.
        await f.Shell.Workbench.Settings.SetAsync(EditorOptions.CodeLensKey, false, Workbench.Settings.SettingScope.User, TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => shapes.Insights.EndOfLineText(interfaceLine) is null);
    }

    [AvaloniaFact]
    public async Task Navigation_commands_and_tool_windows_work()
    {
        await using var f = ShellFixture.Create();
        var folder = await f.OpenLanguageFeaturesAsync();
        var shapesPath = Path.Combine(folder, "src", "Lib", "Shapes.cs");
        var program = await OpenAsync(f, Path.Combine(folder, "src", "App", "Program.cs"));

        // Peek Definition: the definition in a popup, without leaving the file.
        program.Editor.CaretOffset = program.Model.Text.Text.IndexOf("Circle(2)", StringComparison.Ordinal) + 1;
        f.Shell.Run("editor.action.peekDefinition");
        var view = f.Shell.ActiveEditor!.View!;
        await ShellFixture.PumpUntil(() => view.Peek?.IsOpen == true, seconds: 60);
        Assert.Contains("class Circle", view.Peek!.Viewer.Editor.Document.Text, StringComparison.Ordinal);
        Assert.Equal(program, f.Shell.ActiveEditor.Controller);
        view.Peek.Close();

        // The Code Definition window follows the caret.
        f.Shell.Layout.ShowToolWindow(ViewModels.CodeDefinitionViewModel.ToolWindowId, focus: false);
        await f.Shell.UpdateCodeDefinitionAsync(CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.CodeDefinition.Title.StartsWith("Shapes.cs", StringComparison.Ordinal), seconds: 30);

        // F1 help: framework symbols open their API reference page.
        Uri? help = null;
        f.Shell.OpenHelpUrl = uri => help = uri;
        program.Editor.CaretOffset = program.Model.Text.Text.IndexOf("Console.WriteLine(json)", StringComparison.Ordinal) + 2;
        f.Shell.Run("editor.action.showHelp");
        await ShellFixture.PumpUntil(() => help is not null, seconds: 30);
        Assert.Equal("https://learn.microsoft.com/dotnet/api/system.console", help!.ToString());

        // Find All References: classified, grouped by file or project, filtered, kept.
        program.Editor.CaretOffset = program.Model.Text.Text.IndexOf("circle =", StringComparison.Ordinal) + 1;
        await f.Shell.FindReferencesAsync();
        var results = f.Shell.FindResults;
        Assert.Contains("References to 'circle'", results.Summary, StringComparison.Ordinal);
        Assert.Equal([ReferenceItemKinds.Declaration, ReferenceItemKinds.Read, ReferenceItemKinds.Read], results.Rows.Where(r => !r.IsHeader).Select(r => r.Kind));
        results.GroupBy = "Project";
        Assert.Equal("App (3)", results.Rows[0].Text);
        results.KindFilter = "Read";
        Assert.Equal(2, results.Rows.Count(r => !r.IsHeader));
        results.KindFilter = "All";
        program.Editor.CaretOffset = program.Model.Text.Text.IndexOf("Circle(2)", StringComparison.Ordinal) + 1;
        await f.Shell.FindReferencesAsync();
        Assert.Equal(["References to 'Circle'", "References to 'circle'"], results.ReferenceSets.Select(s => s.Title));
        results.CurrentSet = results.ReferenceSets[1];
        await ShellFixture.PumpUntil(() => results.Rows.Count(r => !r.IsHeader) == 3);
        results.GroupBy = "File";

        // Go to Last Edit Location returns to the edit after moving elsewhere.
        program.Editor.CaretOffset = program.Model.Text.Text.IndexOf("Reporter.Run();", StringComparison.Ordinal);
        program.Editor.TextArea.PerformTextInput("// edited\n");
        var shapes = await OpenAsync(f, shapesPath);
        f.Shell.Run("workbench.action.navigateToLastEditLocation");
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model == program.Model && f.Shell.ActiveEditor.Controller is not null);
        program = f.Shell.ActiveEditor!.Controller!;
        Assert.Equal(program.Model.Text.GetLineByOffset(program.Model.Text.Text.IndexOf("Reporter.Run();", StringComparison.Ordinal)).LineNumber, program.Caret.Line);
        program.Editor.Undo();

        // Go to Base: Circle.Describe goes to Shape.Describe.
        shapes = await OpenAsync(f, shapesPath);
        shapes.Editor.CaretOffset = shapes.Model.Text.Text.IndexOf("override string Describe", StringComparison.Ordinal) + "override string ".Length;
        await f.Shell.GoToBaseAsync();
        var baseLine = shapes.Model.Text.GetLineByOffset(shapes.Model.Text.Text.IndexOf("virtual string Describe", StringComparison.Ordinal)).LineNumber;
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Controller?.Caret.Line == baseLine, seconds: 60);
        shapes = f.Shell.ActiveEditor!.Controller!;

        // The Document Outline lists the types and follows the caret.
        f.Shell.Layout.ShowToolWindow(ViewModels.DocumentOutlineViewModel.ToolWindowId, focus: false);
        await ShellFixture.PumpUntil(() => f.Shell.DocumentOutline.Roots.Count == 1 && f.Shell.DocumentOutline.Roots[0].Children.Count == 5, seconds: 30);
        await ShellFixture.PumpUntil(() => f.Shell.DocumentOutline.Current?.Name == "Describe");
        f.Shell.DocumentOutline.Filter = "Scale";
        Assert.Equal(["Scale", "Scale"], f.Shell.DocumentOutline.Roots.Select(r => r.Name));
        f.Shell.DocumentOutline.Navigate(f.Shell.DocumentOutline.Roots[0]);
        Assert.Contains("Scale(IShape", shapes.Model.Text.GetText(shapes.Model.Text.GetLineByNumber(shapes.Caret.Line)), StringComparison.Ordinal);
        f.Shell.DocumentOutline.Filter = string.Empty;

        // Call Hierarchy: callers of IShape.Area, recursively.
        shapes.Editor.CaretOffset = shapes.Model.Text.Text.IndexOf("double Area();", StringComparison.Ordinal) + "double ".Length;
        f.Shell.Run("editor.action.showCallHierarchy");
        await ShellFixture.PumpUntil(() => f.Shell.Hierarchy.Roots.Count == 1 && f.Shell.Hierarchy.Roots[0].Children.Count == 2, seconds: 30);
        var callsTo = f.Shell.Hierarchy.Roots[0].Children[0];
        Assert.Equal("Calls To 'Area'", callsTo.Label);
        await callsTo.LoadAsync();
        Assert.Contains(callsTo.Children, c => c.Label == "Scale");
        Assert.Contains(callsTo.Children, c => c.Label == "Describe");

        // Type Hierarchy: derived types of Shape.
        shapes.Editor.CaretOffset = shapes.Model.Text.Text.IndexOf("abstract class Shape", StringComparison.Ordinal) + "abstract class ".Length;
        f.Shell.Run("editor.action.showTypeHierarchy");
        await ShellFixture.PumpUntil(() => f.Shell.Hierarchy.Title == "Type Hierarchy: Shape" && f.Shell.Hierarchy.Roots[0].Children.Count == 2, seconds: 30);
        var derived = f.Shell.Hierarchy.Roots[0].Children[1];
        await derived.LoadAsync();
        Assert.Equal(["Circle", "Square"], derived.Children.Select(c => c.Label));
        Screenshot(f, "m12a-navigation");

        // F8: the next problem (a warning in Reporter.cs).
        var reporter = await OpenAsync(f, Path.Combine(folder, "src", "App", "Reporter.cs"));
        await ShellFixture.PumpUntil(() => reporter.Markers.Segments.Any(s => s.Diagnostic.Code == "CA2200"), seconds: 60);
        reporter.Editor.CaretOffset = 0;
        f.Shell.Run("editor.action.marker.next");
        var warningLine = f.Shell.Languages.Diagnostics.For(reporter.Model.Path).First(d => d.Code == "CA2200").Range.Start.Line + 1;
        await ShellFixture.PumpUntil(() => reporter.Caret.Line == warningLine);
    }
}
