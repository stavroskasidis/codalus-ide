using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Git;

namespace Codalus.Shell.Tests;

/// <summary>
/// The diff document (#86, D45): its sides in the editor's colors, from the real texts; on a git repository made in a
/// temporary folder.
/// </summary>
public sealed class DiffDocumentTests
{
    private static readonly int s_keyword = TokenTypes.IndexOf("keyword");
    private static readonly int s_comment = TokenTypes.IndexOf("comment");

    private const string Original = """
        namespace Greetings;

        public static class Greeter
        {
            // Says hello.
            public static string Greet(string name) => $"Hello from {name}";

            public static string Wave() => "o/";
        }

        """;

    internal static string Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
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

    /// <summary>A repository with Greeter.cs committed, open in the shell's Git service (no solution: nothing to build).</summary>
    internal static async Task<(ShellFixture Fixture, string Folder, string File)> RepositoryAsync(string text = Original)
    {
        var f = ShellFixture.Create();
        var folder = Path.Combine(f.UserData, "repo");
        Directory.CreateDirectory(folder);
        Git(folder, "init", "-q", "-b", "main");
        Git(folder, "config", "user.name", "Ada Tester");
        Git(folder, "config", "user.email", "ada@example.com");
        Git(folder, "config", "commit.gpgsign", "false");
        Git(folder, "config", "core.autocrlf", "false");
        var file = Path.Combine(folder, "Greeter.cs");
        await File.WriteAllTextAsync(file, text);
        Git(folder, "add", ".");
        Git(folder, "commit", "-q", "-m", "initial");
        Assert.True(await f.Shell.Git.OpenAsync(folder, CancellationToken.None));
        return (f, folder, file);
    }

    private static int LineOf(string text, string contains) =>
        Array.FindIndex(text.Split('\n'), l => l.Contains(contains, StringComparison.Ordinal)) + 1;

    private static int ColumnOf(string text, int line, string word) =>
        text.Split('\n')[line - 1].IndexOf(word, StringComparison.Ordinal);

    [AvaloniaFact]
    public async Task Both_sides_and_the_inline_view_are_colored_by_the_grammar()
    {
        var (f, _, file) = await RepositoryAsync();
        await using var fixture = f;
        await File.WriteAllTextAsync(file, Original.Replace("    public static string Wave", "    public static int Count => 3;\n\n    public static string Wave", StringComparison.Ordinal).Replace("// Says hello.", "// Says hello to someone.", StringComparison.Ordinal));
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);

        var document = await f.Shell.OpenDiffAsync(file, staged: false);
        Assert.NotNull(document);
        var vm = document.ViewModel;
        await ShellFixture.PumpUntil(() => vm is { OldColors: not null, NewColors: not null } && f.Window.GetVisualDescendants().OfType<DiffView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<DiffView>().Single();

        // The index side (a git blob) and the working tree, each by the C# grammar: keywords and comments in the editor's colors.
        var left = view.LeftText;
        var right = view.RightText;
        var classLine = LineOf(left, "public static class");
        Assert.Equal(s_keyword, view.ColoredTypeAt(DiffPaneSide.Old, classLine, ColumnOf(left, classLine, "class")));
        Assert.Equal(s_comment, view.ColoredTypeAt(DiffPaneSide.Old, LineOf(left, "// Says hello."), 6));
        var added = LineOf(right, "Count => 3");
        Assert.Equal(s_keyword, view.ColoredTypeAt(DiffPaneSide.New, added, ColumnOf(right, added, "int")));
        Assert.Equal(s_comment, view.ColoredTypeAt(DiffPaneSide.New, LineOf(right, "to someone"), 6));
        Assert.True(view.LineColors(DiffPaneSide.Old, classLine) >= 2, "the index side's class line is drawn in one color");
        Assert.True(view.LineColors(DiffPaneSide.New, added) >= 2, "the working tree's added line is drawn in one color");

        // A filler row opposite the added line stays plain.
        Assert.Equal(-1, view.ColoredTypeAt(DiffPaneSide.Old, added, 0));

        // Inline: the removed line from the old text, the added one from the new.
        vm.SideBySide = false;
        await ShellFixture.PumpUntil(() => view.InlineText.Contains("to someone", StringComparison.Ordinal));
        var inline = view.InlineText;
        Assert.Equal(s_comment, view.ColoredTypeAt(DiffPaneSide.Inline, LineOf(inline, "// Says hello."), 6));
        var inlineAdded = LineOf(inline, "Count => 3");
        Assert.Equal(s_keyword, view.ColoredTypeAt(DiffPaneSide.Inline, inlineAdded, ColumnOf(inline, inlineAdded, "int")));
    }

    [AvaloniaFact]
    public async Task Colors_come_from_each_files_own_text_never_from_the_composed_pane()
    {
        await using var f = ShellFixture.Create();

        // The removed lines open a block comment that the old file never closes. The inline view shows them just above
        // the added line; colored from the pane's text, the added line would be part of the comment.
        var vm = new DiffViewModel(
            Path.Combine(f.UserData, "Sample.cs"), "test", "old", () => Task.FromResult("int a;\n/* the rest\nof the file\n"), "new", () => Task.FromResult("int a;\nint b;\n"))
        {
            SideBySide = false,
        };
        var document = new DiffDocument(vm, f.Shell.EditorFont, f.Shell.DiffServices);
        await vm.LoadAsync();
        f.Shell.Layout.OpenDocument(document, focus: true);
        await ShellFixture.PumpUntil(() => vm is { OldColors: not null, NewColors: not null } && f.Window.GetVisualDescendants().OfType<DiffView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<DiffView>().Single();

        var inline = view.InlineText;
        var added = LineOf(inline, "int b;");
        Assert.True(added > LineOf(inline, "of the file"), inline);
        Assert.Equal(s_keyword, view.ColoredTypeAt(DiffPaneSide.Inline, added, 0));
        Assert.Equal(s_comment, view.ColoredTypeAt(DiffPaneSide.Inline, LineOf(inline, "of the file"), 0));
    }

    [AvaloniaFact]
    public async Task The_side_that_is_the_open_document_takes_the_language_services_colors()
    {
        await using var f = ShellFixture.Create();
        var path = Path.Combine(f.UserData, "Sample.cs");
        const string NewText = "class A : IThing { }\n";
        var interfaceType = TokenTypes.IndexOf("interface");

        // The language service's tokens (as the C# host sends them) for the open document's text only.
        var services = new DiffServices(f.Shell)
        {
            LanguageOf = _ => "csharp",
            SemanticTokensAsync = (file, text, _) => Task.FromResult(file == path && text == NewText ? new[] { 0, 10, 6, interfaceType, 0 } : null),
        };
        var vm = new DiffViewModel(path, "test", "old", () => Task.FromResult("class A : IThing { int x; }\n"), "new", () => Task.FromResult(NewText)) { Services = services };
        await vm.LoadAsync();
        await ShellFixture.PumpUntil(() => vm.OpenSideIsOld is false);

        Assert.Equal(interfaceType, vm.NewColors!.TypeAt(0, 10)); // IThing, an interface, as the editor colors it
        Assert.Equal(TokenTypes.IndexOf("class"), vm.OldColors!.TypeAt(0, 10)); // the grammar's guess: a type name
    }

    [AvaloniaFact]
    public async Task The_merge_editors_panes_are_colored_by_the_grammar()
    {
        await using var f = ShellFixture.Create();
        string Write(string name, string text)
        {
            var path = Path.Combine(f.UserData, name);
            File.WriteAllText(path, text);
            return path;
        }

        var local = Write("LOCAL.cs", "class A\n{\n    int Mine;\n}\n");
        var remote = Write("REMOTE.cs", "class A\n{\n    string Theirs;\n}\n");
        var @base = Write("BASE.cs", "class A\n{\n    bool Base;\n}\n");
        var merged = Write("Merged.cs", "class A\n{\n<<<<<<< LOCAL\n    int Mine;\n=======\n    string Theirs;\n>>>>>>> REMOTE\n}\n");

        _ = f.Shell.MergeFilesAsync(local, remote, @base, merged);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<MergeEditorView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<MergeEditorView>().Single();
        await ShellFixture.PumpUntil(() => view.ColoredTypeAt(MergePane.Current, 1, 0) >= 0 && view.ColoredTypeAt(MergePane.Result, 1, 0) >= 0);

        Assert.Equal(s_keyword, view.ColoredTypeAt(MergePane.Current, 1, 0)); // class
        Assert.Equal(s_keyword, view.ColoredTypeAt(MergePane.Incoming, 3, 4)); // string
        Assert.Equal(s_keyword, view.ColoredTypeAt(MergePane.Result, 1, 0));

        // The result is colored again after an edit.
        view.Result.Document.Insert(0, "// note\n");
        await ShellFixture.PumpUntil(() => view.ColoredTypeAt(MergePane.Result, 1, 4) == s_comment);
        Assert.Equal(s_keyword, view.ColoredTypeAt(MergePane.Result, 2, 0));
    }
}
