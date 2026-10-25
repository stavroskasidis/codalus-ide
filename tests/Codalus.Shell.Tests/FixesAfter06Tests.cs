using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.Shell.Editor;

namespace Codalus.Shell.Tests;

/// <summary>M50 (0.7): the maintainer's list after the 0.6.0 release.</summary>
public class FixesAfter06Tests
{
    /// <summary>
    /// Navigate Backward and Forward did nothing after ordinary navigation: only Go To Definition and the like made a
    /// step. Leaving a document and a caret move of more than ten lines now do, as in the classic IDE, and the mouse's
    /// back and forward buttons run them anywhere in the window.
    /// </summary>
    [AvaloniaFact]
    public async Task Back_and_forward_follow_documents_and_long_caret_moves_and_the_mouse_buttons()
    {
        await using var f = ShellFixture.Create();
        var first = Path.Combine(f.UserData, "First.txt");
        var second = Path.Combine(f.UserData, "Second.txt");
        await File.WriteAllTextAsync(first, string.Join('\n', Enumerable.Range(1, 60).Select(i => $"first {i}")));
        await File.WriteAllTextAsync(second, string.Join('\n', Enumerable.Range(1, 60).Select(i => $"second {i}")));

        async Task<CodeEditorController> OpenAsync(string path)
        {
            var document = await f.Shell.OpenFileAsync(path, preview: false);
            Assert.NotNull(document);
            await ShellFixture.PumpUntil(() => document.Controller is not null && f.Shell.ActiveEditor == document);
            return document.Controller!;
        }

        (string Name, int Line) Where()
        {
            var editor = f.Shell.ActiveEditor!;
            return (editor.Model.Name, editor.Controller!.Caret.Line);
        }

        var a = await OpenAsync(first);
        a.Editor.CaretOffset = a.Model.Text.GetLineByNumber(3).Offset;
        ShellFixture.Pump();
        a.Editor.CaretOffset = a.Model.Text.GetLineByNumber(40).Offset; // more than ten lines: a step
        ShellFixture.Pump();
        a.Editor.CaretOffset = a.Model.Text.GetLineByNumber(42).Offset; // two lines: not one
        ShellFixture.Pump();
        await OpenAsync(second);

        // The toolbar's arrow, then the mouse's back button over the editor.
        var back = f.Window.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetName(b) == "Navigate Backward" && b.IsEffectivelyVisible);
        back.Command!.Execute(null);
        await ShellFixture.PumpUntil(() => Where() == ("First.txt", 42));
        var editorView = f.Shell.ActiveEditor!.Controller!.Editor;
        var over = editorView.TranslatePoint(new Point(40, 10), f.Window)!.Value;
        f.Window.MouseDown(over, MouseButton.XButton1);
        f.Window.MouseUp(over, MouseButton.XButton1);
        await ShellFixture.PumpUntil(() => Where() == ("First.txt", 3));

        // Forward retraces them, the mouse's forward button too.
        f.Window.MouseDown(over, MouseButton.XButton2);
        f.Window.MouseUp(over, MouseButton.XButton2);
        await ShellFixture.PumpUntil(() => Where() == ("First.txt", 42));
        await f.RunAsync("workbench.navigateForward");
        await ShellFixture.PumpUntil(() => Where().Name == "Second.txt");

        // A new step after going back drops what was ahead.
        await f.RunAsync("workbench.navigateBack");
        await ShellFixture.PumpUntil(() => Where() == ("First.txt", 42));
        await OpenAsync(second);
        await f.RunAsync("workbench.navigateForward");
        ShellFixture.Pump();
        Assert.Equal("Second.txt", Where().Name);
    }
}
