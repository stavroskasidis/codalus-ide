using Avalonia;
using Avalonia.Controls;
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

    /// <summary>
    /// What is over the minimap where the click goes: the element hit, with its ancestors (type, name, classes, what it
    /// shows) up to the window, and the notifications on screen. Once, in the Mac's full suite (eng/validate.sh,
    /// 2026-11-24, docs/ci-flakes.md), a Border with the panels' background was hit instead, and the assertion's own
    /// text didn't say whose.
    /// </summary>
    private static string Covered(ShellFixture f, Minimap minimap, object? hit, Point point)
    {
        var chain = new List<string>();
        for (var visual = hit as Visual; visual is not null && chain.Count < 40; visual = visual.GetVisualParent())
        {
            var control = visual as Control;
            var classes = control is { Classes.Count: > 0 } ? "." + string.Join('.', control.Classes) : string.Empty;
            var name = string.IsNullOrEmpty(control?.Name) ? string.Empty : "#" + control!.Name;
            var shows = control?.DataContext is { } context ? $" [{context.GetType().Name}]" : string.Empty;
            chain.Add($"{visual.GetType().Name}{name}{classes}{shows} {visual.Bounds}");
        }

        // An element behind the minimap (one of its own ancestors) means the minimap itself wasn't hit there, not that
        // something lies over it.
        var behind = hit is Visual element && minimap.GetVisualAncestors().Contains(element);
        var state = $"The minimap: bounds {minimap.Bounds}, visible {minimap.IsEffectivelyVisible}, hit-testable {minimap.IsHitTestVisible}, visible area {minimap.VisibleArea}; the element hit is {(behind ? "one of its ancestors (behind it)" : "not one of its ancestors (over it, or elsewhere)")}.";
        return $"The click at {point} doesn't reach the minimap. {state} Hit, innermost first:\n  {string.Join("\n  ", chain)}\nNotifications: "
               + string.Join(" | ", f.Shell.Workbench.Notifications.Items.Select(n => n.Message));
    }

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
        var hit = f.Window.InputHitTest(point);
        Assert.True(ReferenceEquals(minimap, hit), Covered(f, minimap, hit, point));
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

    [AvaloniaFact]
    public async Task Scrolling_reads_only_the_lines_it_brings_into_the_minimap()
    {
        // A frame of scrolling read every character the minimap shows; now only the lines it brings in.
        var (f, editor, minimap) = await OpenAsync(5_000);
        await using var _ = f;
        var view = editor.Editor.TextArea.TextView;
        Assert.InRange(minimap.LinesRead, 100, 5_000);
        foreach (var lines in new[] { 3, 6, 9 })
        {
            var before = minimap.LinesRead;
            editor.Editor.ScrollToY(lines * view.DefaultLineHeight);
            Settle(f);
            Assert.True(minimap.FirstLine > 0, "The minimap didn't scroll with the editor.");
            Assert.InRange(minimap.LinesRead - before, 1, 10);
        }

        // An edit reads them again: lines moved.
        var read = minimap.LinesRead;
        editor.Editor.Document.Insert(0, "inserted\n");
        await ShellFixture.PumpUntil(() =>
        {
            Settle(f);
            return minimap.LinesRead - read > 100;
        });
    }

    [AvaloniaFact]
    public async Task The_minimap_draws_each_word_where_it_is_on_its_line()
    {
        var (f, _, minimap) = await OpenAsync(150);
        await using var __ = f;

        // The minimap alone, not the window's frame: whatever the window draws over it (an overlay fading in under load)
        // isn't its drawing.
        using var drawn = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)minimap.Bounds.Width, (int)minimap.Bounds.Height));
        drawn.Render(minimap);
        var pixels = new uint[drawn.PixelSize.Width * drawn.PixelSize.Height];
        var pinned = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            drawn.CopyPixels(new PixelRect(drawn.PixelSize), pinned.AddrOfPinnedObject(), pixels.Length * sizeof(uint), drawn.PixelSize.Width * sizeof(uint));
        }
        finally
        {
            pinned.Free();
        }

        byte Alpha(int x, int y) => (byte)(pixels[(y * drawn.PixelSize.Width) + x] >> 24);

        // Every line is "xN": a two-column word 4 px in, only the visible area's shade to the right of it, and a gap below.
        for (var line = 0; line < 20; line++)
        {
            var y = (int)(line * Minimap.LineHeight);
            Assert.True(Alpha(5, y) > 100, $"line {line}: the word isn't drawn (alpha {Alpha(5, y)})");
            Assert.True(Alpha(40, y) <= 0x30, $"line {line}: something is drawn right of the word (alpha {Alpha(40, y)}; the visible area's shade is 0x30)");
            Assert.True(Alpha(5, y + 1) < Alpha(5, y), $"line {line}: the gap below the word (alpha {Alpha(5, y + 1)}) is drawn like it ({Alpha(5, y)})");
        }
    }
}
