using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// M34h: the maintainer's review of the M34 gallery. Add New Item's rows are the shared row height and its preview has
/// the editor's colors in each theme; NuGet's rows draw no text over text and keep their badges at the gallery's size
/// and the document's minimum, with packages from a feed (the documents' walk had none, so it never saw a row); a
/// preview of changes takes most of the window.
/// </summary>
public partial class PolishTests
{
    [AvaloniaFact]
    public async Task Add_New_Items_rows_are_the_shared_row_height_and_its_preview_has_the_editors_colors()
    {
        await using var f = ShellFixture.Create();
        var solution = f.CopyFixture();
        f.Shell.Trust.Trust(Path.GetDirectoryName(Path.GetDirectoryName(solution))!);
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Values.Any(p => p.State == Codalus.Projects.Protocol.ProjectStates.Loaded), seconds: 180);
        var project = f.Shell.Solutions.Projects.Values.First(p => p.State == Codalus.Projects.Protocol.ProjectStates.Loaded).Path;
        var vm = f.Shell.OpenAddNewItem(project) ?? throw new InvalidOperationException("Add New Item didn't open");
        var view = new AddNewItemView { DataContext = vm };
        var window = new Window { Width = 900, Height = 560, Content = view };
        window.Show();
        await ShellFixture.PumpUntil(() => window.GetVisualDescendants().OfType<ListBoxItem>().Count() > 4);
        vm.Selected = vm.Templates.First(t => t.Template.Extension == ".cs" && t.Name == "Class");
        ShellFixture.Pump();

        // One line each, Shell.Size.Row high (they were two lines with Fluent's padding around them, 52 px).
        var row = (double)Application.Current!.FindResource("Shell.Size.Row")!;
        var rows = window.GetVisualDescendants().OfType<ListBoxItem>().Where(i => i.IsEffectivelyVisible).ToList();
        Assert.All(rows, i => Assert.True(Math.Abs(i.Bounds.Height - row) < 0.5, $"a row of {i.FindAncestorOfType<ListBox>()?.Name ?? "the categories"} is {i.Bounds.Height} px high, not {row}"));

        try
        {
            foreach (var theme in new[] { "light", "dark", "harbor.light", "harbor.dark", "harbor.highContrast" })
            {
                await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, theme, SettingScope.User, CancellationToken.None);
                await ShellFixture.PumpUntil(() => f.Shell.Theme.ActiveTheme == theme && f.Shell.EditorTheme.Id == theme);
                ShellFixture.Pump();
                ShellFixture.Pump();

                // The class's text, colored by the editor theme's semantic colors: "internal class Class1".
                var preview = view.Preview ?? throw new InvalidOperationException("no preview");
                var colors = preview.GrammarColors ?? throw new InvalidOperationException("the preview isn't in the editor's colors");
                var text = preview.Editor.Document;
                var declaration = text.Lines.First(l => text.GetText(l).Contains("class ", StringComparison.Ordinal));
                var line = text.GetText(declaration);
                var keyword = line.IndexOf("class ", StringComparison.Ordinal);
                var name = keyword + "class ".Length;
                Assert.Equal(TokenTypes.IndexOf("keyword"), colors.TokenAt(declaration.LineNumber - 1, keyword));
                Assert.Equal(TokenTypes.IndexOf("class"), colors.TokenAt(declaration.LineNumber - 1, name));
                Assert.Equal(f.Shell.EditorTheme.TokenColor("keyword"), Drawn(preview, declaration.Offset + keyword));
                Assert.Equal(f.Shell.EditorTheme.TokenColor("class"), Drawn(preview, declaration.Offset + name));
                Assert.NotEqual(Drawn(preview, declaration.Offset + keyword), Drawn(preview, declaration.Offset + name));
            }
        }
        finally
        {
            await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, (System.Text.Json.JsonElement?)null, SettingScope.User, CancellationToken.None);
            window.Close();
        }
    }

    /// <summary>The color the code view draws the character at <paramref name="offset"/> in (ARGB).</summary>
    private static uint? Drawn(Editor.CodeViewer viewer, int offset)
    {
        var textView = viewer.Editor.TextArea.TextView;
        var line = textView.GetOrConstructVisualLine(viewer.Editor.Document.GetLineByOffset(offset));
        var column = offset - line.FirstDocumentLine.Offset;
        var element = line.Elements.First(e => column >= e.RelativeTextOffset && column < e.RelativeTextOffset + e.DocumentLength);
        return element.TextRunProperties.ForegroundBrush is ISolidColorBrush brush ? brush.Color.ToUInt32() : null;
    }

    [AvaloniaFact]
    public async Task NuGets_rows_draw_no_text_over_text_and_show_their_versions_as_badges()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("Packages");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Packages.slnx"));
        await ShellFixture.PumpUntil(
            () => f.Shell.Solutions.Projects.Count == 4 && f.Shell.Solutions.Progress is { Completed: true }
                  && f.Shell.Solutions.RestoreState == "succeeded" && f.Shell.Solutions.Projects.Values.Count(p => p.Restored) >= 3,
            seconds: 180);
        var plain = Path.Combine(folder, "src", "Plain", "Plain.csproj");
        f.Shell.OpenPackageManager(plain);
        await ShellFixture.PumpUntil(() => f.Shell.Layout.ActiveDocument is PackageManagerDocument);
        var document = (PackageManagerDocument)f.Shell.Layout.ActiveDocument!;
        var vm = document.ViewModel;
        await ShellFixture.PumpUntil(() => vm.Sources.Contains("local"));
        vm.SelectedSource = "local";
        vm.Search = "Codalus.Sample";
        await ShellFixture.PumpUntil(() => vm.Rows.Any(r => r is { Id: "Codalus.Sample.Greeting", InstalledVersion: not null }) && !vm.IsLoading, seconds: 60);

        // The installed version that has an update: its badge, then the update's in the accent; the others a plain one.
        var greeting = vm.Rows.Single(r => r.Id == "Codalus.Sample.Greeting");
        Assert.True(greeting is { IsInstalled: true, IsUpdatable: true, IsUpToDate: false, IsAvailable: false, UpdateLabel: "↑ 1.1.0" });
        Assert.Contains("update available", greeting.AccessibleName, StringComparison.Ordinal);
        Assert.Contains(vm.Rows, r => r is { IsHeader: false, IsAvailable: true });
        Assert.All(vm.Rows.Where(r => !r.IsHeader), r => Assert.InRange(r.AvatarColor, 0, 4));
        Assert.Equal(greeting.AvatarColor, new PackageRowViewModel("Codalus.Sample.Greeting").AvatarColor); // the same on every run

        // At the gallery's size and at a document's minimum, in every theme: no text over text, nothing cut off, contrast.
        var problems = new List<string>();
        foreach (var size in new[] { new Size(772, 483), new Size(DocumentMinWidth, 480) })
        {
            Snapshot($"nuget {size.Width:0}", new PackageManagerView { DataContext = vm }, size.Width, size.Height);
            var view = new PackageManagerView { DataContext = vm };
            problems.AddRange((await InspectInEveryAppliedThemeAsync(f, view, size)).Select(p => $"{size.Width:0} px: {p}"));
        }

        Assert.True(problems.Count == 0, "NuGet:\n" + string.Join('\n', problems));
    }

    [AvaloniaFact]
    public async Task Keyboard_Shortcuts_selected_command_is_in_the_selection_and_accent_colors()
    {
        await using var f = ShellFixture.Create();
        f.Shell.OpenKeyboardShortcuts();
        var vm = ((KeyboardShortcutsDocument)f.Shell.Layout.ActiveDocument!).ViewModel;
        await ShellFixture.PumpUntil(() => vm.Rows.Any(r => r.KeyLabel.Length > 0));
        vm.Selected = vm.Rows.First(r => r.KeyLabel.Length > 0);
        foreach (var (_, variant) in s_themes.Where(t => t.Variant == Avalonia.Styling.ThemeVariant.Dark || t.Variant == ThemeService.HarborLight || t.Variant == ThemeService.HighContrast))
        {
            var window = new Window { Width = 900, Height = 480, RequestedThemeVariant = variant, Content = new KeyboardShortcutsView { DataContext = vm } };
            window.Show();
            ShellFixture.Pump();
            ShellFixture.Pump();
            var item = window.GetVisualDescendants().OfType<ListBoxItem>().Single(i => i.IsSelected);
            var presenter = item.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            Color Token(string key) => window.TryFindResource(key, variant, out var value) && value is ISolidColorBrush brush ? brush.Color : default;
            Assert.Equal(Token("Shell.Selection"), (presenter.Background as ISolidColorBrush)?.Color);
            Assert.Equal(Token("Shell.Accent"), (presenter.BorderBrush as ISolidColorBrush)?.Color);
            Assert.Equal(3, presenter.BorderThickness.Left);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_preview_of_changes_takes_three_quarters_of_the_window_and_fits_a_small_one()
    {
        Assert.Equal(new Size(960, 600), DialogView.PreviewSize(new Size(1280, 800)));
        Assert.Equal(new Size(640, 400), DialogView.PreviewSize(new Size(800, 500))); // the minimum where there is room
        Assert.Equal(new Size(568, 368), DialogView.PreviewSize(new Size(600, 400))); // a small window less its margins

        // Over a 1280 × 800 window: the preview at its size, its changes filling what the rest leaves.
        var dialog = new DialogViewModel();
        var shown = dialog.ShowAsync(new DialogRequest("Preview Changes: Rename", "2 changes in 2 files.", ["Apply", "Cancel"])
        {
            Details = [new DetailLine("src/Greeter.cs", "header"), new DetailLine("- public sealed class Greeter", "removed"), new DetailLine("+ public sealed class Welcomer", "added"), new DetailLine("  {")],
        });
        var view = new DialogView { DataContext = dialog, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var window = new Window { Width = 1280, Height = 800, Content = new Panel { Children = { view } } };
        window.Show();
        ShellFixture.Pump();
        ShellFixture.Pump();
        var frame = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Frame");
        Assert.Equal(new Size(960, 600), frame.Bounds.Size);
        var changes = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "ChangesBox");
        Assert.True(changes.Bounds.Height > 400, $"the changes are {changes.Bounds.Height} px high in a 600 px preview");

        // Other forms keep their size: a name to type is as wide as its content, at most 560 px, and not tall.
        dialog.Cancel();
        _ = dialog.ShowAsync(new DialogRequest("Rename", "The new name:", ["Rename", "Cancel"]) { Input = "Greeter" });
        ShellFixture.Pump();
        ShellFixture.Pump();
        Assert.True(frame.Bounds.Width <= 560 && frame.Bounds.Height < 300, $"the name's dialog is {frame.Bounds.Size}");
        dialog.Cancel();
        window.Close();
        Assert.True(shown.IsCompleted);
    }
}
