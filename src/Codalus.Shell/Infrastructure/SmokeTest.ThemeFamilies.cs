using Avalonia;
using Avalonia.Media;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// M45's smoke step: the theme families in the shipped app. Each of the twelve variants applies live over the open
/// solution, with its own tokens (not Classic's under it) and its editor colors; Harbor follows the system; Options shows
/// the family picker. The pictures (<c>smoke-test-theme-&lt;family&gt;.&lt;variant&gt;.png</c>) are the website's and the
/// gallery's.
/// </summary>
internal sealed partial class SmokeTest
{
    private static readonly string[] s_familyThemes =
    [
        .. new[] { "harbor", "graphite", "daylight", "ember" }.SelectMany(f => new[] { "light", "dark", "highContrast" }.Select(v => $"{f}.{v}")),
    ];

    private async Task ThemeFamiliesStepAsync()
    {
        var before = shell.Theme.Setting;
        try
        {
            var program = await OpenAsync(Program).ConfigureAwait(true);
            await WaitAsync(() => program.Colorizer.HasTokens, "semantic tokens arrive", 90).ConfigureAwait(true);
            shell.Layout.ShowToolWindow(SolutionExplorerViewModel.ToolWindowId, focus: false);
            await RenderedAsync().ConfigureAwait(true);
            // The editor has the focus, so its tab shows the family's active-tab shape.
            if (shell.Layout.ActiveDocument is { } document)
            {
                shell.Layout.OpenDocument(document, focus: true);
            }

            program.Editor.TextArea.Focus();
            await RenderedAsync().ConfigureAwait(true);
            var classicBackground = Token("Shell.Background", ThemeService.VariantOf("light"));
            foreach (var theme in s_familyThemes)
            {
                await shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, theme, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                await WaitAsync(() => shell.Theme.ActiveTheme == theme && Equals(shell.Workbench.Context.GetValue("themeFamily"), ThemeService.FamilyOf(theme)), $"the {theme} theme applies").ConfigureAwait(true);
                await RenderedAsync().ConfigureAwait(true);
                var variant = ThemeService.VariantOf(theme);
                Check(Application.Current!.ActualThemeVariant == variant, $"{theme}: the window uses its variant");
                Check(Token("Shell.Background", variant) is { } background && background != classicBackground, $"{theme}: its own window color");
                Check(shell.EditorTheme.Id == theme, $"{theme}: the editor takes its colors");
                Screenshot($"smoke-test-theme-{theme}.png");
            }

            // Harbor, the default, follows the system: the variant is the OS's light, dark or high contrast.
            await shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, ThemeService.Harbor, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
            await WaitAsync(() => shell.Theme.ActiveTheme.StartsWith("harbor.", StringComparison.Ordinal), "Harbor follows the system").ConfigureAwait(true);

            // Options: the families as cards, in Harbor Light.
            await shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, "harbor.light", SettingScope.User, CancellationToken.None).ConfigureAwait(true);
            await WaitAsync(() => shell.Theme.ActiveTheme == "harbor.light", "Harbor Light applies").ConfigureAwait(true);
            shell.OpenSettings(ThemeService.SettingKey);
            await WaitAsync(() => shell.Layout.FindDocument<SettingsDocument>(SettingsDocument.DocumentId)?.ViewModel.Items.OfType<ColorThemeSettingViewModel>().Any() == true, "Options shows the family picker").ConfigureAwait(true);
            await Task.Delay(300).ConfigureAwait(true);
            await RenderedAsync().ConfigureAwait(true);
            Screenshot("smoke-test-theme-families-options.png");
            if (shell.Layout.FindDocument<SettingsDocument>(SettingsDocument.DocumentId) is { } settings)
            {
                shell.Layout.CloseDocument(settings);
            }
        }
        finally
        {
            await shell.Workbench.Settings.SetAsync(ThemeService.SettingKey, before, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
        }
    }

    private static Color? Token(string key, Avalonia.Styling.ThemeVariant variant) =>
        Application.Current!.TryGetResource(key, variant, out var value) && value is ISolidColorBrush brush ? brush.Color : null;
}
