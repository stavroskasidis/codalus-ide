using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Codalus.Shell.Editor;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// <c>editor.codeLensStyle</c> (Options → Environment → Appearance): the popup style's counts (the default) read usages
/// and inheritors and list their results in a popup under the count; the References window style's open that window.
/// A click runs the title under the pointer, not the row's first.
/// </summary>
public class CodeLensStyleTests
{
    [Theory]
    [InlineData("3 references", "references", "public class ", false, "3 references")]
    [InlineData("3 references", "references", "public class ", true, "3 usages")]
    [InlineData("1 reference", "references", "    public void ", true, "1 usage")]
    [InlineData("2 implementations", "implementations", "public abstract class ", true, "2 inheritors")]
    [InlineData("1 implementation", "implementations", "public record ", true, "1 inheritor")]
    [InlineData("2 implementations", "implementations", "public interface ", true, "2 implementations")]
    [InlineData("2 implementations", "implementations", "    public virtual string ", true, "2 implementations")]
    [InlineData("2 implementations", "implementations", "public abstract class ", false, "2 implementations")]
    [InlineData("Ada, 3 days ago", null, "public class ", true, "Ada, 3 days ago")] // not the C# service's
    public void Titles_follow_the_style(string title, string? kind, string declaration, bool popups, string expected) =>
        Assert.Equal(expected, LensTitles.Display(title, kind, declaration, popups));

    [Theory]
    [InlineData("references", "public class ", LensSubject.Usages)]
    [InlineData("implementations", "public abstract class ", LensSubject.DerivedTypes)]
    [InlineData("implementations", "[Serializable] public record class ", LensSubject.DerivedTypes)]
    [InlineData("implementations", "public interface ", LensSubject.Implementations)]
    [InlineData("implementations", "    public abstract double ", LensSubject.MemberImplementations)]
    [InlineData(null, "public class ", LensSubject.Other)]
    public void Subjects_come_from_the_declaration(string? kind, string declaration, LensSubject expected) =>
        Assert.Equal(expected, LensTitles.Subject(kind, declaration));

    private static async Task<CodeEditorController> OpenShapesAsync(ShellFixture f)
    {
        var folder = await f.OpenLanguageFeaturesAsync();
        var document = await f.Shell.OpenFileAsync(Path.Combine(folder, "src", "Lib", "Shapes.cs"));
        Assert.NotNull(document);
        await ShellFixture.PumpUntil(() => document.Controller is not null);
        return document.Controller!;
    }

    private static int LineOf(CodeEditorController editor, string text) =>
        editor.Model.Text.GetLineByOffset(editor.Model.Text.Text.IndexOf(text, StringComparison.Ordinal)).LineNumber;

    /// <summary>Clicks the first title that contains <paramref name="title"/> on the CodeLens row above <paramref name="line"/>.</summary>
    private static async Task ClickTitleAsync(ShellFixture f, CodeEditorController editor, int line, string title)
    {
        Avalonia.Point? at = null;
        await ShellFixture.PumpUntil(() => (at = editor.Insights.LensTitlePoint(line, title)) is not null, seconds: 60, describe: () => $"row '{editor.Insights.LensRowAbove(line)}'");
        var click = Avalonia.VisualExtensions.TranslatePoint(editor.Editor.TextArea.TextView, at!.Value, f.Window)!.Value;
        f.Window.MouseDown(click, MouseButton.Left);
        f.Window.MouseUp(click, MouseButton.Left);
    }

    [AvaloniaFact]
    public async Task The_window_style_lists_a_classes_derived_types_in_the_References_window()
    {
        await using var f = ShellFixture.Create();
        await f.Shell.Workbench.Settings.SetAsync(EditorOptions.CodeLensStyleKey, CodeLensStyles.Window, SettingScope.User, TestContext.Current.CancellationToken);
        f.Window.Width = 1200;
        f.Window.Height = 1600;
        var shapes = await OpenShapesAsync(f);
        var shapeLine = LineOf(shapes, "public abstract class Shape");
        await ShellFixture.PumpUntil(() => shapes.Insights.LensRowAbove(shapeLine) is { } row && row.Contains("2 implementations", StringComparison.Ordinal), seconds: 60);

        // The second title, not the row's first (the references): the click runs the lens under the pointer.
        var references = 0;
        f.Shell.Workbench.Commands.Executed += (_, e) => references += e.CommandId is "editor.action.showReferences" ? 1 : 0;
        await ClickTitleAsync(f, shapes, shapeLine, "2 implementations");
        await ShellFixture.PumpUntil(() => f.Shell.References.ShowsImplementations && f.Shell.References.HasResults, describe: () => f.Shell.References.Title);
        Assert.Equal("'Shape' implementations", f.Shell.References.Title);
        Assert.Equal(3, f.Shell.References.Rows.Count(r => !r.IsHeader)); // Circle, and Square's two parts
        Assert.Equal(0, references);
        Assert.Null(f.Shell.ActiveEditor!.View!.LensResults);

        // The first title still finds the references.
        await ClickTitleAsync(f, shapes, shapeLine, "references");
        await ShellFixture.PumpUntil(() => references > 0 || (!f.Shell.References.ShowsImplementations && f.Shell.References.HasResults));
        await ShellFixture.PumpUntil(() => !f.Shell.References.ShowsImplementations, describe: () => f.Shell.References.Title);
        Assert.Equal("'Shape' references", f.Shell.References.Title);
    }

    [AvaloniaFact]
    public async Task The_popup_style_is_the_default_and_lists_derived_types_and_usages_under_the_count()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 1600;
        Assert.True(f.Shell.EditorOptions.CodeLensPopups); // nothing set: the popup style
        var shapes = await OpenShapesAsync(f);
        var shapeLine = LineOf(shapes, "public abstract class Shape");
        var interfaceLine = LineOf(shapes, "public interface IShape");
        await ShellFixture.PumpUntil(() => shapes.Insights.LensRowAbove(shapeLine) is { } row && row.Contains("2 inheritors", StringComparison.Ordinal), seconds: 60,
            describe: () => $"row '{shapes.Insights.LensRowAbove(shapeLine)}'");
        Assert.Contains("usages", shapes.Insights.LensRowAbove(shapeLine), StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => shapes.Insights.LensRowAbove(interfaceLine) is { } row && row.Contains("3 implementations", StringComparison.Ordinal), seconds: 60);

        // Inheritors: Shape's derived types, with their namespace and project.
        var view = f.Shell.ActiveEditor!.View!;
        await ClickTitleAsync(f, shapes, shapeLine, "2 inheritors");
        await ShellFixture.PumpUntil(() => view.LensResults is { IsOpen: true });
        var popup = view.LensResults!;
        Assert.Equal("Derived types of 'Shape'", popup.Title);
        Assert.Equal(["Circle", "Square"], popup.Items.Select(i => i.Name));
        Assert.All(popup.Items, i => Assert.Equal(("(in Lib)", "Lib"), (i.Detail, i.Project)));
        Assert.False(f.Shell.References.HasResults); // not the window

        // Down and Enter open the second one, and close the popup.
        f.Press(Key.Down);
        f.Press(Key.Enter);
        await ShellFixture.PumpUntil(() => !popup.IsOpen, describe: () => $"focused {f.Window.FocusManager?.GetFocusedElement()} in {(f.Window.FocusManager?.GetFocusedElement() as Avalonia.Visual)?.GetType()}, popup focus {Avalonia.Controls.TopLevel.GetTopLevel(popup.Popup.Child)?.FocusManager?.GetFocusedElement()}");
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Controller is { } active && CaretLine(active).Contains("class Square", StringComparison.Ordinal),
            describe: () => f.Shell.ActiveEditor?.Controller is { } active ? CaretLine(active) : "no editor");

        // Usages: IShape's references (not its declaration), each line's code, file and project.
        await f.Shell.OpenFileAsync(shapes.Model.Path!);
        await ClickTitleAsync(f, shapes, interfaceLine, "2 usages");
        await ShellFixture.PumpUntil(() => popup.IsOpen && popup.Title == "Usages of 'IShape'", describe: () => popup.Title);
        Assert.Equal(2, popup.Items.Count); // Shape's base list and Geometry.Scale's parameter
        Assert.Contains(popup.Items, i => i.Name.Contains("abstract class Shape : IShape", StringComparison.Ordinal) && i.Detail!.StartsWith("Shapes.cs:", StringComparison.Ordinal));

        // The header's button shows them in the References window instead.
        popup.OpenWindow();
        await ShellFixture.PumpUntil(() => f.Shell.References.HasResults);
        Assert.False(popup.IsOpen);
        Assert.Equal("'IShape' references", f.Shell.References.Title);

        // Implementations of an interface: the popup's other title.
        await ClickTitleAsync(f, shapes, interfaceLine, "3 implementations");
        await ShellFixture.PumpUntil(() => popup.IsOpen && popup.Title == "Implementations of 'IShape'", describe: () => popup.Title);
        Assert.Contains(popup.Items, i => i.Name == "Shape");
        f.Press(Key.Escape);
        await ShellFixture.PumpUntil(() => !popup.IsOpen);
    }

    private static string CaretLine(CodeEditorController editor)
    {
        var line = editor.Model.Text.GetLineByOffset(editor.Editor.CaretOffset);
        return editor.Model.Text.GetText(line.Offset, line.Length);
    }
}
