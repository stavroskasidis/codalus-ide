using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Platform;
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

    [Fact]
    public void The_diff_settings_default_to_side_by_side()
    {
        var layout = Assert.Single(BuiltIns.GitSettings(), d => d.Key == BuiltIns.DiffLayoutSetting);
        Assert.Equal(DiffViewModel.LayoutSideBySide, layout.Default.GetString());
        Assert.Equal([DiffViewModel.LayoutSideBySide, DiffViewModel.LayoutInline], layout.Enum);
        Assert.Equal(Workbench.Settings.SettingScope.User, layout.Scope);
    }

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
            OpenText = (file, text) => file == path && text == NewText,
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

    [AvaloniaFact]
    public async Task Changed_lines_their_words_and_the_filler_are_drawn_in_the_themes_diff_colors()
    {
        var (f, _, file) = await RepositoryAsync();
        await using var fixture = f;
        await File.WriteAllTextAsync(file, Original.Replace("string Greet(", "string Hail(", StringComparison.Ordinal).Replace("    public static string Wave() => \"o/\";\n", string.Empty, StringComparison.Ordinal));
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        var document = await f.Shell.OpenDiffAsync(file, staged: false);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<DiffView>().Any() && document!.ViewModel.NewColors is not null);
        var view = f.Window.GetVisualDescendants().OfType<DiffView>().Single();

        // The changed word of the paired lines, whole: "Greet" and "Hail".
        var left = view.LeftText;
        var right = view.RightText;
        var changed = LineOf(right, "Hail(");
        Assert.Equal(changed, LineOf(left, "Greet("));
        Assert.Equal([(ColumnOf(left, changed, "Greet"), 5)], view.WordsAt(DiffPaneSide.Old, changed));
        Assert.Equal([(ColumnOf(right, changed, "Hail"), 4)], view.WordsAt(DiffPaneSide.New, changed));
        Assert.Empty(view.WordsAt(DiffPaneSide.New, 1));

        // Drawn: the line's fill past its end, the word's fill and border, and the hatching opposite the removed line.
        for (var i = 0; i < 5; i++)
        {
            ShellFixture.Pump();
        }

        using var frame = f.Window.CaptureRenderedFrame()!;
        var variant = f.Window.ActualThemeVariant;
        var editor = view.EditorOf(DiffPaneSide.New).TextArea.TextView;
        var removedRow = LineOf(left, "Wave()");
        Assert.True(removedRow > 0, left);
        Assert.Equal(Token("Shell.Diff.Added.Line", variant), Pixel(frame, editor, changed, editor.Bounds.Width - 4));
        Assert.Equal(Token("Shell.Diff.Removed.Line", variant), Pixel(frame, view.EditorOf(DiffPaneSide.Old).TextArea.TextView, removedRow, editor.Bounds.Width - 4));
        var row = Row(frame, editor, changed, 0, editor.Bounds.Width - 1);
        Assert.Contains(Token("Shell.Diff.Added.Word", variant), row);
        Assert.Contains(Token("Shell.Diff.Added.WordBorder", variant), row);
        Assert.Contains(Token("Shell.Diff.Filler", variant), Row(frame, editor, removedRow, 0, editor.Bounds.Width - 1));
    }

    [AvaloniaFact]
    public async Task Each_side_numbers_its_own_lines_and_has_its_own_status_bar_and_header()
    {
        var (f, folder, file) = await RepositoryAsync();
        await using var fixture = f;

        // The working tree with a byte order mark and CRLF line endings, and a line added.
        var text = Original.Replace("    // Says hello.\n", "    // Says hello.\n    // Twice.\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        await File.WriteAllBytesAsync(file, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(text)]);
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        var document = await f.Shell.OpenDiffAsync(file, staged: false);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<DiffView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<DiffView>().Single();

        Assert.Equal("Greeter.cs (Working Tree)", document!.Title);
        Assert.Equal("compare", document.TabIcon);
        Assert.Equal("Greeter.cs (Index)", view.HeaderOf(DiffPaneSide.Old));
        Assert.Equal("Greeter.cs (Working tree)", view.HeaderOf(DiffPaneSide.New));
        Assert.Equal(["SPC", "LF", "UTF-8"], view.StatusOf(DiffPaneSide.Old).Skip(1));
        Assert.Equal(["SPC", "CRLF", "UTF-8 with BOM"], view.StatusOf(DiffPaneSide.New).Skip(1));

        // The added line has a filler opposite: the old side's numbers skip it, so its next line keeps its own number.
        var added = LineOf(view.RightText, "// Twice.");
        Assert.Equal((0, 0), view.NumbersAt(DiffPaneSide.Old, added));
        Assert.Equal((0, added), view.NumbersAt(DiffPaneSide.New, added));
        Assert.Equal((added, 0), view.NumbersAt(DiffPaneSide.Old, added + 1));
        Assert.Equal((0, added + 1), view.NumbersAt(DiffPaneSide.New, added + 1));

        // The caret's line in each status bar: the file's line, not the pane's.
        view.EditorOf(DiffPaneSide.Old).TextArea.Caret.Line = added + 1;
        view.EditorOf(DiffPaneSide.Old).TextArea.Caret.Column = 3;
        Assert.Equal($"Ln: {added}   Ch: 3", view.StatusOf(DiffPaneSide.Old)[0]);
        Assert.Equal("1 change", view.CountText);
        Assert.Equal(("−0", "+1"), view.LineCounts);

        // Staged: HEAD against the index.
        Git(folder, "add", ".");
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        var staged = await f.Shell.OpenDiffAsync(file, staged: true);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<DiffView>().Any(v => v.ViewModel == staged!.ViewModel));
        var stagedView = f.Window.GetVisualDescendants().OfType<DiffView>().Single(v => v.ViewModel == staged!.ViewModel);
        Assert.Equal("Greeter.cs (Staged)", staged!.Title);
        Assert.Equal("Greeter.cs (HEAD)", stagedView.HeaderOf(DiffPaneSide.Old));
        Assert.Equal("Greeter.cs (Index)", stagedView.HeaderOf(DiffPaneSide.New));

        // A revision comparison's title reads in its direction.
        var revisions = new DiffDocument(new DiffViewModel(file, "k", "3f2a1c9", () => Task.FromResult("a\n"), "8b7e4d2", () => Task.FromResult("b\n")), f.Shell.EditorFont);
        Assert.Equal("Greeter.cs (3f2a1c9 → 8b7e4d2)", revisions.Title);
    }

    [AvaloniaFact]
    public async Task The_layout_is_a_setting_F8_moves_between_changes_and_the_view_keeps_its_place()
    {
        var (f, _, file) = await RepositoryAsync();
        await using var fixture = f;
        await File.WriteAllTextAsync(file, Original.Replace("namespace Greetings;", "namespace Greetings.Core;", StringComparison.Ordinal).Replace("\"o/\"", "\"\\o/\"", StringComparison.Ordinal));
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        var document = await f.Shell.OpenDiffAsync(file, staged: false);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<DiffView>().Any());
        var view = f.Window.GetVisualDescendants().OfType<DiffView>().Single();
        Assert.True(document!.ViewModel.SideBySide);

        // F8 and Shift+F8 (the commands their keys run while the diff has the focus).
        Assert.Equal(2, document.ViewModel.Changes.Count);
        Assert.Equal("Change 1 of 2", view.CountText); // the first change is shown when the diff opens
        await f.RunAsync("git.diff.nextChange");
        Assert.Equal("Change 2 of 2", view.CountText);
        await f.RunAsync("git.diff.previousChange");
        Assert.Equal("Change 1 of 2", view.CountText);
        Assert.Contains(f.Shell.Workbench.Keybindings.EffectiveRules, r => r.Command == "git.diff.nextChange" && r.When.Text == "focusedView == diff");

        // The splitter: dragged, then double-clicked for half and half.
        document.ViewModel.SplitRatio = 0.3;
        view.ResetSplit();
        Assert.Equal(0.5, document.ViewModel.SplitRatio);

        // Inline from the gear menu: saved in git.diff.layout, so the next diff opens inline.
        document.ViewModel.SideBySide = false;
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Settings.GetString(BuiltIns.DiffLayoutSetting, "") == DiffViewModel.LayoutInline);
        f.Shell.Layout.CloseDocument(document);
        var again = await f.Shell.OpenDiffAsync(file, staged: false);
        Assert.False(again!.ViewModel.SideBySide);
        await f.RunAsync("git.diff.sideBySide");
        Assert.True(again.ViewModel.SideBySide);
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Settings.GetString(BuiltIns.DiffLayoutSetting, "") == DiffViewModel.LayoutSideBySide);

        // A tab switch keeps the caret where it was.
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<DiffView>().Any(v => v.ViewModel == again.ViewModel));
        view = f.Window.GetVisualDescendants().OfType<DiffView>().Single(v => v.ViewModel == again.ViewModel);
        await f.RunAsync("git.diff.nextChange");
        var row = view.CurrentRow;
        await f.Shell.OpenFileAsync(file);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor is not null);
        f.Shell.Layout.OpenDocument(again, focus: true);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<DiffView>().Any(v => v.ViewModel == again.ViewModel));
        var rebuilt = f.Window.GetVisualDescendants().OfType<DiffView>().Single(v => v.ViewModel == again.ViewModel);
        await ShellFixture.PumpUntil(() => rebuilt.CurrentRow == row);
    }

    [AvaloniaFact]
    public async Task The_navigation_bar_row_is_on_the_open_documents_side_with_an_empty_row_opposite()
    {
        var (f, _, file) = await RepositoryAsync();
        await using var fixture = f;
        await File.WriteAllTextAsync(file, Original.Replace("Hello from", "Hi from", StringComparison.Ordinal));
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);

        // The file isn't open: neither side is the open document, and neither has the row.
        var document = await f.Shell.OpenDiffAsync(file, staged: false);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<DiffView>().Any() && document!.ViewModel.NewColors is not null);
        var view = f.Window.GetVisualDescendants().OfType<DiffView>().Single();
        Assert.Null(document!.ViewModel.OpenSideIsOld);
        Assert.False(view.NavigationOf(DiffPaneSide.Old).Row);
        Assert.False(view.NavigationOf(DiffPaneSide.New).Row);

        // Open in the editor: the working tree is its text, so both sides get the row and only that side the bar's place.
        await f.Shell.OpenFileAsync(file);
        f.Shell.Layout.OpenDocument(document, focus: true);
        await document.ViewModel.ColorAsync();
        await ShellFixture.PumpUntil(() => document.ViewModel.OpenSideIsOld == false);
        view = f.Window.GetVisualDescendants().OfType<DiffView>().Single(v => v.ViewModel == document.ViewModel);
        await ShellFixture.PumpUntil(() => view.NavigationOf(DiffPaneSide.New).Row && view.NavigationOf(DiffPaneSide.Old).Row);
        Assert.False(view.NavigationOf(DiffPaneSide.Old).Bar); // the empty, disabled row
    }

    private const string TwoChanges = """
        namespace Greetings;

        public static class Greeter
        {
            // Says hi.
            public static string Greet(string name) => $"Hello from {name}";

            public static string Wave() => "\\o/";
        }

        """;

    /// <summary>Clicks a button of the open dialog.</summary>
    private static void Answer(ShellFixture f, string button) => f.Shell.Dialogs.Buttons.Single(b => b.Label == button).Command.Execute(null);

    private static async Task<DiffView> OpenDiffViewAsync(ShellFixture f, string file, bool staged)
    {
        var document = await f.Shell.OpenDiffAsync(file, staged);
        Assert.NotNull(document);
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<DiffView>().Any(v => v.ViewModel == document.ViewModel) && document.View is not null);
        return f.Window.GetVisualDescendants().OfType<DiffView>().Single(v => v.ViewModel == document.ViewModel);
    }

    [AvaloniaFact]
    public async Task Stage_Change_stages_the_rows_drawn_even_when_git_puts_two_changes_in_one_hunk()
    {
        var (f, folder, file) = await RepositoryAsync();
        await using var fixture = f;
        await File.WriteAllTextAsync(file, TwoChanges);
        Assert.Single(Git(folder, "diff").Split('\n'), l => l.StartsWith("@@", StringComparison.Ordinal)); // one git hunk
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        var view = await OpenDiffViewAsync(f, file, staged: false);
        Assert.Equal(2, view.ViewModel.Changes.Count);

        // The second change: F8, then Stage Change (the command the context menu runs).
        await f.RunAsync("git.diff.nextChange");
        Assert.Equal("Change 2 of 2", view.CountText);
        await f.RunAsync("git.diff.stageChange");
        await ShellFixture.PumpUntil(() => !view.ViewModel.IsBusy && view.ViewModel.Changes.Count == 1);
        var staged = Git(folder, "diff", "--cached");
        Assert.Contains("\\o/", staged, StringComparison.Ordinal);
        Assert.DoesNotContain("Says hi", staged, StringComparison.Ordinal);
        Assert.Contains("Says hi", Git(folder, "diff"), StringComparison.Ordinal);

        // The staged diff unstages it: Unstage Change, then nothing is staged.
        var stagedView = await OpenDiffViewAsync(f, file, staged: true);
        Assert.Equal("1 change", stagedView.CountText);
        Assert.Equal(["Unstage Selected Lines", "Unstage Change", "Next Change", "Previous Change", "Copy", "Open Greeter.cs"], stagedView.ContextMenuEntries().Select(e => e.Header));
        Assert.True(await stagedView.ApplyToChangeAsync(DiffAction.Unstage));
        Assert.Equal(string.Empty, Git(folder, "diff", "--cached"));
    }

    [AvaloniaFact]
    public async Task The_working_tree_side_is_the_open_buffer_and_staging_saves_it_first()
    {
        var (f, folder, file) = await RepositoryAsync();
        await using var fixture = f;
        var editor = await f.Shell.OpenFileAsync(file);
        await ShellFixture.PumpUntil(() => editor?.Controller is not null);
        editor!.Model.Text.Insert(0, "// unsaved\n");
        Assert.True(editor.Model.IsModified);

        var view = await OpenDiffViewAsync(f, file, staged: false);
        Assert.StartsWith("// unsaved", view.RightText, StringComparison.Ordinal);
        Assert.Equal("Greeter.cs (Working tree) unsaved edits included", view.HeaderOf(DiffPaneSide.New));
        var change = view.ViewModel.Changes[0];
        Assert.Equal("Stage Change: line 1 (saves Greeter.cs first)", view.StagingTooltipOf(change, DiffAction.Stage));

        Assert.True(await view.ApplyToChangeAsync(DiffAction.Stage));
        Assert.False(editor.Model.IsModified);
        Assert.StartsWith("// unsaved", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
        Assert.Contains("+// unsaved", Git(folder, "diff", "--cached"), StringComparison.Ordinal);

        // The diff follows the editor by itself: an edit shows after a pause.
        editor.Model.Text.Insert(0, "// again\n");
        await ShellFixture.PumpUntil(() => view.RightText.StartsWith("// again", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task A_save_that_changes_the_text_stops_the_action_and_says_so()
    {
        var (repo, _, file) = await RepositoryAsync();
        await using var repoFixture = repo;
        var model = CodeDocument.Create(file, await File.ReadAllTextAsync(file));
        model.Text.Insert(0, "// typed\n");
        string? message = null;
        var services = new DiffServices(repo.Shell)
        {
            OpenDocument = path => path == file ? model : null,
            // Format on save: what is written isn't what was typed.
            SaveAsync = async _ =>
            {
                await File.WriteAllTextAsync(file, "// formatted\n" + Original);
                return true;
            },
            ShowMessage = text => message = text,
        };
        var vm = new DiffViewModel(repo.Shell.Git, file, staged: false) { Services = services };
        await vm.LoadAsync();
        Assert.StartsWith("// typed", vm.NewText, StringComparison.Ordinal);

        Assert.False(await vm.ApplyAsync(DiffAction.Stage, vm.ChangeRows(vm.Changes[0].FirstRow)));
        Assert.Contains("changed as it was saved", message, StringComparison.Ordinal);
        Assert.Equal(string.Empty, Git(Path.GetDirectoryName(file)!, "diff", "--cached"));
    }

    [AvaloniaFact]
    public async Task Discard_asks_first_with_cancel_the_default_and_names_the_lines()
    {
        var (f, folder, file) = await RepositoryAsync();
        await using var fixture = f;
        await File.WriteAllTextAsync(file, TwoChanges);
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        var view = await OpenDiffViewAsync(f, file, staged: false);

        // Discard Change… on the first change (the caret's): asked, cancelled, nothing changed.
        var discarding = view.ApplyToChangeAsync(DiffAction.Discard);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Equal("Discard line 5 of Greeter.cs?", f.Shell.Dialogs.Title);
        Assert.Contains("can't be undone", f.Shell.Dialogs.Message, StringComparison.Ordinal);
        Assert.True(f.Shell.Dialogs.Buttons.Single(b => b.Label == "Cancel").IsDefault);
        Assert.Contains(f.Shell.Dialogs.Changes, d => d.IsAdded && d.Text.Contains("Says hi", StringComparison.Ordinal));
        Answer(f, "Cancel");
        Assert.False(await discarding);
        Assert.Equal(TwoChanges, await File.ReadAllTextAsync(file));

        // Again, answered Discard: only that change goes.
        discarding = view.ApplyToChangeAsync(DiffAction.Discard);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Answer(f, "Discard");
        Assert.True(await discarding);
        var text = await File.ReadAllTextAsync(file);
        Assert.Contains("Says hello.", text, StringComparison.Ordinal);
        Assert.Contains("\\o/", text, StringComparison.Ordinal);

        // Discard All… keeps today's question; Cancel leaves the file.
        var all = view.DiscardAllAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Equal("Discard changes to Greeter.cs?", f.Shell.Dialogs.Title);
        Answer(f, "Cancel");
        Assert.False(await all);
        Assert.Equal(text, await File.ReadAllTextAsync(file));
    }

    [AvaloniaFact]
    public async Task The_staging_column_is_hidden_by_default_and_its_toggle_is_a_setting()
    {
        var (f, folder, file) = await RepositoryAsync();
        await using var fixture = f;
        await File.WriteAllTextAsync(file, TwoChanges);
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        var view = await OpenDiffViewAsync(f, file, staged: false);

        Assert.False(view.ViewModel.ShowStagingControls);
        Assert.Empty(view.StagingButtons);
        Assert.Contains("Stage All", view.ToolbarNames);
        Assert.Contains("Discard All…", view.ToolbarNames);
        Assert.Contains("Show Staging Controls", view.ToolbarNames);
        Assert.Equal(["Stage Selected Lines", "Stage Change", "Discard Selected Lines…", "Discard Change…", "Next Change", "Previous Change", "Copy", "Open Greeter.cs"], view.ContextMenuEntries().Select(e => e.Header));

        // On an unchanged line, the change commands are off.
        view.EditorOf(DiffPaneSide.New).TextArea.Caret.Line = 1;
        view.EditorOf(DiffPaneSide.New).TextArea.Focus();
        Assert.All(view.ContextMenuEntries().Take(4), e => Assert.False(e.Enabled));

        // Shown (the command, as the toolbar's toggle and the gear menu run it): saved, and each change has Stage and Discard.
        await f.RunAsync("git.diff.toggleStagingControls");
        Assert.True(view.ViewModel.ShowStagingControls);
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Settings.GetBoolean(BuiltIns.DiffStagingControlsSetting, false));
        await ShellFixture.PumpUntil(() => view.StagingButtons.Count == 4);
        Assert.Equal([DiffAction.Stage, DiffAction.Discard, DiffAction.Stage, DiffAction.Discard], view.StagingButtons.Select(b => b.Action));
        Assert.Equal("Stage Change: line 5", view.StagingTooltipOf(view.ViewModel.Changes[0], DiffAction.Stage));

        // Clicking a change's Stage stages that change.
        view.StagingButtons[2].Button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await ShellFixture.PumpUntil(() => Git(folder, "diff", "--cached").Contains("\\o/", StringComparison.Ordinal) && !view.ViewModel.IsBusy);

        // Stage All, then the staged diff's column has Unstage alone, and Unstage All empties the index.
        Assert.True(await view.StageAllAsync());
        Assert.Equal(string.Empty, Git(folder, "diff"));
        var stagedView = await OpenDiffViewAsync(f, file, staged: true);
        await ShellFixture.PumpUntil(() => stagedView.StagingButtons.Count == 2);
        Assert.All(stagedView.StagingButtons, b => Assert.Equal(DiffAction.Unstage, b.Action));
        Assert.Contains("Unstage All", stagedView.ToolbarNames);
        Assert.True(await stagedView.UnstageAllAsync());
        Assert.Equal(string.Empty, Git(folder, "diff", "--cached"));
    }

    private static Color Token(string key, Avalonia.Styling.ThemeVariant variant) =>
        Application.Current!.TryGetResource(key, variant, out var value) && value is ISolidColorBrush brush ? brush.Color : throw new InvalidOperationException($"{key} in {variant}");

    /// <summary>The pixel at an x in a text view, in the middle of a 1-based line (just under its top for <paramref name="top"/>).</summary>
    private static Color Pixel(Avalonia.Media.Imaging.WriteableBitmap frame, AvaloniaEdit.Rendering.TextView view, int line, double x, bool top = false)
    {
        var y = view.GetVisualTopByDocumentLine(line) - view.VerticalOffset + (top ? 2 : view.DefaultLineHeight / 2);
        var point = view.TranslatePoint(new Point(x, y), (Visual?)Avalonia.Controls.TopLevel.GetTopLevel(view) ?? view)!.Value;
        using var buffer = frame.Lock();
        var offset = ((int)point.Y * buffer.RowBytes) + ((int)point.X * 4);
        var (b0, b1, b2, b3) = (Marshal.ReadByte(buffer.Address, offset), Marshal.ReadByte(buffer.Address, offset + 1), Marshal.ReadByte(buffer.Address, offset + 2), Marshal.ReadByte(buffer.Address, offset + 3));
        return buffer.Format == PixelFormat.Rgba8888 ? Color.FromArgb(b3, b0, b1, b2) : Color.FromArgb(b3, b2, b1, b0);
    }

    /// <summary>The colors along a line's rows of pixels, from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static HashSet<Color> Row(Avalonia.Media.Imaging.WriteableBitmap frame, AvaloniaEdit.Rendering.TextView view, int line, double from, double to)
    {
        var colors = new HashSet<Color>();
        for (var dy = 1.0; dy < view.DefaultLineHeight - 1; dy += 2)
        {
            for (var x = from; x < to; x++)
            {
                var y = view.GetVisualTopByDocumentLine(line) - view.VerticalOffset + dy;
                var point = view.TranslatePoint(new Point(x, y), (Visual?)Avalonia.Controls.TopLevel.GetTopLevel(view) ?? view)!.Value;
                using var buffer = frame.Lock();
                var offset = ((int)point.Y * buffer.RowBytes) + ((int)point.X * 4);
                var (b0, b1, b2, b3) = (Marshal.ReadByte(buffer.Address, offset), Marshal.ReadByte(buffer.Address, offset + 1), Marshal.ReadByte(buffer.Address, offset + 2), Marshal.ReadByte(buffer.Address, offset + 3));
                colors.Add(buffer.Format == PixelFormat.Rgba8888 ? Color.FromArgb(b3, b0, b1, b2) : Color.FromArgb(b3, b2, b1, b0));
            }
        }

        return colors;
    }
}
