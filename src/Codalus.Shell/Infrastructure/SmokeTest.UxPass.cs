using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Codalus.Platform;
using Codalus.Shell.Theming;
using Codalus.Shell.Views;
using Codalus.Workbench.Settings;
using MenuItem = Avalonia.Controls.MenuItem;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// M34a's smoke steps (the UX pass): the menus and the Edit submenus photographed for the gallery, Ctrl+Space through
/// the window's keyboard handling in the real app, and the Output window following new output, in every theme.
/// </summary>
internal sealed partial class SmokeTest
{
    /// <summary>Classic's four themes, which the UX pass photographs.</summary>
    private static readonly string[] s_classicThemes = ["blue", "light", "dark", "highContrast"];

    /// <summary>
    /// The gallery's themes (M34h, the maintainer's decision): Classic's four (Classic is the default, D36) and Harbor's
    /// three (Light, Dark and High contrast; the default when that was decided). With <c>CODALUS_GALLERY_THEMES=all</c> also
    /// the other families' nine variants, for a review of every family: more than the smoke test's time on CI.
    /// </summary>
    private static readonly string[] s_galleryThemes = Environment.GetEnvironmentVariable("CODALUS_GALLERY_THEMES") == "all"
        ? [.. ThemeService.Themes.Select(t => t.Id)]
        : [.. s_classicThemes, "harbor.light", "harbor.dark", "harbor.highContrast"];

    private async Task UxPassStepsAsync()
    {
        await StepAsync("shell.menus", "M34a", "The menus are trees with icons and fit the screen; the gallery photographs them", async () =>
        {
            if (shell.Workbench.Platform == PlatformKind.MacOS)
            {
                // The global menu bar: the same tree as native submenus (no in-window menu to photograph).
                var native = NativeMenu.GetMenu(window) ?? throw new SmokeFailure("the window has no native menu");
                var edit = native.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "Edit") ?? throw new SmokeFailure("no Edit menu");
                Check(edit.Menu?.Items.OfType<NativeMenuItem>().Any(i => i.Header == "IntelliSense" && i.Menu is not null) == true, "Edit has an IntelliSense submenu");
                return;
            }

            var bar = window.GetVisualDescendants().OfType<Menu>().FirstOrDefault(m => m.Name == "MenuBar") ?? throw new SmokeFailure("no menu bar");
            var screen = window.Screens.ScreenFromWindow(window)?.WorkingArea.Height / window.RenderScaling ?? 1000;
            foreach (var top in bar.Items.OfType<MenuItem>().ToList())
            {
                var title = (top.Header as string ?? "menu").Replace("_", string.Empty, StringComparison.Ordinal);
                top.Open();
                await RenderedAsync().ConfigureAwait(true);
                var height = PopupHeight(top);
                Check(height > 0 && height <= screen, $"the {title} menu ({height:0} px) fits the screen ({screen:0} px)");
                Screenshot($"smoke-test-menu-{title.ToLowerInvariant()}.png", top);
                if (title == "Edit")
                {
                    foreach (var sub in top.Items.OfType<MenuItem>().Where(i => i.ItemCount > 0).ToList())
                    {
                        sub.Open();
                        await RenderedAsync().ConfigureAwait(true);
                        Check(PopupHeight(sub) <= screen, $"Edit ▸ {sub.Header} fits the screen");
                        Screenshot($"smoke-test-menu-edit-{Slug(sub.Header as string)}.png", top, sub);
                        sub.Close();
                    }
                }

                top.Close();
                await RenderedAsync().ConfigureAwait(true);
            }
        }).ConfigureAwait(true);

        await StepAsync("editor.completionKey", "M34a", "Ctrl+Space through the window's key handling opens completion", async () =>
        {
            var program = await OpenAsync(Program).ConfigureAwait(true);
            var original = program.Model.Text.Text;
            MoveCaret(program, "Console.WriteLine", "Con".Length);
            await RenderedAsync().ConfigureAwait(true);
            Check(Equals(shell.Workbench.Context.GetValue("editorTextFocus"), true), "the focused editor sets editorTextFocus");

            // The physical Control key on every OS (Cmd+Space is Spotlight on macOS).
            var area = program.Editor.TextArea;
            area.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space, KeyModifiers = Avalonia.Input.KeyModifiers.Control, Source = area });
            await WaitAsync(() => program.CompletionWindow is { } w && w.CompletionList.CompletionData.Any(d => d.Text == "Console"), "completion lists Console", 90).ConfigureAwait(true);
            Check(program.Model.Text.Text == original, "Ctrl+Space typed nothing");
            await WaitAsync(() => program.CompletionWindow?.CompletionList.SelectedItem?.Text == "Console", "the best match of the typed prefix, Console, is selected").ConfigureAwait(true);
            var column = program.Caret.Column;
            await WaitAsync(() => shell.StatusBar.Right.Concat(shell.StatusBar.Left).Any(i => i.Text?.Contains($"Col {column}", StringComparison.Ordinal) == true), $"the status bar shows column {column}").ConfigureAwait(true);
            await RenderedAsync().ConfigureAwait(true);
            Screenshot("smoke-test-completion.png", program.CompletionWindow is Popup completion ? [completion] : []);
            program.CompletionWindow?.Close();
        }, "language.ready").ConfigureAwait(true);

        await StepAsync("shell.outputFollows", "M34a", "The Output window follows new output; the gallery in every theme", async () =>
        {
            shell.ShowLogs();
            await WaitAsync(() => window.GetVisualDescendants().OfType<OutputView>().Any(v => v.IsEffectivelyVisible), "the Output window shows").ConfigureAwait(true);
            for (var i = 0; i < 200; i++)
            {
                shell.Workbench.Log.AppendLine($"[smoke] output line {i}");
            }

            var output = window.GetVisualDescendants().OfType<OutputView>().First(v => v.IsEffectivelyVisible);
            await WaitAsync(() => output.Text.Contains("[smoke] output line 199", StringComparison.Ordinal) && AtEnd(output), "the Output window shows the last line").ConfigureAwait(true);

            if (shell.Workbench.Platform == PlatformKind.MacOS)
            {
                return;
            }

            // The gallery: Edit ▸ IntelliSense open over the editor (minimap) and the Output window, in each theme.
            var before = shell.Theme.Setting;
            await OpenAsync(Program).ConfigureAwait(true);
            var bar = window.GetVisualDescendants().OfType<Menu>().First(m => m.Name == "MenuBar");
            foreach (var theme in s_classicThemes)
            {
                await shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, theme, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                await WaitAsync(() => shell.Theme.ActiveTheme == theme, $"the {theme} theme applies").ConfigureAwait(true);
                var edit = bar.Items.OfType<MenuItem>().First(i => Equals(i.Header, "_Edit"));
                edit.Open();
                await RenderedAsync().ConfigureAwait(true);
                var intelliSense = edit.Items.OfType<MenuItem>().First(i => Equals(i.Header, "IntelliSense"));
                intelliSense.Open();
                await RenderedAsync().ConfigureAwait(true);
                Screenshot($"smoke-test-m34a-{theme}.png", edit, intelliSense);
                intelliSense.Close();
                edit.Close();
                var debug = bar.Items.OfType<MenuItem>().First(i => Equals(i.Header, "_Debug"));
                debug.Open();
                await RenderedAsync().ConfigureAwait(true);
                Screenshot($"smoke-test-m34a-debug-{theme}.png", debug);
                debug.Close();
            }

            await shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, before, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        }).ConfigureAwait(true);

        await StepAsync("shell.fileIcons", "M34a", "File-type and project icons in the Solution Explorer and the tabs, the navigation bar and the outlining markers; the gallery in every theme", async () =>
        {
            var greeter = await OpenAsync(Greeter).ConfigureAwait(true);
            shell.Layout.ShowToolWindow(ViewModels.SolutionExplorerViewModel.ToolWindowId, focus: false);
            shell.Explorer.Reveal("f:" + Program);
            shell.Explorer.Reveal("f:" + Greeter);
            await WaitAsync(() => shell.Explorer.Rows.Any(r => r.Name == "Greeter.cs" && r.Icon == "cl:csharp"), "Greeter.cs shows the C# file icon").ConfigureAwait(true);
            Check(shell.Explorer.Rows.Any(r => r.Name == "Hello" && r.Icon == "cl:project-csharp"), "the console app shows its project icon");
            Check(shell.Explorer.Rows.Any(r => r.Name == "Greetings" && r.Icon == "cl:project-csharp"), "the library shows its project icon");
            await WaitAsync(() => shell.ActiveEditor?.View?.NavigationBar.Types.Any(t => t.Display == "Greeter") == true, "the navigation bar lists Greeter", 60).ConfigureAwait(true);
            greeter.Editor.TextArea.Focus();
            var before = shell.Theme.Setting;
            foreach (var theme in s_classicThemes)
            {
                await shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, theme, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                await WaitAsync(() => shell.Theme.ActiveTheme == theme, $"the {theme} theme applies").ConfigureAwait(true);
                await RenderedAsync().ConfigureAwait(true);
                Screenshot($"smoke-test-m34a-explorer-{theme}.png");
            }

            await shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, before, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        }, "language.ready").ConfigureAwait(true);
    }

    private static bool AtEnd(OutputView view) =>
        view.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } viewer
        || viewer.Offset.Y + viewer.Viewport.Height >= viewer.Extent.Height - 2;

    private static string Slug(string? text) =>
        new((text ?? "menu").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    private static Popup? PopupOf(MenuItem item) => item.GetVisualDescendants().OfType<Popup>().FirstOrDefault();

    private static double PopupHeight(MenuItem item) => PopupOf(item)?.Child?.Bounds.Height ?? 0;

    /// <summary>The window with the open menus' popups drawn over it (they are windows of their own on most platforms).</summary>
    private void Screenshot(string name, params MenuItem[] open) =>
        Screenshot(name, open.Select(PopupOf).OfType<Popup>());

    private void Screenshot(string name, IEnumerable<Popup> open)
    {
        HideToasts(name);
        try
        {
            Visual root = window;
            while (root.GetVisualParent() is { } parent)
            {
                root = parent;
            }

            var scaling = window.RenderScaling;
            var size = new PixelSize(Math.Max(1, (int)(root.Bounds.Width * scaling)), Math.Max(1, (int)(root.Bounds.Height * scaling)));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
            bitmap.Render(root);
            var origin = root.PointToScreen(default);
            var popups = new List<(RenderTargetBitmap Image, Rect Where)>();
            foreach (var popup in open)
            {
                if (popup.Child is not { } child || TopLevel.GetTopLevel(child) is not { } host || host == window)
                {
                    continue; // an overlay popup is part of the window's own rendering
                }

                var at = host.PointToScreen(default);
                var image = new RenderTargetBitmap(
                    new PixelSize(Math.Max(1, (int)(host.Bounds.Width * scaling)), Math.Max(1, (int)(host.Bounds.Height * scaling))),
                    new Vector(96 * scaling, 96 * scaling));
                image.Render(host);
                popups.Add((image, new Rect(new Point((at.X - origin.X) / scaling, (at.Y - origin.Y) / scaling), host.Bounds.Size)));
            }

            if (popups.Count > 0)
            {
                using var context = bitmap.CreateDrawingContext(false);
                foreach (var (image, where) in popups)
                {
                    context.DrawImage(image, where);
                    image.Dispose();
                }
            }

            Directory.CreateDirectory(shell.Paths.Logs);
            bitmap.Save(Path.Combine(shell.Paths.Logs, name), PngBitmapEncoderOptions.Default);
        }
        catch (Exception ex)
        {
            shell.Workbench.Log.AppendLine($"[smoke] Screenshot {name} failed: {ex.Message}");
        }
    }
}
