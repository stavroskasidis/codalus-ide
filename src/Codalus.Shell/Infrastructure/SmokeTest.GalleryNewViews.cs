using Avalonia.Controls;
using Avalonia.VisualTree;
using Codalus.Shell.Docking;
using Codalus.Shell.Theming;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Settings;

namespace Codalus.Shell.Infrastructure;

/// <summary>
/// M34g: the views M40–M47 added, in the gallery, in their states: Code Search's floating window (empty, nothing found,
/// Feature Search, results with the preview), the WCF Test Client (empty, a missing address, a WSDL file's form, then in
/// <c>wcf.testClient</c> a response and a fault), the first-run setup's four steps, Options' color theme cards and the
/// Service References document's WCF page; on Windows, the Windows Forms designer with the Toolbox and the Properties
/// window (<c>winforms.designer</c>) and the WPF previewer (<c>wpf.preview</c>). M43's (<c>netfx.projects</c>): Solution
/// Explorer with a project that can't build, Convert to SDK-Style's preview, the Error List's reason, and on Windows the
/// Call Stack and Modules of a desktop-CLR session. The tool windows among them (the
/// Toolbox, the Live Visual Tree and Property Explorer, the XAML Previewer, Code Search docked) are in the tool window
/// sweep already.
/// </summary>
internal sealed partial class SmokeTest
{
    /// <summary>Before a solution opens (<c>shell.galleryEmpty</c>).</summary>
    private async Task GalleryNewViewsEmptyAsync()
    {
        // Code Search: what to type, nothing found, and Feature Search's commands (they need no solution).
        await GalleryCodeSearchAsync([("empty", string.Empty, CodeSearchViewModel.CodeTab), ("no-results", "zqxwv", CodeSearchViewModel.CodeTab), ("feature-search", "build", CodeSearchViewModel.FeatureTab)]).ConfigureAwait(true);

        // The WCF Test Client: empty, a missing address, then the samples' WSDL file read and an operation's form.
        var client = shell.OpenWcfTestClient();
        var document = shell.Layout.ActiveDocument;
        try
        {
            await GalleryDocumentAsync("wcf-test-client", "empty", document).ConfigureAwait(true);
            await client.LoadAsync().ConfigureAwait(true);
            await GalleryDocumentAsync("wcf-test-client", "error", document).ConfigureAwait(true);
            var wsdl = Path.Combine(samples, "Wcf", "contracts", "CalculatorService.wsdl");
            if (File.Exists(wsdl))
            {
                client.Address = wsdl;
                await client.LoadAsync().ConfigureAwait(true);
                if (client.Operations.Any(o => o.Name == "Greet"))
                {
                    client.Select("Greet");
                }

                await GalleryDocumentAsync("wcf-test-client", "form", document).ConfigureAwait(true);
            }
        }
        finally
        {
            if (document is not null)
            {
                shell.Layout.CloseDocument(document);
            }
        }

        // The first-run setup (M46), opened as from Help, each step; the settings it may touch are put back.
        var settings = shell.Workbench.Settings;
        var before = s_setupKeys.ToDictionary(k => k, k => settings.GetLayerValue(k, SettingScope.User));
        try
        {
            shell.ShowSetup(firstStart: false);
            await WaitAsync(() => shell.IsSetupOpen, "the setup opens for the gallery").ConfigureAwait(true);
            var model = shell.SetupModel!;
            var setup = shell.SetupWindowView!;
            foreach (var step in new[] { "appearance", "keyboard", "settings", "tools" })
            {
                if (step == "tools")
                {
                    await SoftWaitAsync(() => model.Tools.Count > 0, 90).ConfigureAwait(true);
                }

                await GalleryThemesAsync(async theme =>
                {
                    await Task.Delay(100).ConfigureAwait(true);
                    await RenderedAsync(setup).ConfigureAwait(true);
                    GalleryShot("setup-" + step, "empty", theme, null, setup);
                }).ConfigureAwait(true);
                if (step != "tools")
                {
                    await model.NextAsync().ConfigureAwait(true);
                }
            }
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
        }
    }

    /// <summary>With the Testing solution open (<c>shell.gallery</c>).</summary>
    private async Task GalleryNewViewsDataAsync()
    {
        // Code Search's results, the first selected and previewed (the index may still be building: then the files).
        await SoftWaitAsync(() => shell.CodeSearchIndex.IsComplete, 60).ConfigureAwait(true);
        await GalleryCodeSearchAsync([("data", "Calculator", CodeSearchViewModel.CodeTab)]).ConfigureAwait(true);

        // Options' color theme cards (M45).
        shell.OpenSettings(ThemeService.SettingKey);
        if (shell.Layout.ActiveDocument is SettingsDocument options)
        {
            await GalleryDocumentAsync("options-color-theme", "data", options).ConfigureAwait(true);
            shell.Layout.CloseDocument(options);
        }

        // The Service References document's WCF page (M42): dotnet-svcutil's state and the add form.
        if (shell.Solutions.Projects.Keys.FirstOrDefault() is { } project && shell.OpenServiceReferences(project, ServiceReferencesViewModel.WcfKind) is { } references)
        {
            var document = shell.Layout.ActiveDocument;
            await SoftWaitAsync(() => !references.IsBusy && references.WcfStatus is not null, 120).ConfigureAwait(true);
            await GalleryDocumentAsync("service-references-wcf", "data", document).ConfigureAwait(true);
            if (document is not null)
            {
                shell.Layout.CloseDocument(document);
            }
        }
    }

    /// <summary>The WCF Test Client with the running service's response, then a fault (<c>wcf.testClient</c>).</summary>
    private async Task GalleryWcfResponseAsync(WcfTestClientViewModel client)
    {
        var document = shell.Layout.ActiveDocument;
        await GalleryDocumentAsync("wcf-test-client", "data", document).ConfigureAwait(true);
        if (client.Operations.Any(o => o.Name == "Divide"))
        {
            client.Select("Divide", Workbench.Wcf.SoapVersion.Soap11);
            client.Row("a")!.Value = "1";
            client.Row("b")!.Value = "0";
            await client.SendAsync().ConfigureAwait(true);
            await GalleryDocumentAsync("wcf-test-client", "fault", document).ConfigureAwait(true);
        }

        WriteGalleryIndex();
    }

    /// <summary>The Windows Forms designer with a new button selected, the Toolbox and the Properties window (Windows).</summary>
    private async Task GalleryWinFormsAsync(WinFormsDesignerDocument designer)
    {
        await GalleryThemesAsync(async theme =>
        {
            GalleryShot("winforms-designer", "data", theme, PaneOf(designer));
            GalleryTool(ToolboxViewModel.ToolWindowId, "data", theme);
            GalleryTool(PropertiesWindowViewModel.ToolWindowId, "data", theme);
            await Task.CompletedTask.ConfigureAwait(true);
        }, settle: true).ConfigureAwait(true);
        WriteGalleryIndex();
    }

    /// <summary>The XAML Previewer showing the WPF app's window (Windows).</summary>
    private async Task GalleryWpfPreviewAsync()
    {
        await GalleryThemesAsync(theme =>
        {
            GalleryTool(XamlPreviewViewModel.ToolWindowId, "wpf", theme);
            return Task.CompletedTask;
        }, settle: true).ConfigureAwait(true);
        WriteGalleryIndex();
    }

    /// <summary>
    /// M43 in the gallery (<c>netfx.projects</c>, every OS): Solution Explorer with the old-style project and the reason
    /// a project can't build, Convert to SDK-Style's preview, and the Error List after building that project alone.
    /// </summary>
    private async Task GalleryNetFrameworkAsync(string legacy, string web)
    {
        shell.Explorer.Reveal("p:" + web + "|cannotBuild");
        await GalleryThemesAsync(theme =>
        {
            GalleryTool(SolutionExplorerViewModel.ToolWindowId, "netfx", theme);
            return Task.CompletedTask;
        }, settle: true).ConfigureAwait(true);

        var converting = shell.ConvertToSdkStyleAsync(legacy);
        if (await SoftWaitAsync(() => shell.Dialogs.IsOpen && shell.Dialogs.HasDetails, 60).ConfigureAwait(true))
        {
            await GalleryThemesAsync(async theme =>
            {
                await SettleAsync().ConfigureAwait(true);
                GalleryShot("netfx-convert-preview", "data", theme, null);
            }).ConfigureAwait(true);
        }

        shell.Dialogs.Cancel();
        await converting.ConfigureAwait(true);

        // Building the project that needs Visual Studio's targets alone fails, saying why in the Error List.
        Check(!await shell.BuildAsync(Codalus.Projects.Protocol.BuildTargets.Build, web).ConfigureAwait(true), "LegacyWebApp's build fails here");
        await SoftWaitAsync(() => shell.ErrorList.Rows.Any(r => r.IsError && r.Project == "LegacyWebApp"), 30).ConfigureAwait(true);
        await GalleryThemesAsync(theme =>
        {
            GalleryTool(ErrorListViewModel.ToolWindowId, "netfx", theme);
            return Task.CompletedTask;
        }, settle: true).ConfigureAwait(true);
        WriteGalleryIndex();
    }

    /// <summary>The desktop-CLR debugger stopped at a breakpoint: the Call Stack and the Modules window (Windows, <c>netfx.projects</c>).</summary>
    private async Task GalleryNetFrameworkDebuggingAsync()
    {
        await GalleryThemesAsync(theme =>
        {
            GalleryTool(CallStackViewModel.ToolWindowId, "netfx", theme);
            GalleryTool(ModulesViewModel.ToolWindowId, "netfx", theme);
            return Task.CompletedTask;
        }, settle: true).ConfigureAwait(true);
        WriteGalleryIndex();
    }

    /// <summary>Code Search's floating window, a picture of it per state and theme.</summary>
    private async Task GalleryCodeSearchAsync(IReadOnlyList<(string State, string Query, int Tab)> states)
    {
        var search = shell.CodeSearch;
        shell.Run("workbench.action.gotoAll");
        await WaitAsync(() => search.IsFloatingOpen, "Code Search opens for the gallery").ConfigureAwait(true);
        try
        {
            foreach (var (state, query, tab) in states)
            {
                search.TabIndex = tab;
                search.Query = query;
                await SoftWaitAsync(() => !search.IsSearching && (query.Length == 0 || search.Rows.Count > 0 || state == "no-results"), 30).ConfigureAwait(true);
                if (state == "data")
                {
                    await SoftWaitAsync(() => shell.CodeSearchPreview is not null, 15).ConfigureAwait(true);
                }

                var view = window.GetVisualDescendants().OfType<CodeSearchView>().FirstOrDefault(v => v.IsEffectivelyVisible && !v.IsToolWindow);
                await GalleryThemesAsync(async theme =>
                {
                    await SettleAsync().ConfigureAwait(true);
                    GalleryShot("code-search", state, theme, view);
                }).ConfigureAwait(true);
            }
        }
        finally
        {
            search.Close();
            search.TabIndex = CodeSearchViewModel.CodeTab;
        }
    }

    /// <summary>A document's pane in each theme.</summary>
    private Task GalleryDocumentAsync(string surface, string state, Dock.Model.Core.IDockable? document) =>
        document is null ? Task.CompletedTask : GalleryThemesAsync(async theme =>
        {
            await SettleAsync().ConfigureAwait(true);
            GalleryShot(surface, state, theme, PaneOf(document));
        });

    /// <summary>Waits for <paramref name="condition"/> without failing the step: a gallery picture is taken either way.</summary>
    private static async Task<bool> SoftWaitAsync(Func<bool> condition, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                return false;
            }

            await Task.Delay(100).ConfigureAwait(true);
        }

        return true;
    }
}
