using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Codalus.Shell.Theming;

namespace Codalus.Shell.Tests;

/// <summary>Razor files colored as the classic IDE colors them: the C# host's Razor tokens over the grammar, in every theme.</summary>
public sealed class RazorColorTests
{
    /// <summary>The maintainer's Routes.razor, reduced.</summary>
    private const string Routes = """
        @using Microsoft.AspNetCore.Components.Authorization
        @inject ILogger<Routes> Logger
        @* A Razor comment *@
        <Router AppAssembly="@typeof(Program).Assembly">
            <Found Context="routeData">
                <RouteView RouteData="@routeData" DefaultLayout="@typeof(Layout.MainLayout)" />
            </Found>
        </Router>
        @if (Logger is not null)
        {
            <p class="note">Hello @Logger</p>
        }

        """;

    /// <summary>The rendered color of the character at <paramref name="marker"/> plus <paramref name="delta"/>.</summary>
    private static FontWeight? WeightAt(CodeEditorController editor, string marker)
    {
        var offset = editor.Model.Text.Text.IndexOf(marker, StringComparison.Ordinal);
        var line = editor.Model.Text.GetLineByOffset(offset);
        var visual = editor.Editor.TextArea.TextView.GetOrConstructVisualLine(line);
        var column = offset - line.Offset;
        return visual.Elements.FirstOrDefault(e => e.RelativeTextOffset <= column && column < e.RelativeTextOffset + Math.Max(1, e.DocumentLength))?.TextRunProperties.Typeface.Weight;
    }

    private static Color? ColorAt(CodeEditorController editor, string marker, int delta = 0)
    {
        var text = editor.Model.Text.Text;
        var offset = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"'{marker}' not found");
        var line = editor.Model.Text.GetLineByOffset(offset + delta);
        var column = offset + delta - line.Offset;
        var view = editor.Editor.TextArea.TextView;
        view.EnsureVisualLines();
        var visual = view.GetOrConstructVisualLine(line);
        return visual.Elements.FirstOrDefault(e => e.RelativeTextOffset <= column && column < e.RelativeTextOffset + Math.Max(1, e.DocumentLength))
            ?.TextRunProperties.ForegroundBrush is ISolidColorBrush brush ? brush.Color : null;
    }

    private static Color Of(uint argb) => Color.FromUInt32(argb);

    [AvaloniaFact]
    public async Task Razor_markup_uses_razor_colors_over_the_grammar_with_line_numbers()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1400;
        f.Window.Height = 900;
        var folder = f.CopySample("Blazor");
        var routes = Path.Combine(folder, "BlazorApp", "Components", "Routes.razor");
        await File.WriteAllTextAsync(routes, Routes, TestContext.Current.CancellationToken);
        await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, "dark", Workbench.Settings.SettingScope.User, CancellationToken.None);
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Blazor.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 120);
        await ShellFixture.PumpUntil(() => f.Shell.Languages.Status(ShellHost.CSharpLanguageId) is { State: WorkspaceStates.Ready, Projects: 1 }, seconds: 180);

        var document = await f.Shell.OpenFileAsync(routes, preview: false, null);
        await ShellFixture.PumpUntil(() => document?.Controller is not null);
        var editor = document!.Controller!;
        Assert.Equal(ShellHost.RazorLanguageId, editor.Model.LanguageId);
        Assert.NotNull(editor.GrammarScope); // the grammar colors the file until the tokens come, and where they have none

        // The maintainer's report: components orange-red (the grammar's invalid tags) like HTML; Razor comments gray.
        var dark = EditorTheme.Dark;
        await ShellFixture.PumpUntil(() => ColorAt(editor, "<Router", 1) == Of(0xFF4EC9B0), seconds: 120);
        Assert.Equal(Of(0xFF4EC9B0), ColorAt(editor, "<Found", 1)); // a component, teal like a type
        Assert.Equal(Of(0xFF4EC9B0), ColorAt(editor, "</Router", 2));
        Assert.Equal(Of(0xFF4EC9B0), ColorAt(editor, "AppAssembly")); // its parameter: the component's teal, bold (M50, after 0.7.0)
        Assert.Equal(FontWeight.Bold, WeightAt(editor, "AppAssembly"));
        Assert.NotEqual(FontWeight.Bold, WeightAt(editor, "class="));
        Assert.Equal(Of(0xFF808080), ColorAt(editor, "<Router")); // delimiters gray
        Assert.Equal(Of(0xFF569CD6), ColorAt(editor, "<p", 1)); // an HTML element blue
        Assert.Equal(Of(0xFF9CDCFE), ColorAt(editor, "class=")); // its attribute
        Assert.Equal(Of(0xFF57A64A), ColorAt(editor, "A Razor comment")); // Razor comments green (the classic IDE's, D47)
        Assert.Equal(Of(0xFF57A64A), ColorAt(editor, "@*"));
        Assert.Equal(Of(0xFFA699E6), ColorAt(editor, "@using")); // the transition and the directive
        Assert.Equal(Of(0xFFA699E6), ColorAt(editor, "@using", 1));
        Assert.Equal(Of(dark.TokenColor("namespace")!.Value), ColorAt(editor, "Microsoft.AspNetCore")); // a namespace in plain foreground
        Assert.Equal(Of(dark.TokenColor("interface")!.Value), ColorAt(editor, "ILogger<"));
        Assert.Equal(Of(dark.TokenColor("controlKeyword")!.Value), ColorAt(editor, "@if", 1));
        Assert.Equal(Of(dark.TokenColor("keyword")!.Value), ColorAt(editor, "@typeof", 1)); // C# inside an attribute value

        // Line numbers, as in C# documents.
        Assert.True(editor.Editor.ShowLineNumbers);
        var numbers = editor.Editor.TextArea.LeftMargins.OfType<AvaloniaEdit.Editing.LineNumberMargin>().Single();
        Assert.True(numbers.IsEffectivelyVisible && numbers.Bounds.Width > 0, $"line numbers: visible {numbers.IsEffectivelyVisible}, width {numbers.Bounds.Width}");
        NavigationDepthTests.Screenshot(f, "razor-colors-dark");

        // Light: the classic IDE's Light colors.
        await f.Shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, "light", Workbench.Settings.SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => ColorAt(editor, "<Router", 1) == Of(0xFF0B7F7F), seconds: 30);
        Assert.Equal(Of(0xFF800000), ColorAt(editor, "<p", 1));
        Assert.Equal(Of(0xFF008000), ColorAt(editor, "A Razor comment"));
        NavigationDepthTests.Screenshot(f, "razor-colors-light");
    }
}
