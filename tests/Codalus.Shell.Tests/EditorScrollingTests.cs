using Avalonia.Headless.XUnit;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Range = Codalus.LanguageServices.Protocol.Range;

namespace Codalus.Shell.Tests;

/// <summary>
/// What scrolls a text editor without the user: Peek Definition's and the Code Definition window's viewer, and the
/// scroll benchmark. (AvaloniaEdit's TextEditor.ScrollToVerticalOffset doesn't move the scroll viewer: they didn't.)
/// </summary>
public class EditorScrollingTests
{
    private static readonly string s_text = string.Concat(Enumerable.Range(1, 300).Select(i => $"// line {i}\n"));

    private static Range At(int line) => new(new Position(line - 1, 0), new Position(line - 1, 4));

    /// <summary>The first line whose top is in view (the text view also lays out the line above it).</summary>
    private static int FirstShownLine(CodeViewer viewer)
    {
        var view = viewer.Editor.TextArea.TextView;
        return view.VisualLines.First(line => line.VisualTop >= view.VerticalOffset - 0.5).FirstDocumentLine.LineNumber;
    }

    private static async Task<(ShellFixture F, CodeViewer Viewer, Avalonia.Controls.Window Window)> HostAsync()
    {
        var f = ShellFixture.Create();
        var viewer = new CodeViewer(f.Shell, f.Shell.EditorFont);
        var window = new Avalonia.Controls.Window { Content = viewer, Width = 400, Height = 260 };
        window.Show();
        viewer.ShowPlain(string.Empty);
        await ShellFixture.PumpUntil(() => viewer.Editor.TextArea.TextView.VisualLinesValid);
        return (f, viewer, window);
    }

    private static async Task SettleAsync(CodeViewer viewer, Avalonia.Controls.Window window)
    {
        // The viewer scrolls at background priority, after the layout that shows the text.
        var until = DateTime.UtcNow.AddMilliseconds(300);
        await ShellFixture.PumpUntil(() => DateTime.UtcNow >= until);
        ShellFixture.RenderFrame(window);
        await ShellFixture.PumpUntil(() => viewer.Editor.TextArea.TextView.VisualLinesValid);
    }

    [AvaloniaFact]
    public async Task The_definition_shows_with_three_lines_of_context_above_it()
    {
        var (f, viewer, window) = await HostAsync();
        await using var _ = f;
        viewer.Show(s_text, "plaintext", At(150));
        await SettleAsync(viewer, window);
        Assert.Equal(147, FirstShownLine(viewer));
        Assert.Equal(150, viewer.Editor.TextArea.Caret.Line);

        // Another definition further up the same text.
        viewer.Show(s_text, "plaintext", At(40));
        await SettleAsync(viewer, window);
        Assert.Equal(37, FirstShownLine(viewer));
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_scroll_benchmark_scrolls_the_editor_at_its_speed()
    {
        await using var f = ShellFixture.Create();
        var path = Path.Combine(f.UserData, "long.cs");
        await File.WriteAllTextAsync(path, string.Concat(Enumerable.Range(0, 20_000).Select(i => $"// line {i}\n")), TestContext.Current.CancellationToken);
        await f.Shell.OpenFileAsync(path, preview: false, null);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == path && f.Shell.ActiveEditor.Controller is not null);
        var editor = f.Shell.ActiveEditor!.Controller!.Editor;
        await ShellFixture.PumpUntil(() => editor.TextArea.TextView.VisualLinesValid);

        // 4,000 pixels a second.
        foreach (var seconds in new[] { 0.25, 1.0, 2.5 })
        {
            Codalus.Shell.Infrastructure.BenchDriver.ScrollFrame(editor, TimeSpan.FromSeconds(seconds));
            ShellFixture.Pump();
            ShellFixture.RenderFrame(f.Window);
            Assert.Equal(seconds * 4000, editor.TextArea.TextView.VerticalOffset, 1);
        }
    }
}
