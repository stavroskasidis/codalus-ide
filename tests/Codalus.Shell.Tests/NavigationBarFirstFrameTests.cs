using Avalonia.Headless.XUnit;
using Codalus.Shell.Editor;

namespace Codalus.Shell.Tests;

/// <summary>
/// The navigation bar shows from an editor's first frame when its language service will list types, so the code doesn't
/// move down when they come (the maintainer's report, after the CodeLens rows of M56): the C# host's other languages
/// here (Visual Basic, XAML, C# scripts), with the real host. C# itself is <c>NavigationDepthTests</c>' CodeLens test;
/// F#, JavaScript and TypeScript, and style sheets are their tests' (<c>FSharpShellTests</c>, <c>WebLanguagesTests</c>).
/// </summary>
public sealed class NavigationBarFirstFrameTests
{
    /// <summary>
    /// Opens a file and records, in every frame from its editor's creation, whether the navigation bar showed, and
    /// <paramref name="rows"/> when given (whether CodeLens rows were there); waits until the service listed its symbols,
    /// and with <paramref name="rows"/> its lenses.
    /// </summary>
    internal static async Task<(List<bool> Frames, NavigationBar Bar, CodeEditorController Editor, List<bool> Rows)> OpenRecordingBarAsync(
        ShellFixture f, string path, int seconds = 120, Func<CodeEditorController, bool>? rows = null)
    {
        var frames = new List<bool>();
        var rowFrames = new List<bool>();
        CodeEditorController? hooked = null;
        void Hook(CodeEditorDocument document)
        {
            var editor = hooked = document.Controller!;
            editor.Editor.TextArea.TextView.VisualLinesChanged += (_, _) =>
            {
                frames.Add(document.View?.NavigationBar.IsVisible == true);
                if (rows is not null)
                {
                    rowFrames.Add(rows(editor));
                }
            };
        }

        void OnActive(object? sender, Docking.ShellDocument? document)
        {
            if (hooked is null && document is CodeEditorDocument opened && string.Equals(opened.Model.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                if (opened.Controller is not null)
                {
                    Hook(opened);
                }
                else
                {
                    opened.ControllerAttached += (_, _) =>
                    {
                        if (hooked is null)
                        {
                            Hook(opened);
                        }
                    };
                }
            }
        }

        f.Shell.Layout.ActiveDocumentChanged += OnActive;
        try
        {
            var document = await f.Shell.OpenFileAsync(path, preview: false, null);
            await ShellFixture.PumpUntil(() => document?.Controller is not null && hooked is not null);
            var editor = document!.Controller!;
            await ShellFixture.PumpUntil(() => editor.Insights.SymbolsVersion == editor.Model.Version, seconds,
                () => $"no symbols for {Path.GetFileName(path)}; expected types: {editor.Insights.TypesExpected}");
            if (rows is not null)
            {
                await ShellFixture.PumpUntil(() => !editor.Insights.HasLensEstimates, seconds, () => $"no lenses for {Path.GetFileName(path)}: {editor.Insights.DescribeLenses()}");
            }

            ShellFixture.Pump();
            editor.Editor.TextArea.TextView.EnsureVisualLines();
            return (frames, document.View!.NavigationBar, editor, rowFrames);
        }
        finally
        {
            f.Shell.Layout.ActiveDocumentChanged -= OnActive;
        }
    }

    [AvaloniaFact]
    public async Task The_bar_of_xaml_and_csharp_scripts_shows_from_the_first_frame()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;
        var folder = await f.OpenLanguageFeaturesAsync();
        var files = new (string Name, string Text, bool Types)[]
        {
            ("Window.axaml", "<?xml version=\"1.0\"?>\n<!-- a window -->\n<Window xmlns=\"https://github.com/avaloniaui\">\n  <Grid>\n    <Button x:Name=\"Ok\"/>\n  </Grid>\n</Window>\n", true),
            ("Page.xaml", "<Page xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">\n  <StackPanel/>\n</Page>\n", true),
            ("Script.csx", "using System;\n\npublic class Greeter\n{\n    public string Hello() => \"hi\";\n}\n\nConsole.WriteLine(new Greeter().Hello());\n", true),
            ("Statements.csx", "using System;\n\nvar name = \"world\";\nConsole.WriteLine($\"Hello, {name}\");\n", false),
        };

        foreach (var (name, text, types) in files)
        {
            var path = Path.Combine(folder, "src", "Lib", name);
            await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
            var (frames, bar, editor, _) = await OpenRecordingBarAsync(f, path);
            Assert.True(bar.IsVisible == types, $"{name}: the bar {(bar.IsVisible ? "shows" : "doesn't show")} with {bar.Types.Count} types once the symbols came");
            Assert.True(bar.HasTypes == types, $"{name}: {bar.Types.Count} types listed");
            Assert.NotEmpty(frames);
            Assert.True(frames.TrueForAll(shown => shown == types), $"{name}: the bar showed in {frames.Count(s => s)} of {frames.Count} frames before and after the symbols came");
            Assert.False(editor.Insights.TypesExpected);
        }
    }

    [AvaloniaFact]
    public async Task The_bar_of_visual_basic_shows_from_the_first_frame()
    {
        await using var f = ShellFixture.Create();
        f.Window.Width = 1200;
        f.Window.Height = 800;
        var folder = f.CopySample("VisualBasic");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "VisualBasic.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 4 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 240);

        // A class (Product.vb) and a module without types (Program.vb): the bar shows for the first from its first
        // frame, and never for the second.
        foreach (var (file, types) in new[] { (Path.Combine("VbLibrary", "Product.vb"), true), (Path.Combine("VbConsole", "Program.vb"), false) })
        {
            var (frames, bar, _, _) = await OpenRecordingBarAsync(f, Path.Combine(folder, file), seconds: 240);
            Assert.Equal(types, bar.HasTypes);
            Assert.NotEmpty(frames);
            Assert.True(frames.TrueForAll(shown => shown == types), $"{file}: the bar showed in {frames.Count(s => s)} of {frames.Count} frames");
        }
    }
}
