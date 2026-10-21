using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;

namespace Codalus.Shell.Tests;

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

    /// <summary>Saves the window when <c>CODALUS_UI_SNAPSHOTS</c> names a folder (for looking at the rendering).</summary>
    internal static void Screenshot(ShellFixture f, string name)
    {
        if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is { Length: > 0 } folder)
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
            var log = f.Shell.Workbench.Output.GetOrCreate("codalus.languages").GetText();
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

        // CodeLens: counts on a row of their own above declarations, where the code starts; clicking one runs Find All
        // References.
        var shapes = await OpenAsync(f, Path.Combine(folder, "src", "Lib", "Shapes.cs"));
        var interfaceLine = shapes.Model.Text.GetLineByOffset(shapes.Model.Text.Text.IndexOf("interface IShape", StringComparison.Ordinal)).LineNumber;
        await ShellFixture.PumpUntil(() => shapes.Insights.EndOfLineText(interfaceLine) is { } eol && eol.Contains("implementations", StringComparison.Ordinal), seconds: 60);
        Assert.Contains("2 references", shapes.Insights.LensRowAbove(interfaceLine), StringComparison.Ordinal);
        var areaDeclaration = shapes.Model.Text.GetLineByOffset(shapes.Model.Text.Text.IndexOf("double Area();", StringComparison.Ordinal)).LineNumber;
        await ShellFixture.PumpUntil(() => shapes.Insights.LensRowAbove(areaDeclaration) is not null, seconds: 60);
        ShellFixture.Pump();
        var view = shapes.Editor.TextArea.TextView;
        view.EnsureVisualLines();
        var declaration = view.GetVisualLine(areaDeclaration)!;
        var before = view.GetVisualLine(areaDeclaration - 1)!;
        Assert.Single(declaration.TextLines); // the declaration keeps its own height: no taller caret or selection
        Assert.Equal(view.DefaultLineHeight, declaration.Height, 1);
        Assert.Equal(2, before.TextLines.Count); // the line before ends, then the row
        var rowTop = before.VisualTop + before.TextLines[0].Height - view.VerticalOffset;
        var codeX = view.GetVisualPosition(new AvaloniaEdit.TextViewPosition(areaDeclaration, shapes.Model.Text.GetLineByNumber(areaDeclaration).Length - "double Area();".Length + 1), AvaloniaEdit.Rendering.VisualYPosition.LineTop).X;

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

        // Clicking the row runs its lens (Find All References from IShape.Area).
        shapes.Editor.CaretOffset = 0;
        shapes.Editor.ScrollTo(1, 1);
        await ShellFixture.PumpUntil(() => view.VerticalOffset == 0 && view.GetVisualLine(areaDeclaration - 1) is { TextLines.Count: 2 }, describe: () => $"offset {view.VerticalOffset}, lines {view.GetVisualLine(areaDeclaration - 1)?.TextLines.Count}, row '{shapes.Insights.LensRowAbove(areaDeclaration)}', folded {shapes.Insights.IsFoldedAway?.Invoke(shapes.Model.Text.GetLineByNumber(areaDeclaration - 1).EndOffset)}");
        before = view.GetVisualLine(areaDeclaration - 1)!;
        rowTop = before.VisualTop + before.TextLines[0].Height - view.VerticalOffset;
        var references = 0;
        f.Shell.Workbench.Commands.Executed += (_, e) => references += e.CommandId is "editor.action.showReferences" or "editor.action.referenceSearch" ? 1 : 0;
        var click = Avalonia.VisualExtensions.TranslatePoint(view, new Avalonia.Point(codeX + 8, rowTop + before.TextLines[1].Height / 2), f.Window)!.Value;
        f.Window.MouseDown(click, Avalonia.Input.MouseButton.Left);
        f.Window.MouseUp(click, Avalonia.Input.MouseButton.Left);
        await ShellFixture.PumpUntil(() => references > 0, describe: () => $"commands run: {references}; row '{shapes.Insights.LensRowAbove(areaDeclaration)}'");

        // CodeLens can be turned off.
        await f.Shell.Workbench.Settings.SetAsync(EditorOptions.CodeLensKey, false, Workbench.Settings.SettingScope.User, TestContext.Current.CancellationToken);
        await ShellFixture.PumpUntil(() => shapes.Insights.EndOfLineText(interfaceLine) is null);
    }

    [AvaloniaFact]
    public async Task Ctrl_hover_shows_a_symbol_as_a_link_to_its_definition()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;
        var folder = await f.OpenLanguageFeaturesAsync();
        var program = await OpenAsync(f, Path.Combine(folder, "src", "App", "Program.cs"));
        await ShellFixture.PumpUntil(() => program.Insights.Hints.Count > 0, seconds: 60); // the language service answers
        var view = program.Editor.TextArea.TextView;
        Avalonia.Point At(string text, int into)
        {
            var offset = program.Model.Text.Text.IndexOf(text, StringComparison.Ordinal) + into;
            var location = program.Model.Text.GetLocation(offset);
            var visual = view.GetVisualPosition(new AvaloniaEdit.TextViewPosition(location), AvaloniaEdit.Rendering.VisualYPosition.LineMiddle) - view.ScrollOffset;
            return Avalonia.VisualExtensions.TranslatePoint(view, new Avalonia.Point(visual.X + 2, visual.Y), f.Window)!.Value;
        }

        string Describe()
        {
            var log = f.Shell.LanguageLog.GetText();
            return $"{program.Link.State}; Quick Info open: {program.IsQuickInfoOpen}; pointer over the text: {view.IsPointerOver}; C#: {f.Shell.Languages.Status(ShellHost.CSharpLanguageId)}; "
                + $"restore: {f.Shell.Solutions.RestoreState}; design-time builds: {f.Shell.Solutions.DesignTimeProgress}. Language log: {log[Math.Max(0, log.Length - 3000)..]}";
        }

        // Ctrl+hover over a symbol until the condition holds, as a user would: the pointer stays on the symbol, moving
        // within it every couple of seconds. While design-time builds load the projects again (after the restore), the
        // service can briefly not know the symbol, and the link asks again on a later move (as the Navigation test asks
        // Peek again).
        async Task HoverUntil(string text, Func<bool> condition, string what)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            for (var move = 0; !condition(); move++)
            {
                if (started.Elapsed >= TimeSpan.FromSeconds(30))
                {
                    Assert.Fail($"No {what} after {move} moves within '{text}': {Describe()}");
                }

                ShellFixture.RenderFrame(f.Window);
                f.Window.MouseMove(At(text, 2 + move % 2), Avalonia.Input.RawInputModifiers.Control);
                var moved = System.Diagnostics.Stopwatch.StartNew();
                await ShellFixture.PumpUntil(() => condition() || moved.Elapsed >= TimeSpan.FromSeconds(2));
            }
        }

        // Without Ctrl, nothing; with Ctrl over Scale, the method is a link: underlined, the pointer a hand.
        var scale = program.Model.Text.Text.IndexOf("Scale(", StringComparison.Ordinal);
        f.Window.MouseMove(At("Scale(", 1));
        ShellFixture.Pump();
        Assert.Null(program.Link.Link);
        await HoverUntil("Scale(", () => program.Link.Link is not null, "link");
        Assert.Equal((scale, scale + "Scale".Length), program.Link.Link);
        Assert.Equal("Hand", view.Cursor?.ToString());

        // Resting there opens Quick Info, which leaves the link alone: its light dismiss lets the pointer through to the
        // text (it took the pointer, and the link with it). Moving off the symbol, above Quick Info past the end of a
        // line, closes Quick Info and takes the link away.
        await HoverUntil("Scale(", () => program.IsQuickInfoOpen, "Quick Info");
        ShellFixture.RenderFrame(f.Window);
        ShellFixture.Pump();
        Assert.True(view.IsPointerOver, Describe());
        Assert.Equal((scale, scale + "Scale".Length), program.Link.Link);
        var endOfLine = At("new Circle(2);", "new Circle(2);".Length);
        ShellFixture.RenderFrame(f.Window);
        f.Window.MouseMove(new Avalonia.Point(endOfLine.X + 100, endOfLine.Y), Avalonia.Input.RawInputModifiers.Control);
        await ShellFixture.PumpUntil(() => program.Link.Link is null && !program.IsQuickInfoOpen, describe: Describe);

        // Text in a string isn't a link, and letting go of Ctrl takes the link away.
        f.Window.MouseMove(At("\"Ada\"", 2), Avalonia.Input.RawInputModifiers.Control);
        await ShellFixture.PumpUntil(() => program.Link.Link is null);
        await HoverUntil("Scale(", () => program.Link.Link is not null, "link");
        f.Window.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.ControlLeft, Avalonia.Input.RawInputModifiers.None);
        await ShellFixture.PumpUntil(() => program.Link.Link is null);
        Assert.NotEqual("Hand", view.Cursor?.ToString());
    }

    [AvaloniaFact]
    public async Task The_breakpoint_margin_comes_before_the_line_numbers_and_shows_where_a_click_adds_one()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;
        var folder = await f.OpenLanguageFeaturesAsync();
        var shapes = await OpenAsync(f, Path.Combine(folder, "src", "Lib", "Shapes.cs"));
        var margins = shapes.Editor.TextArea.LeftMargins;
        var numbers = margins.IndexOf(margins.OfType<AvaloniaEdit.Editing.LineNumberMargin>().Single());
        Assert.True(margins.IndexOf(shapes.BreakpointMargin) < numbers, string.Join(", ", margins.Select(m => m.GetType().Name)));
        Assert.True(margins.IndexOf(shapes.Folding.GetType().Assembly.GetType("AvaloniaEdit.Folding.FoldingMargin") is { } type ? margins.First(type.IsInstanceOfType) : margins[^1]) > numbers);

        // Over a line, the margin shows where a click puts the breakpoint; the pointer leaving takes it away.
        var area = shapes.Model.Text.GetLineByOffset(shapes.Model.Text.Text.IndexOf("double Area();", StringComparison.Ordinal)).LineNumber;
        await ShellFixture.PumpUntil(() => shapes.Insights.LensRowAbove(area) is not null, seconds: 60);
        ShellFixture.Pump();
        var view = shapes.Editor.TextArea.TextView;
        view.EnsureVisualLines();
        Avalonia.Point InMargin(double y) => Avalonia.VisualExtensions.TranslatePoint(shapes.BreakpointMargin, new Avalonia.Point(shapes.BreakpointMargin.Bounds.Width / 2, y - view.VerticalOffset), f.Window)!.Value;
        var line = view.GetVisualLine(area)!;
        f.Window.MouseMove(InMargin(line.VisualTop + line.Height / 2));
        await ShellFixture.PumpUntil(() => shapes.BreakpointMargin.HoverLine == area);

        // Next to the CodeLens row above it, the margin is the declaration's too: a click there sets its breakpoint.
        var before = view.GetVisualLine(area - 1)!;
        var row = InMargin(before.VisualTop + before.TextLines[0].Height + before.TextLines[1].Height / 2);
        f.Window.MouseMove(row);
        ShellFixture.Pump();
        Assert.Equal(area, shapes.BreakpointMargin.HoverLine);
        f.Window.MouseDown(row, Avalonia.Input.MouseButton.Left);
        f.Window.MouseUp(row, Avalonia.Input.MouseButton.Left);
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.Breakpoints.At(shapes.Model.Path, area) is not null);
        Assert.Null(f.Shell.Debugger.Breakpoints.At(shapes.Model.Path, area - 1));
        f.Shell.Debugger.Breakpoints.Clear();
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

        // The editor's view, read each time: it can be attached (or replaced) after the document opened, and Peek opens
        // in the one that is current.
        CodeEditorView? View() => f.Shell.ActiveEditor?.View;

        // Again every few seconds: while design-time builds reload the projects, the definition can be briefly unknown.
        // CI's two-core runners can take minutes, as for loading the projects. Messages last 5 s, so keep the last one.
        string? lastMessage = null;
        f.Shell.StatusBar.PropertyChanged += (_, e) => lastMessage = e.PropertyName == nameof(f.Shell.StatusBar.Message) && f.Shell.StatusBar.Message is { } m ? m : lastMessage;
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (View()?.Peek?.IsOpen != true)
        {
            if (started.Elapsed > TimeSpan.FromSeconds(180))
            {
                var log = f.Shell.LanguageLog.GetText();
                Assert.Fail($"Peek Definition did not open. Last message: {lastMessage}. Language log: {log[Math.Max(0, log.Length - 4000)..]}");
            }

            await f.RunAsync("editor.action.peekDefinition");
            try
            {
                await ShellFixture.PumpUntil(() => View()?.Peek?.IsOpen == true, seconds: 10);
            }
            catch (Xunit.Sdk.XunitException)
            {
                // Not yet: ask again.
            }
        }

        var view = View()!;
        Assert.Contains("class Circle", view.Peek!.Viewer.Editor.Document.Text, StringComparison.Ordinal);
        Assert.Equal(program, f.Shell.ActiveEditor!.Controller);
        view.Peek.Close();

        // The Code Definition window follows the caret.
        f.Shell.Layout.ShowToolWindow(ViewModels.CodeDefinitionViewModel.ToolWindowId, focus: false);
        await f.Shell.UpdateCodeDefinitionAsync(CancellationToken.None);
        await ShellFixture.PumpUntil(() => f.Shell.CodeDefinition.Title.StartsWith("Shapes.cs", StringComparison.Ordinal), seconds: 30);

        // F1 help: framework symbols open their API reference page.
        Uri? help = null;
        f.Shell.OpenHelpUrl = uri => help = uri;
        program.Editor.CaretOffset = program.Model.Text.Text.IndexOf("Console.WriteLine(json)", StringComparison.Ordinal) + 2;
        await f.RunAsync("editor.action.showHelp");
        await ShellFixture.PumpUntil(() => help is not null, seconds: 30);
        Assert.Equal("https://learn.microsoft.com/dotnet/api/system.console", help!.ToString());

        // Find All References: classified, grouped by file or project, filtered, kept.
        program.Editor.CaretOffset = program.Model.Text.Text.IndexOf("circle =", StringComparison.Ordinal) + 1;
        await f.Shell.FindReferencesAsync();
        var results = f.Shell.FindResults;
        Assert.Contains("References to 'circle'", results.Summary, StringComparison.Ordinal);
        Assert.Equal([ReferenceItemKinds.Declaration, ReferenceItemKinds.Read, ReferenceItemKinds.Read], results.Rows.Where(r => !r.IsHeader).Select(r => r.Kind));
        results.GroupBy = "Project";
        // M34h: a group's header is its label with its count as a badge (the count stays in its Code and screen readers' name).
        Assert.Equal(("App", 3), (results.Rows[0].Label, results.Rows[0].Count));
        Assert.Equal("App (3)", results.Rows[0].Code);
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
        await f.RunAsync("workbench.action.navigateToLastEditLocation");
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
        await f.RunAsync("editor.action.showCallHierarchy");
        await ShellFixture.PumpUntil(() => f.Shell.Hierarchy.Roots.Count == 1 && f.Shell.Hierarchy.Roots[0].Children.Count == 2, seconds: 30);
        var callsTo = f.Shell.Hierarchy.Roots[0].Children[0];
        Assert.Equal("Calls To 'Area'", callsTo.Label);
        await callsTo.LoadAsync();
        Assert.Contains(callsTo.Children, c => c.Label == "Scale");
        Assert.Contains(callsTo.Children, c => c.Label == "Describe");

        // Type Hierarchy: derived types of Shape.
        shapes.Editor.CaretOffset = shapes.Model.Text.Text.IndexOf("abstract class Shape", StringComparison.Ordinal) + "abstract class ".Length;
        await f.RunAsync("editor.action.showTypeHierarchy");
        await ShellFixture.PumpUntil(() => f.Shell.Hierarchy.Title == "Type Hierarchy: Shape" && f.Shell.Hierarchy.Roots[0].Children.Count == 2, seconds: 30);
        var derived = f.Shell.Hierarchy.Roots[0].Children[1];
        await derived.LoadAsync();
        Assert.Equal(["Circle", "Square"], derived.Children.Select(c => c.Label));
        Screenshot(f, "m12a-navigation");

        // F8: the next problem (a warning in Reporter.cs).
        var reporter = await OpenAsync(f, Path.Combine(folder, "src", "App", "Reporter.cs"));
        await ShellFixture.PumpUntil(() => reporter.Markers.Segments.Any(s => s.Diagnostic.Code == "CA2200"), seconds: 60);
        reporter.Editor.CaretOffset = 0;
        await f.RunAsync("editor.action.marker.next");
        var warningLine = f.Shell.Languages.Diagnostics.For(reporter.Model.Path).First(d => d.Code == "CA2200").Range.Start.Line + 1;
        await ShellFixture.PumpUntil(() => reporter.Caret.Line == warningLine);
    }
}
