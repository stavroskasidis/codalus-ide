using Codalus.Rpc;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Settings;

namespace Codalus.Shell;

/// <summary>
/// The Start button's drop-down: the startup project's launch profiles, and for a web app the debugging browser
/// (<c>debug.browser</c>) and script debugging (<c>debug.javaScript</c>); then the project's Debug properties.
/// </summary>
public sealed partial class ShellHost
{
    private CancellationTokenSource? _launchMenuRefresh;

    private void InitializeLaunchMenu()
    {
        Toolbar.LaunchMenuOpening = RefreshLaunchMenu;
        Toolbar.SelectLaunchProfile = name =>
        {
            if (Runs.StartupProject is { } project)
            {
                Runs.SetProfile(project, name);
                SaveEditorSession();
            }
        };
        Toolbar.SelectBrowser = browser => _ = Workbench.Settings.SetAsync(Browsers.SettingKey, browser, SettingScope.User, CancellationToken.None);
        Toolbar.SelectScriptDebugging = enabled => _ = Workbench.Settings.SetAsync(JavaScriptDebugSetting, enabled, SettingScope.User, CancellationToken.None);
        Toolbar.OpenDebugProperties = OpenStartupDebugProperties;
        Workbench.Settings.Changed += (_, e) =>
        {
            if (e.Keys.Any(k => k is Browsers.SettingKey or JavaScriptDebugSetting))
            {
                Ui.Post(RefreshLaunchMenu);
            }
        };
    }

    /// <summary>Project Properties of the startup project, on its Debug page (its launch profiles).</summary>
    private void OpenStartupDebugProperties()
    {
        if (Runs.StartupProject is { } project && OpenProjectProperties(project) is { } properties
            && properties.Pages.FirstOrDefault(p => p.IsLaunchProfiles) is { } debug)
        {
            properties.SelectedPage = debug;
        }
    }

    /// <summary>No drop-down choices (no solution, a folder's launch configurations); a refresh under way is dropped.</summary>
    private void ClearLaunchMenu()
    {
        Interlocked.Exchange(ref _launchMenuRefresh, null)?.Cancel();
        Toolbar.SetLaunchMenu(null);
    }

    /// <summary>
    /// Reads the startup project's launch profiles and finds the browsers off the UI thread, then gives the Start
    /// drop-down its choices; the latest call wins. Nothing to choose without a single startup project in a trusted
    /// solution (several startup projects, a folder's launch configurations).
    /// </summary>
    private void RefreshLaunchMenu()
    {
        if (Toolbar is null)
        {
            return;
        }

        if (Solutions?.Solution is not { Trusted: true } || Runs.MultipleStartup || Runs.StartupProject is not { } project
            || !Solutions.Projects.ContainsKey(project))
        {
            ClearLaunchMenu();
            return;
        }

        var cancel = new CancellationTokenSource();
        Interlocked.Exchange(ref _launchMenuRefresh, cancel)?.Cancel();

        var name = Path.GetFileNameWithoutExtension(project);
        var browser = Workbench.Settings.GetString(Browsers.SettingKey, "auto");
        var scriptDebugging = Workbench.Settings.GetBoolean(JavaScriptDebugSetting, true);
        _ = Task.Run(async () =>
        {
            LaunchMenu menu;
            try
            {
                // The profiles alone, from the file: asking the project host for its run information evaluates the
                // project again, at each project change while the solution loads.
                var (profiles, _) = await RunService.ReadProfilesAsync(project, cancel.Token).ConfigureAwait(false);
                menu = new LaunchMenu(
                    name,
                    [.. profiles.Select(p => new LaunchProfileChoice(p.Name, p.IsRunnable ? null : Localizer.Format("msg.launchMenu.profileCantRun", "'{0}' profiles can't run in Codalus IDE.", p.CommandName)))],
                    Runs.ResolveProfile(project, profiles)?.Name,
                    profiles.Any(p => p.ApplicationUrl is not null) ? WebLaunchChoices(browser, scriptDebugging) : null);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or JsonRpcRemoteException or JsonRpcConnectionLostException)
            {
                // The project host can't say now (still loading, restarting): only its Debug properties. Opening the
                // drop-down asks again.
                menu = new LaunchMenu(name, [], null, null);
            }

            Ui.Post(() =>
            {
                if (!cancel.IsCancellationRequested)
                {
                    Toolbar.SetLaunchMenu(menu);
                }
            });
        });
    }

    /// <summary>The <c>debug.browser</c> choices: automatic, the Chromium browsers (those not installed disabled), a path set by hand.</summary>
    internal static WebLaunchChoices WebLaunchChoices(string? setting, bool scriptDebugging)
    {
        setting = string.IsNullOrWhiteSpace(setting) ? "auto" : setting.Trim();
        var found = Browsers.Find(setting);
        var automatic = Browsers.Find("auto");
        List<BrowserChoice> choices =
        [
            new("auto", automatic is null ? Localizer.Get("msg.launchMenu.automatic", "Automatic") : Localizer.Format("msg.launchMenu.automaticBrowser", "Automatic ({0})", automatic.Display), true),
            .. new[] { Browsers.Chrome, Browsers.Edge, Browsers.Chromium }.Select(kind => Browsers.Find(kind) is { } browser
                ? new BrowserChoice(kind, browser.Display, true)
                : new BrowserChoice(kind, new Browser(kind, kind).Display, false)),
        ];
        if (!choices.Any(c => c.Setting == setting))
        {
            choices.Add(new BrowserChoice(setting, Path.GetFileName(setting), found is not null));
        }

        return new WebLaunchChoices(setting, found?.Display, choices, scriptDebugging);
    }
}
