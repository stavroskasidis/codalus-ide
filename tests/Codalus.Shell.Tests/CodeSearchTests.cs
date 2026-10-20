using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Search;

namespace Codalus.Shell.Tests;

/// <summary>
/// Code Search (M47) in the headless shell on samples/LanguageFeatures, with the real build and C# language hosts: the
/// chips and prefixes, ranking, the editable preview, Enter, the scope, incomplete results, Feature Search, docking,
/// and the persistent index searching before the language host has loaded the solution.
/// </summary>
public sealed class CodeSearchTests
{
    private static CodeSearchRow Row(CodeSearchViewModel search, string name, CodeSearchKind kind) =>
        search.Rows.First(r => r.Result is { } result && result.Name == name && result.Kind == kind);

    private static bool Has(CodeSearchViewModel search, string name, CodeSearchKind kind) =>
        search.Rows.Any(r => r.Result is { } result && result.Name == name && result.Kind == kind);

    private static async Task SearchAsync(ShellFixture f, string query, Func<bool> until, int seconds = 30)
    {
        var search = f.Shell.CodeSearch;
        search.Query = query;
        await ShellFixture.PumpUntil(until, seconds, () => $"'{query}': {string.Join(", ", search.Rows.Select(r => $"{r.Name} ({r.Result?.Kind})"))}; {State(f)}");
    }

    /// <summary>The window's state, the focus and Go To All's binding, for a wait that times out.</summary>
    private static string State(ShellFixture f)
    {
        var search = f.Shell.CodeSearch;
        var tab = search.IsCodeTab ? "Code Search" : search.IsFeatureTab ? "Feature Search" : "no";
        var gotoAll = f.Shell.Workbench.Keybindings.GetBinding("workbench.action.gotoAll")?.ToLabel(Codalus.Platform.PlatformInfo.Current) ?? "unbound";
        return $"open {search.IsOpen}, {tab} tab, floating {search.IsFloatingOpen}, docked {search.IsDocked}, filter {search.Filter}, "
            + $"query '{search.Query}', {search.Rows.Count} rows, incomplete {search.IsIncomplete}, focus on {f.Window.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing"}, "
            + $"Go To All is {gotoAll}";
    }

    [AvaloniaFact]
    public async Task Code_search_filters_ranks_previews_edits_and_opens_results()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("LanguageFeatures");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "LanguageFeatures.slnx"));
        var shapes = Path.Combine(folder, "src", "Lib", "Shapes.cs");
        var program = Path.Combine(folder, "src", "App", "Program.cs");
        var search = f.Shell.CodeSearch;

        // Incomplete while the projects load (the notice and the refresh are A_second_open_…'s, where it lasts): files first.
        f.Press(Key.T, ShellFixture.Primary); // primary+t: Cmd+T on macOS
        await ShellFixture.PumpUntil(() => search is { IsOpen: true, IsCodeTab: true, IsFloatingOpen: true }, describe: () => State(f));
        Assert.True(f.Shell.CodeSearchIncomplete);
        await SearchAsync(f, "Shapes", () => search.Rows.Any(r => r.Name == "Shapes.cs"));
        Assert.True(f.Window.FindDescendantOfType<CodeSearchView>()!.IsEffectivelyVisible);

        // Complete once the C# host has indexed the solution; the open window's results refresh by themselves.
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: "ready", Projects: 2 }, seconds: 180, describe: () => $"C# {f.Shell.Languages.Status(ShellHost.CSharpLanguageId)}; {State(f)}");
        await ShellFixture.PumpUntil(() => f.Shell.CodeSearchIndex.IsComplete && !search.IsIncomplete && search.Rows.Any(r => r.Name == "Shapes.cs"), seconds: 60, describe: () => $"index complete {f.Shell.CodeSearchIndex.IsComplete}, {f.Shell.CodeSearchIndex.SymbolCount} symbols; {State(f)}");
        await SearchAsync(f, "Shape", () => Has(search, "Shape", CodeSearchKind.Type));
        Assert.Contains(search.Rows, r => r.Name == "IShape" && r.Result!.Kind == CodeSearchKind.Type);

        // Two lines: the name with its match, then where it is; the file on the right.
        var circleQuery = "Circle";
        await SearchAsync(f, circleQuery, () => Has(search, "Circle", CodeSearchKind.Type));
        var circle = Row(search, "Circle", CodeSearchKind.Type);
        Assert.Equal("in Lib (project Lib)", circle.Detail);
        Assert.Equal("Shapes.cs", circle.FileLabel);
        Assert.Equal([0, 1, 2, 3, 4, 5], circle.Highlights);
        Assert.Same(circle, search.Rows.First(r => r.IsItem)); // the exact type first
        Assert.Contains("Circle, type, in Lib (project Lib), Shapes.cs", circle.AccessibleName, StringComparison.Ordinal);

        // Prefixes and chips filter the kinds; a prefix shows as its chip.
        await SearchAsync(f, "f:Shap", () => search.Rows.Count > 0 && search.Rows.All(r => r.Result?.Kind == CodeSearchKind.File));
        Assert.True(search.Chips.Single(c => c.Filter == CodeSearchFilter.Files).IsSelected);
        await SearchAsync(f, "t:Shape", () => search.Rows.Count > 0 && search.Rows.All(r => r.Result?.Kind == CodeSearchKind.Type));
        Assert.Contains(search.Rows, r => r.Name == "IShape");
        await SearchAsync(f, "Area", () => Has(search, "Area", CodeSearchKind.Member));
        search.Filter = CodeSearchFilter.Members;
        await ShellFixture.PumpUntil(() => search.Rows.Count > 0 && search.Rows.All(r => r.Result?.Kind == CodeSearchKind.Member), describe: () => State(f));
        Assert.Contains(search.Rows, r => r.Detail == "in Square (project Lib)");
        f.Press(Key.X, RawInputModifiers.Alt); // the text chip, from the keyboard
        await ShellFixture.PumpUntil(() => search.Filter == CodeSearchFilter.Text && search.Rows.Count > 0 && search.Rows.All(r => r.Result?.Kind == CodeSearchKind.Text), describe: () => State(f));
        Assert.Contains(search.Rows, r => r.FileLabel!.StartsWith("Shapes.cs:", StringComparison.Ordinal) && r.Name.Contains("Area()", StringComparison.Ordinal));
        f.Press(Key.A, RawInputModifiers.Alt);
        await ShellFixture.PumpUntil(() => search.Filter == CodeSearchFilter.All, describe: () => State(f));

        // The preview: the result's file at its line, the match selected, an editor of the document itself.
        await SearchAsync(f, "Circle", () => Has(search, "Circle", CodeSearchKind.Type));
        search.SelectedIndex = search.Rows.IndexOf(Row(search, "Circle", CodeSearchKind.Type));
        await ShellFixture.PumpUntil(() => f.Shell.CodeSearchPreview is { } p && p.Editor.SelectedText == "Circle", describe: () => State(f));
        var preview = f.Shell.CodeSearchPreview!;
        Assert.Equal(shapes, preview.Controller.Model.Path);
        Assert.Contains("class Circle", preview.Controller.Model.Text.GetText(preview.Controller.Model.Text.GetLineByOffset(preview.Editor.CaretOffset)), StringComparison.Ordinal);
        Assert.Same(preview, f.Window.FindDescendantOfType<CodeSearchView>()!.Preview.Content);
        Assert.Equal(search.SelectedIndex, f.Window.FindDescendantOfType<CodeSearchView>()!.FindDescendantOfType<Avalonia.Controls.ListBox>()!.SelectedIndex); // the list shows it selected
        Assert.Equal(shapes, search.PreviewTitle);
        Assert.StartsWith("Ln ", search.PreviewStatus, StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => f.Shell.CodeSearchPreview?.Controller.Colorizer.HasTokens == true, seconds: 60, describe: () => State(f)); // the language service's colors, as in a tab
        preview = f.Shell.CodeSearchPreview!;

        // Edited there and saved with Ctrl+S: the document changes on disk, with undo.
        preview.Editor.Document.Insert(0, "// previewed\n");
        Assert.True(preview.Controller.Model.IsModified);
        Assert.Contains("modified", search.PreviewTitle, StringComparison.Ordinal);
        await search.SavePreviewAsync();
        Assert.StartsWith("// previewed", await File.ReadAllTextAsync(shapes, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.False(preview.Controller.Model.IsModified);
        preview.Editor.Undo();
        Assert.True(preview.Controller.Model.IsModified);

        // An unsaved edit isn't lost when the preview moves on: the document opens with it.
        await SearchAsync(f, "Program.cs", () => search.Rows.Any(r => r.Name == "Program.cs"));
        search.SelectedIndex = search.Rows.IndexOf(search.Rows.First(r => r.Name == "Program.cs"));
        await ShellFixture.PumpUntil(() => f.Shell.CodeSearchPreview?.Controller.Model.Path == program, describe: () => State(f));
        await ShellFixture.PumpUntil(() => f.Shell.Editors.Any(e => e.Model.Path == shapes && e.Model.IsModified), describe: () => State(f));
        await f.Shell.SaveAllAsync();

        // Ctrl+Enter opens and keeps the window; Enter opens at the declaration and closes it.
        await SearchAsync(f, "Geometry", () => Has(search, "Geometry", CodeSearchKind.Type));
        search.SelectedIndex = search.Rows.IndexOf(Row(search, "Geometry", CodeSearchKind.Type));
        f.Window.FindDescendantOfType<CodeSearchView>()!.FocusInput();
        f.Press(Key.Enter, RawInputModifiers.Control);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == shapes, describe: () => State(f));
        Assert.True(search.IsOpen);
        await SearchAsync(f, "Scale", () => Has(search, "Scale", CodeSearchKind.Member));
        search.SelectedIndex = search.Rows.IndexOf(Row(search, "Scale", CodeSearchKind.Member));
        f.Window.FindDescendantOfType<CodeSearchView>()!.FocusInput();
        f.Press(Key.Enter);
        await ShellFixture.PumpUntil(() => !search.IsOpen && f.Shell.ActiveEditor?.Controller is { } c && c.Model.Text.GetText(c.Model.Text.GetLineByOffset(c.Editor.CaretOffset)).Contains("Scale(", StringComparison.Ordinal), describe: () => State(f));
        Assert.Null(f.Shell.CodeSearchPreview); // the preview let go of its document

        // The scope: the current document only.
        await f.Shell.OpenFileAsync(program);
        await f.Shell.GoToAllAsync();
        search.Scope = CodeSearchScope.CurrentDocument;
        await SearchAsync(f, "Circle", () => search.Rows.Count > 0 && search.Rows.All(r => r.Result?.Path == program && r.Result.Kind == CodeSearchKind.Text));
        Assert.Equal("currentDocument", f.Shell.Workbench.Settings.GetString(ShellHost.CodeSearchScopeSetting));
        search.Scope = CodeSearchScope.EntireSolution;

        // : goes to a line of the active document.
        await SearchAsync(f, ":3", () => search.Rows.Any(r => r.Result?.Kind == CodeSearchKind.Line));
        await search.AcceptAsync();
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Controller?.Caret.Line == 3, describe: () => State(f));

        // Esc closes; the focus goes back to the editor.
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Controller?.Editor.TextArea.IsFocused == true, describe: () => State(f));
        await Task.Delay(500, TestContext.Current.CancellationToken); // the opened document's focus retries are over
        ShellFixture.Pump();
        await f.Shell.GoToAllAsync();
        await ShellFixture.PumpUntil(() => search.IsFloatingOpen, describe: () => State(f));
        ShellFixture.Pump(); // the box takes the focus once the window is laid out
        Assert.NotNull(f.Window.FocusManager?.GetFocusedElement() is Avalonia.Visual focused ? focused.FindAncestorOfType<CodeSearchView>() : null);
        f.Press(Key.Escape);
        Assert.False(search.IsOpen);
        await ShellFixture.PumpUntil(() => f.Window.FocusManager?.GetFocusedElement() is Avalonia.Visual focused && focused.FindAncestorOfType<CodeSearchView>() is null, describe: () => State(f));
    }

    /// <summary>
    /// The preview asking for a file twice while it is read (two batches of the same results) gets one text model, and
    /// the stale request letting go leaves the file open in the language service for the other. Two models of
    /// Shapes.cs, the stale one let go while the C# host started, left the host with an empty file (CI, Linux), so a
    /// search found only its file name and text lines.
    /// </summary>
    [AvaloniaFact]
    public async Task Two_requests_for_a_files_model_at_once_share_it_and_one_letting_go_keeps_it_open()
    {
        await using var f = ShellFixture.Create();
        var path = Path.Combine(f.UserData, "Shapes.cs");
        await File.WriteAllTextAsync(path, "public sealed class Shape;\n", TestContext.Current.CancellationToken);

        var first = f.Shell.AcquireModelAsync(path);
        var second = f.Shell.AcquireModelAsync(path);
        var model = await first;
        f.Shell.ReleaseModel(model); // the stale request, perhaps before the other has taken the model
        Assert.Same(model, await second);
        Assert.True(f.Shell.Languages.IsOpen(path));

        f.Shell.ReleaseModel(model);
        Assert.False(f.Shell.Languages.IsOpen(path));
    }

    [AvaloniaFact]
    public async Task Feature_search_runs_commands_and_code_search_docks_as_a_tool_window()
    {
        await using var f = ShellFixture.Create();
        var search = f.Shell.CodeSearch;

        // Feature Search: the title row's search box, a command with its key, run from the window.
        await f.RunAsync("workbench.action.featureSearch");
        await ShellFixture.PumpUntil(() => search is { IsOpen: true, IsFeatureTab: true }, describe: () => State(f));
        Assert.False(search.HasPreview);
        await SearchAsync(f, "open settings", () => search.Rows.Any(r => r.Feature?.Id == "workbench.openSettings"));
        search.SelectedIndex = search.Rows.IndexOf(search.Rows.First(r => r.Feature?.Id == "workbench.openSettings"));
        await search.AcceptAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Layout.FindDocument<SettingsDocument>(SettingsDocument.DocumentId) is not null, describe: () => State(f));
        Assert.False(search.IsOpen);

        // An options page opens Options on its category.
        f.Press(Key.Q, RawInputModifiers.Control); // ctrl+q on every platform (Cmd+Q quits on macOS)
        await ShellFixture.PumpUntil(() => search is { IsOpen: true, IsFeatureTab: true }, describe: () => State(f));
        await SearchAsync(f, "Code Search", () => search.Rows.Any(r => r.Feature?.Kind == FeatureSearchKind.OptionsPage && r.Name == "Code Search"));
        search.SelectedIndex = search.Rows.IndexOf(search.Rows.First(r => r.Feature?.Kind == FeatureSearchKind.OptionsPage && r.Name == "Code Search"));
        await search.AcceptAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Layout.FindDocument<SettingsDocument>(SettingsDocument.DocumentId)?.ViewModel.SelectedCategory?.Name == "Code Search", describe: () => State(f));

        // The tabs switch with the keys; the pin keeps the window open after a result opens.
        f.Press(Key.T, ShellFixture.Primary);
        await ShellFixture.PumpUntil(() => search is { IsOpen: true, IsCodeTab: true }, describe: () => State(f));
        var notes = Path.Combine(f.UserData, "Notes.txt");
        await File.WriteAllTextAsync(notes, "one\ntwo\nthree\n", TestContext.Current.CancellationToken);
        await f.Shell.OpenFileAsync(notes);
        await f.Shell.GoToAllAsync();
        search.IsPinned = true;
        await SearchAsync(f, "notes", () => search.Rows.Any(r => r.Name == "Notes.txt"));
        await search.AcceptAsync();
        Assert.True(search.IsOpen);
        search.IsPinned = false;

        // Docked: the same window in a tool window, the preview there; floating again.
        search.ToggleDockCommand.Execute(null);
        await ShellFixture.PumpUntil(() => search.IsDocked && f.Shell.Layout.IsVisible(ShellHost.CodeSearchToolWindowId), describe: () => State(f));
        Assert.False(search.IsFloatingOpen);
        await SearchAsync(f, "notes", () => search.Rows.Any(r => r.Name == "Notes.txt"));
        search.SelectedIndex = search.Rows.IndexOf(search.Rows.First(r => r.Name == "Notes.txt"));
        await ShellFixture.PumpUntil(() => f.Shell.CodeSearchPreview is { } preview && preview.FindAncestorOfType<CodeSearchView>() is { IsToolWindow: true }, describe: () => State(f));
        search.ToggleDockCommand.Execute(null);
        await ShellFixture.PumpUntil(() => !search.IsDocked && search.IsFloatingOpen && !f.Shell.Layout.IsVisible(ShellHost.CodeSearchToolWindowId), describe: () => State(f));
        await ShellFixture.PumpUntil(() => f.Shell.CodeSearchPreview is { } preview && preview.FindAncestorOfType<CodeSearchView>() is { IsToolWindow: false }, describe: () => State(f));

        // The window's size, pin and preview are remembered with the shell's state.
        search.Width = 900;
        search.ShowPreview = false;
        var state = search.CaptureState();
        Assert.Equal((900, false, false), (state.Width, state.ShowPreview, state.Docked));
        search.Close();
    }

    [AvaloniaFact]
    public async Task A_second_open_searches_the_saved_index_before_the_language_host_has_loaded()
    {
        // The sample outside both sessions' user data folders: the index's paths stay the same.
        var root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-codesearch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.Copy(Path.Combine(ShellFixture.RepoRoot, "samples", "Directory.Build.props"), Path.Combine(root, "Directory.Build.props"));
        File.Copy(Path.Combine(ShellFixture.RepoRoot, "samples", "Directory.Packages.props"), Path.Combine(root, "Directory.Packages.props"));
        Codalus.Tests.FixtureSdk.Pin(root);
        CopyTree(Path.Combine(ShellFixture.RepoRoot, "samples", "LanguageFeatures"), Path.Combine(root, "LanguageFeatures"));
        var solution = Path.Combine(root, "LanguageFeatures", "LanguageFeatures.slnx");
        try
        {
            await using (var first = ShellFixture.Create())
            {
                first.Shell.Trust.Trust(root);
                await first.Shell.OpenSolutionAsync(solution);
                await ShellFixture.PumpUntil(() => first.Shell.CodeSearchIndex.IsComplete && first.Shell.CodeSearchIndex.SymbolCount > 10, seconds: 180);
                await first.Shell.CloseSolutionAsync();
                await first.Shell.SolutionState.FlushAsync();
                var saved = Path.Combine(new Workbench.Projects.SolutionStateKey(solution, IsFolder: false).Directory, CodeSearchIndex.FileName);
                Assert.True(File.Exists(saved), saved);
            }

            // A new session (its own user data, so a cold language host): the saved index answers first.
            await using var second = ShellFixture.Create();
            second.Shell.Trust.Trust(root);
            var opening = second.Shell.OpenSolutionAsync(solution);
            var search = second.Shell.CodeSearch;
            await second.Shell.GoToAllAsync();
            search.Query = "Circle";
            string? statusWhenFound = "unknown";
            await ShellFixture.PumpUntil(() =>
            {
                if (!Has(search, "Circle", CodeSearchKind.Type))
                {
                    return false;
                }

                statusWhenFound = second.Shell.Languages.Status(ShellHost.CSharpLanguageId)?.State;
                return true;
            }, seconds: 60);
            Assert.NotEqual("ready", statusWhenFound);
            Assert.True(search.IsIncomplete);
            Assert.Equal("in Lib (project Lib)", Row(search, "Circle", CodeSearchKind.Type).Detail);
            await opening;
            await ShellFixture.PumpUntil(() => second.Shell.CodeSearchIndex.IsComplete && !search.IsIncomplete, seconds: 180);
            search.Close();
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source).Where(d => Path.GetFileName(d) is not ("bin" or "obj")))
        {
            CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}
