using Avalonia;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// M46's smoke step: the first-run setup in the shipped app, as at a first start. The welcome page says where the user
/// comes from (VS Code, which preselects its keys and marks its importer). Classic Light's appearance step (Classic
/// is the first card and the default, D36) is the website's picture (<c>setup-appearance.png</c>); Ember Dark previews live in the setup window and the IDE behind it;
/// VS Code's keys and a stand-in of VS Code's settings are taken; the tools are checked (nothing installs); the AI step
/// (ADR 0096) compares the three ways, a coding agent chosen for the website's <c>setup-ai.png</c> and then put back, so
/// nothing connects here (the Connect dialog has its own step); the summary's Done opens
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
            // As in a new user data folder: no theme saved yet (the steps before this one chose some), so the setup
            // shows Classic, the default (D36), first. The settings are put back afterwards.
            await settings.SetAsync(ThemeService.SettingKey, (System.Text.Json.JsonElement?)null, SettingScope.User, CancellationToken.None).ConfigureAwait(true);
            shell.ShowSetup(firstStart: true);
            await WaitAsync(() => shell.IsSetupOpen, "the setup opens").ConfigureAwait(true);
            var model = shell.SetupModel!;
            var window = shell.SetupWindowView!;
            window.Width = 1280;
            window.Height = 800;

            // The welcome page: coming from VS Code. Get Started goes on to Appearance; nothing is saved by it.
            Check(model.Page == SetupPage.Welcome && model.NextTitle == "Get Started", "a first start opens on the welcome page");
            model.Origins.Single(o => o.Id == "vscode").SelectCommand.Execute(null);
            await RenderedAsync(window).ConfigureAwait(true);
            Screenshot("setup-welcome.png", window);
            await model.NextAsync().ConfigureAwait(true);
            Check(model.IsAppearance && settings.GetLayerValue(BuiltIns.KeyboardSchemeSetting, SettingScope.User)?.GetRawText() == before[BuiltIns.KeyboardSchemeSetting]?.GetRawText(), "Get Started shows Appearance and saves nothing");
            Check(model.CodeSample.Count > 0 && model.FontSize == Math.Clamp(Math.Round(settings.GetDouble("editor.fontSize", 13)), SetupViewModel.MinFontSize, SetupViewModel.MaxFontSize), "the editor's font size shows on a sample");
            Check(model.Families.Select(f => f.Id).SequenceEqual([ThemeService.Classic, ThemeService.Harbor, ThemeService.Graphite, ThemeService.Daylight, ThemeService.Ember]), "the families as cards, Classic first");
            Check(model.SelectedFamily == ThemeService.Classic && model.IsMatchSystem, "Classic, matching the system, is chosen");

            // The website's picture: Classic, Light.
            model.Modes.Single(m => m.Id == "light").SelectCommand.Execute(null);
            await WaitAsync(() => shell.Theme.ActiveTheme == "light" && shell.Theme.ActiveFamily == ThemeService.Classic, "Classic Light previews").ConfigureAwait(true);
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

            // Keyboard: VS Code's, chosen already by where the user comes from.
            Check(model.SelectedScheme == "vscode", "coming from VS Code preselects its keys");
            await model.NextAsync().ConfigureAwait(true);
            await WaitAsync(() => Equals(shell.Workbench.Context.GetValue("keyboardScheme"), "vscode"), "the VS Code keyboard scheme applies").ConfigureAwait(true);

            // Your settings: a stand-in of VS Code's user folder.
            Directory.CreateDirectory(vscode);
            await File.WriteAllTextAsync(Path.Combine(vscode, "settings.json"), """{ "editor.tabSize": 3 }""").ConfigureAwait(true);
            model.VsCodeFolder = vscode;
            Check(model.SuggestsVsCode, "VS Code's importer is the suggested one");
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

            // AI (ADR 0096): the three ways with the same facts each. A coding agent, with Claude, is the website's
            // picture; then the step is put back as it was, so the summary's button connects nothing.
            await model.NextAsync().ConfigureAwait(true);
            Check(model.IsAi && model.AiPaths.Count == 3 && model.AiPaths.All(p => p.Facts.Count == 4), "the AI step compares three ways");
            Check(!model.AiChanged, "the AI step shows what the settings have");
            var ai = model.AiChoice;
            model.AiPaths.Single(p => p.Path == SetupAiPath.Agent).SelectCommand.Execute(null);
            model.AiAgents.Single(a => a.Id == "claude").SelectCommand.Execute(null);
            Check(model.AgentBenefits.Count > 0 && model.AgentWindows.Count == 2, "an agent's benefits in the IDE and its two windows are told");
            await RenderedAsync(window).ConfigureAwait(true);
            Screenshot("setup-ai.png", window);
            model.AiPaths.Single(p => p.Path == ai.Path).SelectCommand.Execute(null);
            if ((ai.Agent ?? ai.Provider) is { } chosen)
            {
                model.AiAgents.Concat(model.AiProviders).Single(c => c.Id == chosen).SelectCommand.Execute(null);
            }

            Check(!model.AiChanged, "the AI step is as it was");

            // The summary, then Done: the start window, with the choices applied and nothing of AI changed.
            await model.NextAsync().ConfigureAwait(true);
            Check(model.Page == SetupPage.Finish && model.NextTitle == "Done" && model.Summary.Count == 5, "the summary lists what was chosen");
            Check(model.Shortcuts.Count > 0, "the summary shows the chosen scheme's keys");
            await RenderedAsync(window).ConfigureAwait(true);
            Screenshot("setup-summary.png", window);
            await model.NextAsync().ConfigureAwait(true);
            await WaitAsync(() => !shell.IsSetupOpen && shell.IsStartWindowOpen, "Done opens the start window").ConfigureAwait(true);
            Check(!shell.ConnectAgent.IsOpen, "nothing connects when the AI step was left as it was");
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
