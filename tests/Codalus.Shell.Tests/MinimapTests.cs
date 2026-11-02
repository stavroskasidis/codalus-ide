using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Codalus.Shell.Editor;

namespace Codalus.Shell.Tests;

/// <summary>The minimap scrolls the editor to where it's clicked, and drags the visible area like a scroll bar's thumb.</summary>
public class MinimapTests
{
    private static async Task<(ShellFixture F, CodeEditorController Editor, Minimap Minimap)> OpenAsync(int lines)
    {
        var f = ShellFixture.Create();
        var path = Path.Combine(f.UserData, "lines.txt");

        // Short lines: most of the minimap draws nothing, which a click must hit all the same.
        await File.WriteAllTextAsync(path, string.Concat(Enumerable.Range(0, lines).Select(i => $"x{i % 10}\n")), TestContext.Current.CancellationToken);
        await f.Shell.OpenFileAsync(path, preview: false, null);
        await ShellFixture.PumpUntil(() => f.Shell.ActiveEditor?.Model.Path == path && f.Shell.ActiveEditor.Controller is not null);
        var editor = f.Shell.ActiveEditor!.Controller!;
        var minimap = f.Shell.ActiveEditor.View!.Minimap;
        await ShellFixture.PumpUntil(() =>
        {
            ShellFixture.RenderFrame(f.Window);
            return minimap.IsEffectivelyVisible && minimap.Bounds.Height > 100 && minimap.VisibleArea is not null;
        });
        return (f, editor, minimap);
    }

    private static Point At(ShellFixture f, Minimap minimap, double y) =>
        minimap.TranslatePoint(new Point(minimap.Bounds.Width - 8, y), f.Window)!.Value;

    private static void Settle(ShellFixture f)
    {
        ShellFixture.Pump();
        ShellFixture.RenderFrame(f.Window);
        ShellFixture.Pump();
    }

    [AvaloniaTheory]
    [InlineData(150)] // the whole file fits in the minimap
    [InlineData(5_000)] // the minimap scrolls with the editor
    public async Task Clicking_an_empty_part_of_the_minimap_scrolls_the_editor_there(int lines)
    {
        var (f, editor, minimap) = await OpenAsync(lines);
        await using var _ = f;
        var view = editor.Editor.TextArea.TextView;
        Assert.Equal(0, view.VerticalOffset);

        // Below the visible area, at the right where the short lines leave nothing drawn.
        var y = Math.Min(minimap.Bounds.Height - 4, lines * Minimap.LineHeight * 0.75);
        var point = At(f, minimap, y);
        Assert.Same(minimap, f.Window.InputHitTest(point));
        f.Window.MouseDown(point, MouseButton.Left);
        f.Window.MouseUp(point, MouseButton.Left);
        Settle(f);

        Assert.True(view.VerticalOffset > 0, "The editor didn't scroll.");
        if (lines == 150)
        {
            // The clicked line is in the middle of the editor.
            var clicked = (int)(y / Minimap.LineHeight) + 1;
            var first = view.VisualLines[0].FirstDocumentLine.LineNumber;
            var last = view.VisualLines[^1].LastDocumentLine.LineNumber;
            Assert.InRange(clicked, first + 2, last - 2);
        }
    }

    [AvaloniaTheory]
    [InlineData(150)]
    [InlineData(5_000)]
    public async Task Dragging_the_visible_area_moves_it_with_the_pointer(int lines)
    {
        var (f, editor, minimap) = await OpenAsync(lines);
        await using var _ = f;
        var area = minimap.VisibleArea!.Value;
        Assert.Equal(0, area.Top);

        // Held near its bottom edge, the visible area follows the pointer without jumping to center on it.
        var grab = area.Height - 3;
        var start = At(f, minimap, grab);
        f.Window.MouseDown(start, MouseButton.Left);
        Settle(f);
        Assert.Equal(0, editor.Editor.TextArea.TextView.VerticalOffset);

        foreach (var dy in new[] { 10.0, 30.0, 60.0 })
        {
            f.Window.MouseMove(At(f, minimap, grab + dy), RawInputModifiers.LeftMouseButton);
            Settle(f);
            Assert.InRange(minimap.VisibleArea!.Value.Top, dy - 3, dy + 3);
        }

        f.Window.MouseUp(At(f, minimap, grab + 60), MouseButton.Left);
        Settle(f);

        // Released, moving the pointer scrolls no more.
        var offset = editor.Editor.TextArea.TextView.VerticalOffset;
        f.Window.MouseMove(At(f, minimap, grab + 90), RawInputModifiers.None);
        Settle(f);
        Assert.Equal(offset, editor.Editor.TextArea.TextView.VerticalOffset);
    }
}
