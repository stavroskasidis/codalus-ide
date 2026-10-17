using Avalonia;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// M46's smoke step: the first-run setup in the shipped app, as at a first start. Harbor Light's appearance step is the
/// website's picture (<c>setup-appearance.png</c>); Ember Dark previews live in the setup window and the IDE behind it;
/// VS Code's keys and a stand-in of VS Code's settings are taken; the tools are checked (nothing installs); Done opens
/// the start window with all of it applied. The settings it changed are put back afterwards.
/// </summary>
internal sealed partial class SmokeTest
{
    private static readonly string[] s_setupKeys = [ThemeService.SettingKey, BuiltIns.KeyboardSchemeSetting, "editor.tabSize", ShellHost.SetupCompletedSetting];

    private async Task SetupStepAsync()
    {
        Check(shell.FirstRunDecision == SetupDecision.Automation, "the setup didn't open by itself in the smoke test");
        var settings = shell.Workbench.Settings;
        var before = s_setupKeys.ToDictionary(k => k, k => settings.GetLayerValue(k, SettingScope.User));
        var vscode = Path.Combine(shell.Paths.Cache, "smoke-setup", "vscode-user");
        try
        {
            shell.ShowSetup(firstStart: true);
            await WaitAsync(() => shell.IsSetupOpen, "the setup opens").ConfigureAwait(true);
            var model = shell.SetupModel!;
            var window = shell.SetupWindowView!;
            window.Width = 1280;
            window.Height = 800;
            Check(model.Families.Select(f => f.Id).SequenceEqual([ThemeService.Harbor, ThemeService.Graphite, ThemeService.Daylight, ThemeService.Ember]), "the four families as cards");
            Check(model.SelectedFamily == ThemeService.Harbor, "Harbor is chosen");

            // The website's picture: Harbor, Light.
            model.Modes.Single(m => m.Id == "light").SelectCommand.Execute(null);
            await WaitAsync(() => shell.Theme.ActiveTheme == "harbor.light", "Harbor Light previews").ConfigureAwait(true);
            await Task.Delay(300).ConfigureAwait(true);
            await RenderedAsync(window).ConfigureAwait(true);
            Screenshot("setup-appearance.png", window);

            // Ember, Dark: the setup window and the IDE behind it repaint, nothing saved yet.
            model.Families.Single(f => f.Id == ThemeService.Ember).SelectCommand.Execute(null);
            model.Modes.Single(m => m.Id == "dark").SelectCommand.Execute(null);
            await WaitAsync(() => shell.Theme.ActiveTheme == "ember.dark", "Ember Dark previews").ConfigureAwait(true);
            Check(Application.Current!.ActualThemeVariant == ThemeService.EmberDark && window.ActualThemeVariant == ThemeService.EmberDark, "the IDE and the setup window take Ember Dark");
            await RenderedAsync(window).ConfigureAwait(true);
            Screenshot("setup-appearance-ember-dark.png", window);
            await model.NextAsync().ConfigureAwait(true);
            Check(settings.GetString(ThemeService.SettingKey) == "ember.dark", "Next saves the theme");

            // Keyboard: VS Code's.
            model.Schemes.Single(s => s.Id == "vscode").SelectCommand.Execute(null);
            await model.NextAsync().ConfigureAwait(true);
            await WaitAsync(() => Equals(shell.Workbench.Context.GetValue("keyboardScheme"), "vscode"), "the VS Code keyboard scheme applies").ConfigureAwait(true);

            // Your settings: a stand-in of VS Code's user folder.
            Directory.CreateDirectory(vscode);
            await File.WriteAllTextAsync(Path.Combine(vscode, "settings.json"), """{ "editor.tabSize": 3 }""").ConfigureAwait(true);
            model.VsCodeFolder = vscode;
            await RenderedAsync(window).ConfigureAwait(true);
            Screenshot("setup-settings.png", window);
            await model.ImportVsCodeAsync().ConfigureAwait(true);
            Check(settings.GetInt32("editor.tabSize") == 3, $"VS Code's settings are imported ({model.SettingsStatus})");
            await model.NextAsync().ConfigureAwait(true);

            // Tools: M37's check of the .NET SDK and git, which this machine has; nothing installs.
            await WaitAsync(() => model.Tools.Any(t => t.Tool.Id == "dotnet"), "the tools are checked", 90).ConfigureAwait(true);
            Check(model.Tools.Single(t => t.Tool.Id == "dotnet").Tool.State == Workbench.Setup.ToolState.Found, "the .NET SDK is found");
            await RenderedAsync(window).ConfigureAwait(true);
            Screenshot("setup-tools.png", window);

            // Done: the start window, with the choices applied.
            await model.NextAsync().ConfigureAwait(true);
            await WaitAsync(() => !shell.IsSetupOpen && shell.IsStartWindowOpen, "Done opens the start window").ConfigureAwait(true);
            Check(model.WasDone && settings.GetBoolean(ShellHost.SetupCompletedSetting), "the setup is recorded");
            Check(shell.Theme.ActiveTheme == "ember.dark", "the chosen theme applies");
            shell.StartWindowModel!.ContinueCommand.Execute(null);
            await WaitAsync(() => !shell.IsStartWindowOpen, "Continue without code closes the start window").ConfigureAwait(true);
        }
        finally
        {
            if (shell.SetupModel is { } open)
            {
                await open.FinishAsync(done: false).ConfigureAwait(true);
            }

            foreach (var (key, value) in before)
            {
                await settings.SetAsync(key, value, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
            }

            try
            {
                Directory.Delete(Path.GetDirectoryName(vscode)!, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
