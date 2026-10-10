using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using DaedalusStudio.Shell.Editor;

namespace DaedalusStudio.Shell.Tests;

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

    /// <summary>A plugins folder with the sample declarative plugin (samples/plugins/TodoLanguage).</summary>
    private static string TodoPluginFolder()
    {
        var output = Path.Combine(ShellFixture.RepoRoot, "samples", "plugins", "TodoLanguage", "bin", "Debug", "net10.0");
        if (!File.Exists(Path.Combine(output, "TodoLanguage.dll")))
        {
            output = Path.Combine(ShellFixture.RepoRoot, "samples", "plugins", "TodoLanguage", "bin", "Release", "net10.0");
        }

        Assert.True(File.Exists(Path.Combine(output, "TodoLanguage.dll")), "The TodoLanguage sample is not built.");
        var plugins = Path.Combine(Path.GetTempPath(), "daedalus-todo-plugin-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(plugins, "daedalus-samples.todo");
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
        Assert.True(File.Exists(Path.Combine(plugins, "daedalus-samples.todo", "daedalus-plugin.json")), string.Join("\n", Directory.EnumerateFileSystemEntries(plugins, "*", SearchOption.AllDirectories)));
        await using var f = ShellFixture.Create(builtInPlugins: plugins);
        f.Window.Width = 1200;
        f.Window.Height = 800;
        await ShellFixture.PumpUntil(() => f.Shell.Plugins.Plugins.Count > 0, seconds: 30);
        Assert.True(f.Shell.Plugins.Plugins.Any(p => p.Id == "daedalus-samples.todo"), string.Join(", ", f.Shell.Plugins.Plugins.Select(p => $"{p.Id} {p.State} {p.Error} {p.InstallPath}")));
        var plugin = f.Shell.Plugins.Plugins.Single(p => p.Id == "daedalus-samples.todo");
        Assert.True(plugin.IsUsable, $"{plugin.State}: {plugin.Error}");
        Assert.True(f.Shell.Plugins.Plugins.Single(p => p.Id == "daedalus-samples.todo").IsDeclarative);

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

        f.Shell.Run("editor.action.formatDocument");
        await ShellFixture.PumpUntil(() => todo.Model.Text.Text.Contains("\n- [ ] Messy", StringComparison.Ordinal), seconds: 30);

        // The language configuration: '#' comments.
        todo.Editor.Select(0, 0);
        f.Shell.Run("editor.action.commentLine");
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
        f.Shell.Run("markdown.showPreviewToSide");
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
        f.Shell.Run("editor.action.commentLine");
        Assert.StartsWith("# jobs:\n#   build:\n", yaml.Model.Text.Text, StringComparison.Ordinal); // aligned at the smallest indentation
        f.Shell.Run("editor.action.commentLine");
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
        f.Shell.Run("editor.action.commentLine");
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
