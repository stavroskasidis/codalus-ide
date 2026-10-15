using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.Controls;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;

namespace Codalus.Shell.Tests;

/// <summary>
/// M34's final pass (D25's checklist on the surfaces M34d didn't walk): the documents that work as dialogs and the
/// in-window dialog in each of its forms cut no text at their minimum size, and every control of every surface (tool
/// windows, documents, dialogs) has a name a screen reader can say.
/// </summary>
public partial class PolishTests
{
    /// <summary>The smallest a document is expected to be: a 1280×720 window with a tool window docked on each side.</summary>
    private const double DocumentMinWidth = 640;

    /// <summary>The in-window dialog's width (DialogView's MaxWidth is wider; this is its narrowest, in a small window).</summary>
    private const double DialogMinWidth = 440;

    [AvaloniaFact]
    public async Task Documents_and_dialogs_at_their_minimum_size_cut_no_text_and_name_every_control()
    {
        await using var f = ShellFixture.Create();
        var solution = f.CopyFixture();
        f.Shell.Trust.Trust(Path.GetDirectoryName(Path.GetDirectoryName(solution))!);
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Explorer.Rows.Count > 3 && f.Shell.Solutions.Projects.Values.Any(p => p.State == Codalus.Projects.Protocol.ProjectStates.Loaded), seconds: 180);
        var project = f.Shell.Solutions.Projects.Values.First(p => p.State == Codalus.Projects.Protocol.ProjectStates.Loaded).Path;

        var clipped = new List<string>();
        var unnamed = new List<string>();
        var documents = new List<(string Name, Func<object?> Open)>
        {
            ("options", () => { f.Shell.OpenSettings(); return f.Shell.Layout.ActiveDocument; }),
            ("keyboard-shortcuts", () => { f.Shell.OpenKeyboardShortcuts(); return f.Shell.Layout.ActiveDocument; }),
            ("environment", () => { f.Shell.OpenCheckEnvironment(); return f.Shell.Layout.ActiveDocument; }),
            ("project-properties", () => f.Shell.OpenProjectProperties(project) is null ? null : f.Shell.Layout.ActiveDocument),
            ("nuget", () => f.Shell.OpenPackageManager(project) is null ? null : f.Shell.Layout.ActiveDocument),
            ("configuration-manager", () => f.Shell.OpenConfigurationManager() is null ? null : f.Shell.Layout.ActiveDocument),
            ("add-new-item", () => f.Shell.OpenAddNewItem(project) is null ? null : f.Shell.Layout.ActiveDocument),
            ("templates", () => { f.Shell.OpenTemplateManager(); return f.Shell.Layout.ActiveDocument; }),
        };
        foreach (var (name, open) in documents)
        {
            var document = open();
            Assert.True(document is not null, $"{name} didn't open");
            await Task.Delay(300, TestContext.Current.CancellationToken); // documents load their data after they show
            ShellFixture.Pump();
            var window = new Window { Width = DocumentMinWidth, Height = 480, Content = new ContentControl { Content = document } };
            window.Show();
            ShellFixture.Pump();
            ShellFixture.Pump();
            clipped.AddRange(Clipped(window).Select(p => $"{name}: {p}"));
            unnamed.AddRange(Unnamed(window).Select(p => $"{name}: {p}"));
            window.Close();
            if (document is Docking.ShellDocument shellDocument)
            {
                f.Shell.Layout.CloseDocument(shellDocument);
            }

            ShellFixture.Pump();
        }

        // The in-window dialog in each form: a confirmation, a name with an error, a preview of changes, a checklist.
        var dialogs = new List<(string Name, DialogRequest Request)>
        {
            ("confirm", new DialogRequest("Delete 'Program.cs'", "The file goes to the trash. The project file is updated too.", ["Delete", "Cancel"]) { IsDanger = true }),
            ("input", new DialogRequest("Rename", "The new name of the class and its file:", ["Rename", "Cancel"]) { Input = "Greeter", Validate = text => text.Length < 3 ? "The name is too short." : null }),
            ("preview", new DialogRequest("Preview Changes: Rename", "2 changes in 2 files.", ["Apply", "Cancel"])
            {
                Details = [new DetailLine("src/Greeter.cs", "header"), new DetailLine("- public sealed class Greeter", "removed"), new DetailLine("+ public sealed class Welcomer", "added"), new DetailLine("  {")],
            }),
            ("checklist", new DialogRequest("Generate Constructor", "Members to initialize:", ["OK", "Cancel"])
            {
                Choices = [new DialogChoice("name", "string Name", true), new DialogChoice("age", "int Age", false, "a property")],
                Reorderable = true,
            }),
        };
        foreach (var (name, request) in dialogs)
        {
            var dialog = new DialogViewModel();
            var shown = dialog.ShowAsync(request);
            ShellFixture.Pump();
            var window = new Window { Width = DialogMinWidth, Height = 420, Content = new DialogView { DataContext = dialog } };
            window.Show();
            ShellFixture.Pump();
            ShellFixture.Pump();
            clipped.AddRange(Clipped(window).Select(p => $"dialog {name}: {p}"));
            unnamed.AddRange(Unnamed(window).Select(p => $"dialog {name}: {p}"));
            dialog.Cancel();
            await shown;
            window.Close();
            ShellFixture.Pump();
        }

        Assert.True(clipped.Count == 0, "Text cut off:\n" + string.Join('\n', clipped));
        Assert.True(unnamed.Count == 0, "Controls without an accessible name:\n" + string.Join('\n', unnamed));
    }

    [AvaloniaFact]
    public async Task Every_tool_windows_controls_have_an_accessible_name()
    {
        await using var f = ShellFixture.Create();
        var solution = f.CopyFixture();
        f.Shell.Trust.Trust(Path.GetDirectoryName(Path.GetDirectoryName(solution))!);
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Explorer.Rows.Count > 3, seconds: 180);

        var unnamed = new List<string>();
        foreach (var descriptor in f.Shell.Workbench.ToolWindows.All.ToList())
        {
            if (f.Shell.Layout.ShowToolWindow(descriptor.Id, focus: false) is not { } tool)
            {
                continue;
            }

            ShellFixture.Pump();
            var window = new Window { Width = 480, Height = 480, Content = new ContentControl { Content = tool } };
            window.Show();
            ShellFixture.Pump();
            ShellFixture.Pump();
            unnamed.AddRange(Unnamed(window).Select(p => $"{descriptor.Id}: {p}"));
            window.Close();
            ShellFixture.Pump();
        }

        Assert.True(unnamed.Count == 0, "Controls without an accessible name:\n" + string.Join('\n', unnamed));
    }

    /// <summary>
    /// Visible, enabled controls a screen reader stops on that have nothing to say: no automation name, no labeling
    /// control, no text content and no watermark. Rows of lists and trees are named by their item styles, so the
    /// controls inside a row template are left out; so are a control's own template parts (a ComboBox's toggle, a
    /// scroll bar's buttons).
    /// </summary>
    internal static IEnumerable<string> Unnamed(Visual root)
    {
        foreach (var control in root.GetVisualDescendants().OfType<Control>())
        {
            if (control is not (Button or ToggleButton or ComboBox or TextBox or Slider or ListBox or TreeView or NumericUpDown or AutoCompleteBox)
                || control is ScrollBar || control.TemplatedParent is not null && control.TemplatedParent is not ContentPresenter
                || !control.IsEffectivelyVisible || !control.IsEffectivelyEnabled || control.Bounds.Width < 1
                || control.FindAncestorOfType<ListBoxItem>() is not null || control.FindAncestorOfType<TreeViewItem>() is not null
                || control.GetVisualAncestors().Any(a => a.GetType().Name == "DataGridRow")
                || control.FindAncestorOfType<ToolbarPanel>() is { } toolbar && toolbar.Overflowed.Any(o => o == control || control.FindAncestorOfType<Control>() == o))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)) || AutomationProperties.GetLabeledBy(control) is not null)
            {
                continue;
            }

            var text = control switch
            {
                ContentControl { Content: string s } => s,
                ContentControl { Content: TextBlock t } => t.Text,
                ContentControl content when content.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Text)) is { } inner => inner.Text,
                TextBox { PlaceholderText: { Length: > 0 } w } => w,
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            yield return $"{control.GetType().Name}{(control.Name is { Length: > 0 } n ? "#" + n : string.Empty)} ({Describe(control, root)})";
        }
    }
}
