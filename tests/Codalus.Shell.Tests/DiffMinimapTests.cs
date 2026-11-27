using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Codalus.Shell.Editor;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Git;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Tests;

/// <summary>
/// The diff document's minimap: on the new side and in the inline view, as the editor's minimap setting says, with the
/// added and removed lines marked where they are in the diff.
/// </summary>
public sealed class DiffMinimapTests
{
    private static readonly string s_original = string.Concat(Enumerable.Range(1, 40).Select(i => $"line {i}\n"));

    /// <summary>Lines 10 and 11 removed, two lines added after line 20, line 30 changed.</summary>
    private static readonly string s_changed = string.Concat(Enumerable.Range(1, 40).Select(i => i switch
    {
        10 or 11 => string.Empty,
        20 => "line 20\nadded A\nadded B\n",
        30 => "line 30 changed\n",
        _ => $"line {i}\n",
    }));

    [Fact]
    public void The_new_side_marks_its_added_lines_and_the_old_sides_removed_ones()
    {
        string[] oldLines = ["a", "b", "c", "d"];
        string[] newLines = ["a", "c", "x", "d2"];
        DiffRow[] rows = [new(1, 1, ' '), new(2, null, '-'), new(3, 2, ' '), new(null, 3, '+'), new(4, 4, '~')];
        var (left, right) = DiffRows.SideBySide(rows, oldLines, newLines);
        var colors = new SourceLineColorizer(_ => (null, 0));

        var newSide = new DiffMinimapColors(() => right, colors);
        Assert.Equal(
            [MinimapMark.None, MinimapMark.Removed, MinimapMark.None, MinimapMark.Added, MinimapMark.Added],
            Enumerable.Range(0, rows.Length).Select(newSide.MarkOf));

        var oldSide = new DiffMinimapColors(() => left, colors);
        Assert.Equal(
            [MinimapMark.None, MinimapMark.Removed, MinimapMark.None, MinimapMark.Added, MinimapMark.Removed],
            Enumerable.Range(0, rows.Length).Select(oldSide.MarkOf));

        var inline = DiffRows.Inline(rows, oldLines, newLines);
        var inlineColors = new DiffMinimapColors(() => inline, colors);
        Assert.Equal(
            inline.Lines.Select(l => l.Kind switch { '+' => MinimapMark.Added, '-' => MinimapMark.Removed, _ => MinimapMark.None }),
            Enumerable.Range(0, inline.Lines.Count).Select(inlineColors.MarkOf));
        Assert.Equal(MinimapMark.None, newSide.MarkOf(rows.Length));
        Assert.Equal(MinimapMark.None, newSide.MarkOf(-1));
    }

    [Fact]
    public void A_word_is_blended_over_the_band_under_it()
    {
        // Premultiplied ARGB: half-transparent red over opaque green keeps the green's opacity and mixes the two.
        Assert.Equal(0xFF_80_7F_00u, Minimap.Over(0x80_80_00_00, 0xFF_00_FF_00)); // green: 255 × 127/255
        Assert.Equal(0xFF_00_FF_00u, Minimap.Over(0, 0xFF_00_FF_00));
        Assert.Equal(0xFF_11_22_33u, Minimap.Over(0xFF_11_22_33, 0xFF_00_FF_00));
        Assert.Equal(0x80_80_00_00u, Minimap.Over(0x80_80_00_00, 0));
    }

    [Fact]
    public void Another_theme_is_another_version_of_the_editors_colors()
    {
        // The minimap keeps each line's brushes until the version changes: a theme change must redraw them in its colors.
        var colorizer = new SemanticColorizer { Theme = EditorTheme.Dark };
        var version = colorizer.Version;
        colorizer.Theme = EditorTheme.Dark;
        Assert.Equal(version, colorizer.Version);
        colorizer.Theme = EditorTheme.Light;
        Assert.NotEqual(version, colorizer.Version);
    }

    [AvaloniaFact]
    public async Task The_new_side_has_a_minimap_with_the_diffs_changes_as_the_setting_says()
    {
        var (f, _, file) = await DiffDocumentTests.RepositoryAsync(s_original);
        await using var fixture = f;
        f.Window.Width = 1500;
        await File.WriteAllTextAsync(file, s_changed, TestContext.Current.CancellationToken);
        await f.Shell.Git.RefreshAsync(TestContext.Current.CancellationToken);
        var document = await f.Shell.OpenDiffAsync(file, staged: false);
        Assert.NotNull(document);
        var vm = document.ViewModel;
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<DiffView>().Any() && vm.State == DiffState.Lines && vm.Changes.Count == 3);
        var view = f.Window.GetVisualDescendants().OfType<DiffView>().Single();

        // One minimap, at the new side's right edge: the old side's rows are the same rows.
        Assert.Null(view.MinimapOf(DiffPaneSide.Old));
        var minimap = view.MinimapOf(DiffPaneSide.New);
        Assert.NotNull(minimap);
        await ShellFixture.PumpUntil(() =>
        {
            ShellFixture.RenderFrame(f.Window);
            return minimap.IsEffectivelyVisible && minimap.Bounds.Height > vm.Rows.Count * Minimap.LineHeight;
        });
        var editor = view.EditorOf(DiffPaneSide.New);
        Assert.True(minimap.TranslatePoint(default, editor)!.Value.X >= editor.Bounds.Width - 1, "The minimap isn't right of the code.");

        // Each row's stripe: green for the added and changed lines, red where the old side's lines were removed.
        var variant = f.Window.ActualThemeVariant;
        var added = Token("Shell.Diff.Added", variant);
        var removed = Token("Shell.Diff.Removed", variant);
        using (var drawn = Draw(minimap))
        {
            for (var row = 0; row < vm.Rows.Count; row++)
            {
                var expected = vm.Rows[row].Kind switch { '+' or '~' => added, '-' => removed, _ => (Color?)null };
                var stripe = PixelAt(drawn, 1, row);
                if (expected is { } color)
                {
                    Assert.True(stripe == color, $"Row {row} ({vm.Rows[row].Kind}): its stripe is {stripe}, not {color}.");
                }
                else
                {
                    Assert.True(stripe != added && stripe != removed, $"Row {row} is unchanged, but its stripe is {stripe}.");
                }
            }

            // A removed line's band, across the minimap: the new side has no text on it.
            var removedRow = vm.Rows.ToList().FindIndex(r => r.Kind == '-');
            Assert.Equal(Token("Shell.Diff.Removed.Line", variant), PixelAt(drawn, (int)minimap.Bounds.Width - 2, removedRow));

            // An added line's words lie on its band, blended: no pixel of the band lets the background through, neither
            // the word's (drawn at 80%) nor the half-covered row under it.
            var addedRow = vm.Rows.ToList().FindIndex(r => r.Kind == '+');
            for (var dy = 0; dy < Minimap.LineHeight; dy++)
            {
                for (var x = 0; x < 20; x++)
                {
                    var pixel = PixelAt(drawn, x, addedRow, dy);
                    Assert.True(pixel.A == 255, $"The added row {addedRow}'s band at ({x}, +{dy}) is {pixel}: not opaque.");
                }
            }
        }

        // Text Editor → Minimap off and on again, while the diff is open.
        await f.Shell.Workbench.Settings.SetAsync(EditorOptions.MinimapKey, false, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => !minimap.IsVisible);
        await f.Shell.Workbench.Settings.SetAsync(EditorOptions.MinimapKey, true, SettingScope.User, CancellationToken.None);
        await ShellFixture.PumpUntil(() => minimap.IsVisible);

        // A narrow side hides it, to keep its code readable.
        f.Window.Width = 900;
        await ShellFixture.PumpUntil(() =>
        {
            ShellFixture.RenderFrame(f.Window);
            return !minimap.IsVisible;
        }, describe: () => $"The new side is {view.EditorOf(DiffPaneSide.New).Bounds.Width} px wide.");
        f.Window.Width = 1500;
        await ShellFixture.PumpUntil(() =>
        {
            ShellFixture.RenderFrame(f.Window);
            return minimap.IsVisible;
        });

        // The inline view's own minimap marks both kinds of lines.
        vm.SideBySide = false;
        var inline = view.MinimapOf(DiffPaneSide.Inline);
        Assert.NotNull(inline);
        await ShellFixture.PumpUntil(() =>
        {
            ShellFixture.RenderFrame(f.Window);
            return inline.IsEffectivelyVisible && inline.Bounds.Height > 0;
        });
        var lines = view.EditorOf(DiffPaneSide.Inline).Document.LineCount;
        using (var drawn = Draw(inline))
        {
            var stripes = Enumerable.Range(0, lines - 1).Select(line => PixelAt(drawn, 1, line)).ToList();
            Assert.Equal(3, stripes.Count(c => c == removed)); // lines 10, 11 and the old line 30
            Assert.Equal(3, stripes.Count(c => c == added)); // the two added lines and the new line 30
        }
    }

    private static Color Token(string key, Avalonia.Styling.ThemeVariant variant) =>
        Application.Current!.TryGetResource(key, variant, out var value) && value is ISolidColorBrush brush ? brush.Color : throw new InvalidOperationException($"{key} in {variant}");

    /// <summary>The minimap drawn alone, without its visible area's shade (it would tint the marks under it).</summary>
    private static RenderTargetBitmap Draw(Minimap minimap)
    {
        minimap.ViewportBrush = Brushes.Transparent;
        var drawn = new RenderTargetBitmap(new PixelSize((int)minimap.Bounds.Width, (int)minimap.Bounds.Height));
        drawn.Render(minimap);
        return drawn;
    }

    /// <summary>
    /// The color at an x on a 0-based line's band, <paramref name="dy"/> pixels below its top. The bitmap's byte order is
    /// the platform's (BGRA or RGBA), as in the other pixel tests; the colors read are premultiplied, which an opaque
    /// mark's aren't changed by.
    /// </summary>
    private static Color PixelAt(RenderTargetBitmap drawn, int x, int line, int dy = 0)
    {
        var bytes = new byte[4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            drawn.CopyPixels(new PixelRect(x, (int)(line * Minimap.LineHeight) + dy, 1, 1), handle.AddrOfPinnedObject(), bytes.Length, bytes.Length);
        }
        finally
        {
            handle.Free();
        }

        return drawn.Format == Avalonia.Platform.PixelFormat.Rgba8888
            ? Color.FromArgb(bytes[3], bytes[0], bytes[1], bytes[2])
            : Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }
}
