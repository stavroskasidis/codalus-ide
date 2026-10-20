using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Codalus.Shell.Editor;

namespace Codalus.Shell.Tests;

/// <summary>The language registry and TextMate highlighting (ADR 0016) in the headless shell.</summary>
public sealed class LanguageTests
{
    private static async Task<CodeEditorController> OpenAsync(ShellFixture f, string name, string text)
    {
        var path = Path.Combine(f.UserData, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
        await f.Shell.OpenFileAsync(path, preview: false, null);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == path && f.Shell.ActiveEditor.Controller is not null);
        return f.Shell.ActiveEditor!.Controller!;
    }

    /// <summary>The distinct foreground brushes on a rendered line (more than one: the grammar colored it).</summary>
    private static HashSet<string> Colors(CodeEditorController controller, int line)
    {
        var view = controller.Editor.TextArea.TextView;
        view.EnsureVisualLines();
        var visual = view.GetVisualLine(line);
        return visual is null
            ? []
            : [.. visual.Elements.Select(e => e.TextRunProperties.ForegroundBrush is ISolidColorBrush brush ? brush.Color.ToString() : "?")];
    }

    [AvaloniaFact]
    public async Task Files_get_their_language_and_grammar_colors()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;

        var json = await OpenAsync(f, "appsettings.json", "{\n  \"Logging\": { \"Level\": 3, \"On\": true }\n}\n");
        Assert.Equal("json", json.Model.LanguageId);
        Assert.Equal("source.json", json.GrammarScope);
        await ShellFixture.PumpUntil(() => Colors(json, 2).Count >= 3, seconds: 20);

        var project = await OpenAsync(f, "Sample.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        Assert.Equal("msbuild", project.Model.LanguageId);
        Assert.Equal("text.xml", project.GrammarScope);
        await ShellFixture.PumpUntil(() => Colors(project, 1).Count >= 2, seconds: 20);

        var editorConfig = await OpenAsync(f, ".editorconfig", "root = true\n\n[*.cs]\nindent_size = 4\n");
        Assert.Equal("editorconfig", editorConfig.Model.LanguageId);
        Assert.NotNull(editorConfig.GrammarScope);

        var script = await OpenAsync(f, "build", "#!/usr/bin/env bash\necho hi\n");
        Assert.Equal("shellscript", script.Model.LanguageId);

        // C# keeps Roslyn's semantic coloring, without a grammar.
        var cs = await OpenAsync(f, "Program.cs", "class C { }\n");
        Assert.Equal("csharp", cs.Model.LanguageId);
        Assert.Null(cs.GrammarScope);

        // Language configurations: from the bundled grammars, and C#'s own.
        Assert.Equal("//", f.Shell.TextMate.ConfigurationFor("csharp")?.LineComment);
        Assert.Equal(("<!--", "-->"), f.Shell.TextMate.ConfigurationFor("xml")?.BlockComment);
        Assert.Equal("#", f.Shell.TextMate.ConfigurationFor("yaml")?.LineComment);
        Assert.True(f.Shell.TextMate.ConfigurationFor("yaml")?.OffSide);
    }

    [AvaloniaFact]
    public async Task Sticky_scroll_draws_its_lines_as_the_editor_colors_them()
    {
        // The maintainer's report: the pinned lines were plain text in the default color, unlike the code under them.
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;
        var body = string.Concat(Enumerable.Range(0, 80).Select(i => $"\t\tprint({i})\n"));
        var editor = await OpenAsync(f, "greeter.py", "class Greeter:\n\tdef greet(self, name):\n" + body + "\t\treturn name\n");
        var view = editor.Editor.TextArea.TextView;
        var sticky = editor.Editor.FindAncestorOfType<CodeEditorView>()!.StickyScroll;

        // The grammar colors the keywords of the two header lines while they're in view.
        Color? ColorAt(int line, int column)
        {
            view.EnsureVisualLines();
            return view.GetVisualLine(line)?.Elements.FirstOrDefault(e => e.RelativeTextOffset <= column && column < e.RelativeTextOffset + e.DocumentLength)
                ?.TextRunProperties.ForegroundBrush is ISolidColorBrush brush ? brush.Color : null;
        }

        Assert.Equal("source.python", editor.GrammarScope);
        var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(editor.Editor.Foreground).Color;
        await ShellFixture.PumpUntil(() => ColorAt(1, 0) is { } color && color != foreground, seconds: 20);
        var classColor = ColorAt(1, 0);
        var defColor = ColorAt(2, 1);
        var nameColor = ColorAt(1, 6);

        // Scrolled into the method: both headers pinned.
        await ShellFixture.PumpUntil(() => editor.Folding.AllFoldings.Any(), seconds: 20);
        await ShellFixture.PumpUntil(
            () =>
            {
                editor.Editor.ScrollToLine(50); // again until the view has laid out
                return sticky.Shown.SequenceEqual([1, 2]);
            },
            seconds: 20);
        ShellFixture.Pump();

        // On screen: each run in the color the editor gives it, the tab as the text view lays it out, in the text column,
        // with the line numbers.
        var blocks = sticky.GetVisualDescendants().OfType<TextBlock>().ToList();
        var code = blocks.Where(b => b.Inlines is { Count: > 0 }).Select(b => b.Inlines!.OfType<Avalonia.Controls.Documents.Run>().ToList()).ToList();
        Assert.Equal(2, code.Count);
        Color? Of(Avalonia.Controls.Documents.Run run) => (run.Foreground as ISolidColorBrush)?.Color;
        Assert.Equal(["class", " ", "Greeter"], code[0].Take(3).Select(r => r.Text));
        Assert.NotEqual(foreground, classColor);
        Assert.Equal(classColor, Of(code[0][0]));
        Assert.Equal(nameColor, Of(code[0][2]));
        Assert.Equal(defColor, Of(code[1].First(r => r.Text == "def")));
        Assert.StartsWith(new string(' ', editor.Editor.Options.IndentationSize) + "def", string.Concat(code[1].Select(r => r.Text)), StringComparison.Ordinal);
        var first = blocks.First(b => b.Inlines is { Count: > 0 });
        Assert.Equal(editor.Editor.FontFamily, first.FontFamily);
        var textLeft = view.TranslatePoint(default, editor.Editor.TextArea)!.Value.X;
        Assert.Equal(textLeft, first.TranslatePoint(default, sticky)!.Value.X, 0.5);
        Assert.Contains(blocks, b => b.Text == "1" && b.IsEffectivelyVisible);
        Assert.Contains(blocks, b => b.Text == "2" && b.IsEffectivelyVisible);
        if (Environment.GetEnvironmentVariable("CODALUS_UI_SNAPSHOTS") is { Length: > 0 } snapshots)
        {
            Directory.CreateDirectory(snapshots);
            using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(f.Window);
            frame!.Save(Path.Combine(snapshots, "sticky-scroll.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        // Clicking a pinned line goes to it.
        var row = sticky.GetVisualDescendants().OfType<Border>().First(b => b.Cursor is not null);
        row.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(row, new Avalonia.Input.Pointer(1, Avalonia.Input.PointerType.Mouse, true), row, default, 0,
            new Avalonia.Input.PointerPointProperties(Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.LeftButtonPressed), Avalonia.Input.KeyModifiers.None));
        Assert.Equal(1, editor.Editor.TextArea.Caret.Line);
    }

    [AvaloniaFact]
    public async Task Sticky_scroll_stays_under_the_quick_pick_and_the_find_widget()
    {
        // The maintainer's report: the pinned lines drew over the branch picker (a quick pick), hiding "Create New
        // Branch" and the first branches. They were in the window's adorner layer, above all of its content.
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;
        var body = string.Concat(Enumerable.Range(0, 80).Select(i => $"\t\tprint({i})\n"));
        var editor = await OpenAsync(f, "greeter.py", "class Greeter:\n\tdef greet(self, name):\n" + body + "\t\treturn name\n");
        var sticky = editor.Editor.FindAncestorOfType<CodeEditorView>()!.StickyScroll;
        await ShellFixture.PumpUntil(() => editor.Folding.AllFoldings.Any(), seconds: 20);
        await ShellFixture.PumpUntil(
            () =>
            {
                editor.Editor.ScrollToLine(50);
                return sticky.Shown.SequenceEqual([1, 2]);
            },
            seconds: 20);

        // Hit testing reads the last rendered frame: each check below comes after a frame with what it checks.
        ShellFixture.RenderFrame(f.Window);
        Rect BoundsInWindow(Visual visual) => new(visual.TranslatePoint(default, f.Window)!.Value, visual.Bounds.Size);
        Visual? HitAt(Point point) => f.Window.InputHitTest(point) as Visual;
        bool Within<T>(Visual? hit)
            where T : Visual => hit is T || hit?.FindAncestorOfType<T>() is not null;

        // A failure names the visual hit with its ancestors: a type name alone (a DockPanel) is in several templates.
        string Describe(Visual? hit) => hit is null ? "nothing" : string.Join(" < ", hit.GetSelfAndVisualAncestors().Take(14).Select(v => v is Control { Name: { Length: > 0 } name } ? $"{v.GetType().Name}#{name}" : v.GetType().Name));

        // The band is the editor's: in its text area, over the code and over the margins beside it. The points come from
        // the layout: the text view's left edge (the margins' widths vary), and a margin's middle.
        Assert.Same(editor.Editor.TextArea, sticky.GetVisualParent());
        var band = BoundsInWindow(sticky);
        Assert.True(band.Height > 0);
        var text = BoundsInWindow(editor.Editor.TextArea.TextView);
        var overCode = new Point(text.X + 20, band.Center.Y);
        Assert.True(band.Contains(overCode) && text.Contains(overCode), $"the band {band} doesn't cover the text view {text}");
        Assert.True(Within<StickyScroll>(HitAt(overCode)), $"over the code at {overCode} (band {band}, text view {text}): {Describe(HitAt(overCode))}");
        var margin = BoundsInWindow(editor.BreakpointMargin);
        var overMargin = new Point(margin.Center.X, band.Center.Y);
        Assert.True(band.Contains(overMargin), $"the band {band} doesn't cover the breakpoint margin {margin}");
        Assert.True(Within<StickyScroll>(HitAt(overMargin)), $"over the breakpoint margin at {overMargin} (band {band}, margin {margin}): {Describe(HitAt(overMargin))}");

        // The find widget, opened over the band, takes the pointer where they overlap.
        editor.Search.Open();
        ShellFixture.RenderFrame(f.Window);
        var widget = BoundsInWindow(editor.Search.GetVisualDescendants().OfType<Border>().First(b => b.IsEffectivelyVisible && b.Bounds.Height > 0)).Intersect(band);
        Assert.True(widget.Width > 0 && widget.Height > 0, $"the find widget {editor.Search.Bounds} doesn't overlap the band {band}");
        Assert.True(Within<AvaloniaEdit.Search.SearchPanel>(HitAt(widget.Center)), $"the find widget at {widget.Center}: {Describe(HitAt(widget.Center))}");
        editor.Search.Close();
        ShellFixture.Pump();

        // A quick pick (the branch picker, the command palette) over the band: its rows take the pointer, not the band.
        var picking = f.Shell.Palette.ShowAsync([.. Enumerable.Range(0, 12).Select(i => new ViewModels.QuickPickItem($"branch-{i}"))], "Switch to a branch");
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<Views.QuickPickView>().Any(v => v.IsEffectivelyVisible && v.Bounds.Height > 0));
        ShellFixture.RenderFrame(f.Window);
        var picker = f.Window.GetVisualDescendants().OfType<Views.QuickPickView>().First(v => v.IsEffectivelyVisible);
        var overlap = BoundsInWindow(picker).Intersect(band);
        Assert.True(overlap.Width > 0 && overlap.Height > 0, $"the quick pick {BoundsInWindow(picker)} doesn't overlap the band {band}");
        var hit = HitAt(overlap.Center);
        Assert.True(Within<Views.QuickPickView>(hit) && !Within<StickyScroll>(hit), $"the quick pick at {overlap.Center}: {Describe(hit)}");
        var firstRow = picker.GetVisualDescendants().OfType<ListBoxItem>().First();
        var rowPoint = BoundsInWindow(firstRow).Center;
        Assert.True(Within<ListBoxItem>(HitAt(rowPoint)), $"the first row at {rowPoint}: {Describe(HitAt(rowPoint))}");
        f.Shell.Palette.Cancel();
        Assert.Null(await picking);
    }

    [AvaloniaFact]
    public void Semantic_tokens_of_pinned_lines_outlive_the_window_around_the_viewport()
    {
        // Sticky scroll's headers (a namespace, a class) can be far above the window of tokens kept around the viewport.
        var colorizer = new SemanticColorizer();
        colorizer.Apply(0, 60, [0, 0, 9, 3, 0]);
        colorizer.Pin([0]);
        colorizer.Apply(440, 560, [500, 4, 5, 2, 0]);
        Assert.Equal(3, colorizer.TokenAt(0, 2));
        Assert.Equal(2, colorizer.TokenAt(500, 5));
        Assert.True(colorizer.Knows(0));
        Assert.True(colorizer.Knows(450)); // in the window, without tokens
        Assert.False(colorizer.Knows(1));

        // A header's own range request.
        colorizer.ApplyLine(1, [1, 0, 6, 7, 0]);
        Assert.Equal(7, colorizer.TokenAt(1, 0));

        // Unpinned, they go with the next window.
        colorizer.Pin([]);
        colorizer.Apply(440, 560, [500, 4, 5, 2, 0]);
        Assert.Equal(-1, colorizer.TokenAt(0, 2));
        Assert.False(colorizer.Knows(1));
    }

    /// <summary>A plugins folder with the sample declarative plugin (samples/plugins/TodoLanguage).</summary>
    private static string TodoPluginFolder()
    {
        var output = Path.Combine(ShellFixture.RepoRoot, "samples", "plugins", "TodoLanguage", "bin", "Debug", "net10.0");
        if (!File.Exists(Path.Combine(output, "TodoLanguage.dll")))
        {
            output = Path.Combine(ShellFixture.RepoRoot, "samples", "plugins", "TodoLanguage", "bin", "Release", "net10.0");
        }

        Assert.True(File.Exists(Path.Combine(output, "TodoLanguage.dll")), "The TodoLanguage sample is not built.");
        var plugins = Path.Combine(Path.GetTempPath(), "codalus-todo-plugin-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(plugins, "codalus-samples.todo");
        foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(output, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        return plugins;
    }

    [AvaloniaFact]
    public async Task A_declarative_plugin_adds_a_language_with_a_grammar_and_a_language_server()
    {
        var plugins = TodoPluginFolder();
        Assert.True(File.Exists(Path.Combine(plugins, "codalus-samples.todo", "codalus-plugin.json")), string.Join("\n", Directory.EnumerateFileSystemEntries(plugins, "*", SearchOption.AllDirectories)));
        await using var f = ShellFixture.Create(builtInPlugins: plugins);
        f.Window.Width = 1200;
        f.Window.Height = 800;
        await ShellFixture.PumpUntil(() => f.Shell.Plugins.Plugins.Count > 0, seconds: 30);
        Assert.True(f.Shell.Plugins.Plugins.Any(p => p.Id == "codalus-samples.todo"), string.Join(", ", f.Shell.Plugins.Plugins.Select(p => $"{p.Id} {p.State} {p.Error} {p.InstallPath}")));
        var plugin = f.Shell.Plugins.Plugins.Single(p => p.Id == "codalus-samples.todo");
        Assert.True(plugin.IsUsable, $"{plugin.State}: {plugin.Error}");
        Assert.True(f.Shell.Plugins.Plugins.Single(p => p.Id == "codalus-samples.todo").IsDeclarative);

        var todo = await OpenAsync(f, "work.todo", "Week:\n- [ ] Ship M11 @today !high due:2026-10-12\n- [?] Unsure\n- [x] Done thing\n-[ ] Messy due:2026-13-40\n");
        Assert.Equal("todo", todo.Model.LanguageId);
        Assert.Equal("source.todo", todo.GrammarScope);
        await ShellFixture.PumpUntil(() => Colors(todo, 2).Count >= 3, seconds: 20);

        // The plugin's server: diagnostics, completion after @, hover and formatting.
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Diagnostics.For(todo.Model.Path).Any(d => d.Code == "todo.checkbox"), seconds: 60);
        Assert.Contains(f.Shell.Languages.Diagnostics.For(todo.Model.Path), d => d.Code == "todo.date" && d.Range.Start.Line == 4);

        todo.Editor.CaretOffset = todo.Model.Text.GetLineByNumber(2).EndOffset;
        todo.Editor.TextArea.Focus();
        f.Type(" ");
        f.Type("@"); // the server's trigger character
        await ShellFixture.PumpUntil(() => todo.CompletionWindow is not null, seconds: 30);
        Assert.Contains(todo.CompletionWindow!.CompletionList.CompletionData, d => d.Text == "later");
        f.Press(Avalonia.Input.Key.Escape);

        await f.RunAsync("editor.action.formatDocument");
        await ShellFixture.PumpUntil(() => todo.Model.Text.Text.Contains("\n- [ ] Messy", StringComparison.Ordinal), seconds: 30);

        // The language configuration: '#' comments.
        todo.Editor.Select(0, 0);
        await f.RunAsync("editor.action.commentLine");
        Assert.StartsWith("# Week:", todo.Model.Text.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Json_and_markdown_files_get_the_markup_service_and_a_live_preview()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;

        // global.json: the markup host validates it against the bundled schema.
        var global = await OpenAsync(f, "global.json", "{\n  \"sdk\": { \"rollForward\": \"sideways\" }\n}\n");
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Diagnostics.For(global.Model.Path).Any(d => d.Code == "json.schema"), seconds: 60);

        // Markdown: Open Preview to the Side follows edits.
        var readme = await OpenAsync(f, "README.md", "# Title\n\nSome *text*.\n");
        await f.RunAsync("markdown.showPreviewToSide");
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<Views.MarkdownPreviewView>().Any());
        var preview = f.Window.GetVisualDescendants().OfType<Views.MarkdownPreviewView>().Single();
        Assert.StartsWith("# Title", preview.Rendered, StringComparison.Ordinal);
        readme.Model.Text.Insert(readme.Model.Text.TextLength, "\n## Added\n");
        await ShellFixture.PumpUntil(() => preview.Rendered.Contains("## Added", StringComparison.Ordinal), seconds: 10);
    }

    [AvaloniaFact]
    public async Task Language_configurations_drive_comments_brackets_folding_and_word_completion()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;

        // Line comments (YAML's #), toggled on the selected lines.
        var yaml = await OpenAsync(f, "ci.yml", "jobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: dotnet build\n");
        yaml.Editor.Select(0, yaml.Model.Text.GetLineByNumber(2).EndOffset);
        await f.RunAsync("editor.action.commentLine");
        Assert.StartsWith("# jobs:\n#   build:\n", yaml.Model.Text.Text, StringComparison.Ordinal); // aligned at the smallest indentation
        await f.RunAsync("editor.action.commentLine");
        Assert.StartsWith("jobs:\n  build:\n", yaml.Model.Text.Text, StringComparison.Ordinal);

        // Folding without a language server: indentation for YAML.
        await ShellFixture.PumpUntil(() => yaml.Folding.AllFoldings.Count() >= 3, seconds: 10);

        // Word completion from the document's words.
        yaml.Editor.CaretOffset = yaml.Model.Text.TextLength;
        yaml.Editor.TextArea.Focus();
        f.Type("  - run: ");
        foreach (var letter in "ubu")
        {
            f.Type(letter.ToString()); // keystrokes, as typed
        }

        await ShellFixture.PumpUntil(() => yaml.CompletionWindow is not null, seconds: 10);
        Assert.Contains(yaml.CompletionWindow!.CompletionList.CompletionData, d => d.Text == "ubuntu-latest");
        f.Press(Avalonia.Input.Key.Escape);

        // XML: block comments, element folding, auto-closing quotes and typing over them.
        var xml = await OpenAsync(f, "Sample.props", "<Project>\n  <PropertyGroup>\n    <Nullable>enable</Nullable>\n  </PropertyGroup>\n</Project>\n");
        await ShellFixture.PumpUntil(() => xml.Folding.AllFoldings.Count() >= 2, seconds: 10);
        xml.Editor.Select(xml.Model.Text.GetLineByNumber(3).Offset, 0);
        await f.RunAsync("editor.action.commentLine");
        Assert.Contains("<!-- <Nullable>enable</Nullable> -->", xml.Model.Text.Text, StringComparison.Ordinal);

        var json = await OpenAsync(f, "data.json", "\n");
        json.Editor.CaretOffset = 0;
        json.Editor.TextArea.Focus();
        f.Type("{");
        Assert.StartsWith("{}", json.Model.Text.Text, StringComparison.Ordinal);
        f.Type("\"");
        f.Type("a");
        Assert.StartsWith("{\"a\"}", json.Model.Text.Text, StringComparison.Ordinal);
        f.Type("\""); // types over the closing quote
        f.Type(": 1");
        f.Type("}"); // and over the closing brace
        Assert.StartsWith("{\"a\": 1}\n", json.Model.Text.Text, StringComparison.Ordinal);
    }
}
