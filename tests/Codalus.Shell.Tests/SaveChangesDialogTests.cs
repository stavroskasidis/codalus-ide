using Avalonia.Headless.XUnit;
using Codalus.Projects.Protocol;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Tests;

/// <summary>
/// Asking to save before closing is the classic IDE's modal dialog, "Save changes to the following items?", not a toast:
/// for a tab, Close All, the solution and exiting, with the unsaved files under their solution, solution folders and
/// project, each checked when there are several.
/// </summary>
public sealed class SaveChangesDialogTests
{
    private static readonly string s_root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Contoso"));

    private static string At(params string[] parts) => Path.Combine([s_root, .. parts]);

    private static SolutionInfo Solution() => new(
        At("Contoso.slnx"),
        "slnx",
        [new SolutionFolderInfo("/apps/", "apps", null, []), new SolutionFolderInfo("/Solution Items/", "Solution Items", null, [At("README.md")])],
        [new SolutionProjectInfo(At("src", "Contoso.Web", "Contoso.Web.csproj"), "Contoso.Web", "/apps/", null), new SolutionProjectInfo(At("src", "App.Core", "App.Core.csproj"), "App.Core", null, null)],
        [],
        null,
        Trusted: true);

    private static string Row(DialogChoice row) => $"{new string(' ', row.Level * 2)}{row.Label}{(row.Detail is null ? string.Empty : $" ({row.Detail})")}{(row.Checkable ? " [x]" : string.Empty)}";

    [Fact]
    public void The_files_are_listed_under_their_solution_folders_and_projects_each_checked()
    {
        var outside = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "elsewhere", "notes.txt"));
        var rows = SaveChangesTree.Rows(Solution(), [], [
            ("Program.cs", At("src", "Contoso.Web", "Program.cs")),
            ("notes.txt", outside),
            ("HomeController.cs", At("src", "Contoso.Web", "Controllers", "HomeController.cs")),
            ("README.md", At("README.md")),
            ("Greeter.cs", At("src", "App.Core", "Greeter.cs")),
        ]);

        Assert.Equal(
            [
                "Contoso.slnx",
                "  apps",
                "    Contoso.Web",
                "      HomeController.cs (Controllers) [x]",
                "      Program.cs [x]",
                "  Solution Items",
                "    README.md [x]",
                "  App.Core",
                "    Greeter.cs [x]",
                $"notes.txt ({Path.GetDirectoryName(outside)}) [x]",
            ],
            rows.Select(Row));
        Assert.Equal([SaveChangesTree.FileId(2), SaveChangesTree.FileId(0)], rows.Where(r => r.Label.EndsWith("Controller.cs", StringComparison.Ordinal) || r.Label == "Program.cs").Select(r => r.Id));
        Assert.All(rows.Where(r => r.Checkable), r => Assert.True(r.Checked));
        Assert.All(rows, r => Assert.False(r.IsCode));
    }

    [Fact]
    public void One_file_has_no_check_box_and_without_a_solution_the_files_stand_alone()
    {
        Assert.Equal(["Contoso.slnx", "  App.Core", "    Greeter.cs"], SaveChangesTree.Rows(Solution(), [], [("Greeter.cs", At("src", "App.Core", "Greeter.cs"))]).Select(Row));

        var file = At("notes.txt");
        Assert.Equal([$"notes.txt ({s_root})"], SaveChangesTree.Rows(null, [], [("notes.txt", file)]).Select(Row));
        Assert.Equal(["Untitled"], SaveChangesTree.Rows(null, [], [("Untitled", null)]).Select(Row));
    }

    [Fact]
    public async Task Rows_without_a_check_box_are_not_counted_or_returned()
    {
        var dialog = new DialogViewModel();
        var shown = dialog.ShowAsync(new DialogRequest("Save changes to the following items?", string.Empty, ["Save", "Don't Save", "Cancel"])
        {
            Choices =
            [
                new DialogChoice("group:project", "App.Core", true) { Checkable = false },
                new DialogChoice("file:0", "Greeter.cs", true) { Level = 1 },
                new DialogChoice("file:1", "Shapes.cs", true) { Level = 1 },
            ],
        });

        Assert.True(dialog.HasCheckableChoices);
        Assert.Same(dialog.Choices[1], dialog.SelectedChoice); // the first file, not the project
        Assert.False(dialog.Choices[0].IsChecked);
        Assert.Equal("2 of 2", dialog.CheckedSummary);
        Assert.Equal(true, dialog.AllChecked);
        dialog.SelectedChoice = dialog.Choices[0];
        dialog.ToggleSelectedChoice(); // Space on the project: nothing to check
        Assert.False(dialog.Choices[0].IsChecked);

        dialog.Choices[2].IsChecked = false;
        Assert.Null(dialog.AllChecked);
        Assert.Equal("1 of 2", dialog.CheckedSummary);
        dialog.Accept();
        Assert.Equal(["file:0"], (await shown).Checked);
    }

    [AvaloniaFact]
    public async Task Closing_an_unsaved_tab_asks_with_the_dialog()
    {
        await using var f = ShellFixture.Create();
        var (file, editor) = await OpenEditedAsync(f, "notes.txt");

        f.Shell.Layout.CloseDocument(editor);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Equal("Save changes to the following items?", f.Shell.Dialogs.Title);
        // In the OS's order: on macOS the primary button comes last (macOS CI, run 43484636144).
        Assert.Equal(DialogViewModel.Order(["Save", "Don't Save", "Cancel"], "Save", f.Shell.Dialogs.PrimaryLast), f.Shell.Dialogs.Buttons.Select(b => b.Label));
        Assert.Equal("Save", f.Shell.Dialogs.Buttons.Single(b => b.IsDefault).Label);
        Assert.Equal(["notes.txt"], f.Shell.Dialogs.Choices.Select(c => c.Label));
        Assert.False(f.Shell.Dialogs.HasCheckableChoices); // one file: Save and Don't Save are the choice

        f.Shell.Layout.CloseDocument(editor); // a second click on the tab's close asks once
        ShellFixture.Pump();
        f.Shell.Dialogs.Cancel();
        ShellFixture.Pump();
        Assert.False(f.Shell.Dialogs.IsOpen);
        Assert.Contains(editor, f.Shell.Editors);

        f.Shell.Layout.CloseDocument(editor);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        f.Shell.Dialogs.Accept(); // Save
        await ShellFixture.PumpUntil(() => !f.Shell.Editors.Contains(editor));
        Assert.Equal("more notes\n", File.ReadAllText(file));
    }

    [AvaloniaFact]
    public async Task Close_All_asks_once_and_saves_only_the_checked_files()
    {
        await using var f = ShellFixture.Create();
        var (first, _) = await OpenEditedAsync(f, "first.txt");
        var (second, _) = await OpenEditedAsync(f, "second.txt");

        var closing = f.Shell.CloseAllDocumentsAsync(exceptActive: false);
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Equal(["first.txt", "second.txt"], f.Shell.Dialogs.Choices.Select(c => c.Label));
        Assert.Equal("2 of 2", f.Shell.Dialogs.CheckedSummary);
        f.Shell.Dialogs.Choices.Single(c => c.Label == "second.txt").IsChecked = false;
        f.Shell.Dialogs.Accept();
        await closing;
        ShellFixture.Pump();

        Assert.False(f.Shell.Dialogs.IsOpen); // once, not a dialog per tab
        Assert.Empty(f.Shell.Editors);
        Assert.Equal("more notes\n", File.ReadAllText(first));
        Assert.Equal("notes\n", File.ReadAllText(second)); // unchecked: discarded
    }

    [AvaloniaFact]
    public async Task Exiting_asks_and_Cancel_keeps_the_window()
    {
        await using var f = ShellFixture.Create();
        var (file, _) = await OpenEditedAsync(f, "notes.txt");

        var quitting = f.Shell.ConfirmQuitAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        f.Shell.Dialogs.Cancel();
        Assert.False(await quitting);

        quitting = f.Shell.ConfirmQuitAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        f.Shell.Dialogs.Buttons.Single(b => b.Label == "Don't Save").Command.Execute(null);
        Assert.True(await quitting);
        Assert.Equal("notes\n", File.ReadAllText(file));
    }

    [AvaloniaFact]
    public async Task Closing_the_solution_asks_and_Cancel_keeps_it_open()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("LanguageFeatures");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "LanguageFeatures.slnx"));
        var program = Path.Combine(folder, "src", "App", "Program.cs");
        await f.Shell.OpenFileAsync(program);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Controller is not null);
        f.Shell.ActiveEditor!.Model.Text.Insert(0, "// edited\n");

        var closing = f.Shell.CloseSolutionAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Equal(["LanguageFeatures.slnx", "App", "Program.cs"], f.Shell.Dialogs.Choices.Select(c => c.Label));
        f.Shell.Dialogs.Cancel();
        Assert.False(await closing);
        Assert.True(f.Shell.Solutions.IsOpen);
        Assert.Single(f.Shell.Editors);

        closing = f.Shell.CloseSolutionAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        f.Shell.Dialogs.Buttons.Single(b => b.Label == "Don't Save").Command.Execute(null);
        Assert.True(await closing);
        Assert.False(f.Shell.Solutions.IsOpen);
        Assert.Empty(f.Shell.Editors);
        Assert.DoesNotContain("// edited", File.ReadAllText(program), StringComparison.Ordinal);
    }

    private static async Task<(string File, Editor.CodeEditorDocument Editor)> OpenEditedAsync(ShellFixture f, string name)
    {
        var file = Path.Combine(f.UserData, name);
        File.WriteAllText(file, "notes\n");
        await f.Shell.OpenFileAsync(file);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == file && f.Shell.ActiveEditor.Controller is not null);
        var editor = f.Shell.ActiveEditor!;
        editor.Model.Text.Insert(0, "more ");
        ShellFixture.Pump();
        return (file, editor);
    }
}
