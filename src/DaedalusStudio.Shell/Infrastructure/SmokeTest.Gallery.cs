using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DaedalusStudio.Shell.Controls;
using DaedalusStudio.Shell.Docking;
using DaedalusStudio.Shell.Theming;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Settings;

namespace DaedalusStudio.Shell.Infrastructure;

/// <summary>
/// The UI gallery (M34d, D25): every tool window, the main documents and the window's chrome, photographed in Blue,
/// Light, Dark and High Contrast, empty (before a solution opens), with data (the Testing solution after its tests
/// ran) and in an error state (errors in the Error List and the build output, a failed test's details). The pictures
/// go to <c>logs/gallery</c> with an index, <c>index.md</c>, for the maintainer's review; eng/smoke-test.sh copies them
/// next to the report.
/// </summary>
internal sealed partial class SmokeTest
{
    private readonly SortedDictionary<string, SortedDictionary<string, string>> _gallery = new(StringComparer.Ordinal);

    private string GalleryFolder => Path.Combine(shell.Paths.Logs, "gallery");

    /// <summary>Every tool window, empty: no solution is open yet.</summary>
    private Task GalleryEmptyStepAsync() => GalleryToolWindowsAsync("empty");

    /// <summary>Every tool window with data, the documents, the chrome, and the error states.</summary>
    private async Task GalleryDataStepAsync()
    {
        await GalleryToolWindowsAsync("data").ConfigureAwait(true);

        // Documents and the window's chrome: tabs pinned, previewed and colored by project, a tool window focused so the
        // selected document tab is the one of a pane without the focus.
        var sources = Directory.EnumerateFiles(_testing, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Take(3).ToList();
        foreach (var (file, index) in sources.Select((f, i) => (f, i)))
        {
            await shell.OpenFileAsync(file, preview: index == sources.Count - 1).ConfigureAwait(true);
        }

        if (shell.Layout.Documents.FirstOrDefault() is { } first)
        {
            shell.Layout.SetPinned(first, true);
        }

        await shell.Workbench.Settings.SetAsync(ShellHost.ColorTabsByProjectSetting, true, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        await GalleryThemesAsync(async theme =>
        {
            shell.Layout.ShowToolWindow(SolutionExplorerViewModel.ToolWindowId, focus: true);
            await SettleAsync().ConfigureAwait(true);
            GalleryShot("window", "data", theme, null);
            if (shell.ActiveEditor?.Controller is { } editor)
            {
                editor.Editor.TextArea.Focus();
                await SettleAsync().ConfigureAwait(true);
                GalleryShot("window-editor-focused", "data", theme, null);
            }
        }).ConfigureAwait(true);
        await shell.Workbench.Settings.SetAsync(ShellHost.ColorTabsByProjectSetting, (System.Text.Json.JsonElement?)null, SettingScope.User, CancellationToken.None).ConfigureAwait(true);

        var documents = new List<(string Name, Func<ShellDocument?> Open)>
        {
            ("options", () => { shell.OpenSettings(); return shell.Layout.ActiveDocument; }),
            ("keyboard-shortcuts", () => { shell.OpenKeyboardShortcuts(); return shell.Layout.ActiveDocument; }),
            ("project-properties", () => shell.OpenProjectProperties(shell.Solutions.Projects.Keys.FirstOrDefault()) is null ? null : shell.Layout.ActiveDocument),
            ("nuget", () => shell.OpenPackageManager(shell.Solutions.Projects.Keys.FirstOrDefault()) is null ? null : shell.Layout.ActiveDocument),
            ("configuration-manager", () => shell.OpenConfigurationManager() is null ? null : shell.Layout.ActiveDocument),
        };
        foreach (var (name, open) in documents)
        {
            ShellDocument? document;
            try
            {
                document = open();
            }
            catch (Exception ex)
            {
                shell.Workbench.Log.AppendLine($"[smoke] gallery: {name} did not open: {ex.Message}");
                continue;
            }

            if (document is null)
            {
                continue;
            }

            await GalleryThemesAsync(async theme =>
            {
                await SettleAsync().ConfigureAwait(true);
                GalleryShot(name, "data", theme, PaneOf(document));
            }).ConfigureAwait(true);
            shell.Layout.CloseDocument(document);
        }

        // Error states: a compile error in the Error List, the failed build's output, a failed test's details.
        if (sources.FirstOrDefault() is { } broken && await shell.OpenFileAsync(broken, preview: false).ConfigureAwait(true) is { } editorDocument)
        {
            await WaitAsync(() => editorDocument.Controller is not null, "the editor for the error state opens").ConfigureAwait(true);
            var controller = editorDocument.Controller!;
            var original = controller.Model.Text.Text;
            try
            {
                controller.Model.Text.Insert(0, "this is not C#;\n");
                await shell.SaveAllAsync().ConfigureAwait(true);
                await WaitAsync(() => shell.ErrorList.Rows.Count > 0, "the Error List shows the error", 120).ConfigureAwait(true);
                var built = shell.Builds.LastBuild;
                shell.Run("build.buildSolution");
                await WaitAsync(() => shell.Builds.LastBuild is { } b && b != built, "the failing build finishes", 300).ConfigureAwait(true);
                await GalleryThemesAsync(async theme =>
                {
                    GalleryTool(BuiltIns.OutputWindow, "error", theme);
                    GalleryTool(ErrorListViewModel.ToolWindowId, "error", theme);
                    await Task.CompletedTask.ConfigureAwait(true);
                }, settle: true).ConfigureAwait(true);
            }
            finally
            {
                controller.Model.Text.Text = original;
                await shell.SaveAllAsync().ConfigureAwait(true);
            }
        }

        if (shell.TestExplorer.Rows.FirstOrDefault(r => r.Label == "Fails") is { } failed)
        {
            shell.TestExplorer.Selected = failed;
            await GalleryThemesAsync(theme =>
            {
                GalleryTool(TestExplorerViewModel.ToolWindowId, "error", theme);
                return Task.CompletedTask;
            }, settle: true).ConfigureAwait(true);
        }

        WriteGalleryIndex();
        Check(_gallery.Count > 20, $"the gallery has every surface ({_gallery.Count} surfaces)");
    }

    /// <summary>Shows each registered tool window in each theme and photographs its pane.</summary>
    private async Task GalleryToolWindowsAsync(string state)
    {
        var ids = shell.Workbench.ToolWindows.All.Select(d => d.Id).ToList();
        var open = ids.Where(shell.Layout.IsVisible).ToHashSet(StringComparer.Ordinal);
        await GalleryThemesAsync(async theme =>
        {
            foreach (var id in ids)
            {
                shell.Layout.ShowToolWindow(id, focus: true);
                await SettleAsync().ConfigureAwait(true);
                GalleryTool(id, state, theme);
            }
        }).ConfigureAwait(true);

        foreach (var id in ids.Where(i => !open.Contains(i)))
        {
            shell.Layout.CloseToolWindow(id);
        }

        WriteGalleryIndex();
    }

    /// <summary>Runs <paramref name="body"/> in each gallery theme and restores the user's theme.</summary>
    private async Task GalleryThemesAsync(Func<string, Task> body, bool settle = false)
    {
        var before = shell.Theme.Setting;
        try
        {
            foreach (var theme in s_galleryThemes)
            {
                await shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, theme, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                await WaitAsync(() => shell.Theme.ActiveTheme == theme, $"the {theme} theme applies").ConfigureAwait(true);
                if (settle)
                {
                    await SettleAsync().ConfigureAwait(true);
                }

                await body(theme).ConfigureAwait(true);
            }
        }
        finally
        {
            await shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, before, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        }
    }

    /// <summary>Two frames and a moment for content that loads after it shows.</summary>
    private async Task SettleAsync()
    {
        await RenderedAsync().ConfigureAwait(true);
        await Task.Delay(60).ConfigureAwait(true);
        await RenderedAsync().ConfigureAwait(true);
    }

    private void GalleryTool(string id, string state, string theme)
    {
        if (shell.Layout.ShowToolWindow(id, focus: false) is { } tool)
        {
            GalleryShot(id, state, theme, PaneOf(tool));
        }
    }

    /// <summary>The pane of a dockable: its tool window chrome (caption included) or its document's view.</summary>
    private Visual? PaneOf(Dock.Model.Core.IDockable dockable)
    {
        var pane = window.GetVisualDescendants().OfType<PaneHost>().FirstOrDefault(p => p.Dockable == dockable && p.IsEffectivelyVisible);
        return pane?.FindAncestorOfType<Dock.Avalonia.Controls.ToolChromeControl>() as Visual ?? pane;
    }

    /// <summary>Saves the window (or <paramref name="target"/>'s part of it) as <c>{surface}-{state}-{theme}.png</c>.</summary>
    private void GalleryShot(string surface, string state, string theme, Visual? target)
    {
        var name = $"{Slug(surface)}-{state}-{theme}.png";
        try
        {
            Visual root = window;
            while (root.GetVisualParent() is { } parent)
            {
                root = parent;
            }

            var scaling = window.RenderScaling;
            var dpi = new Vector(96 * scaling, 96 * scaling);
            using var full = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)(root.Bounds.Width * scaling)), Math.Max(1, (int)(root.Bounds.Height * scaling))), dpi);
            full.Render(root);
            var area = new Rect(root.Bounds.Size);
            if (target is not null && target.TranslatePoint(default, root) is { } origin)
            {
                area = new Rect(origin, target.Bounds.Size).Intersect(area);
            }

            if (area.Width < 8 || area.Height < 8)
            {
                return;
            }

            using var picture = new RenderTargetBitmap(new PixelSize((int)(area.Width * scaling), (int)(area.Height * scaling)), dpi);
            using (var context = picture.CreateDrawingContext(true))
            {
                context.DrawImage(full, area, new Rect(area.Size));
            }

            Directory.CreateDirectory(GalleryFolder);
            picture.Save(Path.Combine(GalleryFolder, name), PngBitmapEncoderOptions.Default);
            var key = $"{surface} ({state})";
            if (!_gallery.TryGetValue(key, out var themes))
            {
                _gallery[key] = themes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            }

            themes[theme] = name;
        }
        catch (Exception ex)
        {
            shell.Workbench.Log.AppendLine($"[smoke] Gallery picture {name} failed: {ex.Message}");
        }
    }

    /// <summary>index.md: one row per surface and state, a picture per theme.</summary>
    private void WriteGalleryIndex()
    {
        if (_gallery.Count == 0)
        {
            return;
        }

        var text = new StringBuilder();
        text.AppendLine("# UI gallery");
        text.AppendLine();
        text.AppendLine($"Daedalus Studio {ShellInfo.Version} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}: every surface in each theme, empty, with data and in an error state (M34d, D25).");
        text.AppendLine();
        text.AppendLine("| Surface | " + string.Join(" | ", s_galleryThemes) + " |");
        text.AppendLine("|---|" + string.Concat(Enumerable.Repeat("---|", s_galleryThemes.Length)));
        foreach (var (surface, themes) in _gallery)
        {
            text.Append("| ").Append(surface).Append(" |");
            foreach (var theme in s_galleryThemes)
            {
                text.Append(themes.TryGetValue(theme, out var file) ? $" [{theme}]({file}) |" : " |");
            }

            text.AppendLine();
        }

        Directory.CreateDirectory(GalleryFolder);
        File.WriteAllText(Path.Combine(GalleryFolder, "index.md"), text.ToString());
    }
}
