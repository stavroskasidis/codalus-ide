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
                    FillGreet(client); // M34h: the design's form, a list with two items
                }

                await GalleryDocumentAsync("wcf-test-client", "form", document).ConfigureAwait(true);
                await GalleryWcfMockedAsync(client, wsdl, document).ConfigureAwait(true);
            }
        }
        finally
        {
            if (document is not null)
            {
                shell.Layout.CloseDocument(document);
            }
        }

        // New Project's pages (0.6.0, ADR 0082): the templates, no match, a pack to install, a reserved name, Blazor's options.
        await GalleryNewProjectAsync(solutionOpen: false).ConfigureAwait(true);

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

        // New Project with a solution open (0.6.0): Configure's Solution choice.
        await GalleryNewProjectAsync(solutionOpen: true).ConfigureAwait(true);

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

    /// <summary>
    /// New Project's window (0.6.0, ADR 0082) in each gallery theme, as its design's states: without a solution the
    /// templates (Console App chosen), no match, a template pack to install, a reserved name and Blazor's options with
    /// Individual Accounts; with one, Configure's Solution choice. The window closes after.
    /// </summary>
    private async Task GalleryNewProjectAsync(bool solutionOpen)
    {
        shell.Run("templates.newProject");
        if (!await SoftWaitAsync(() => shell.NewProjectModel is { IsLoading: false, Templates.Count: > 0 } && shell.NewProjectWindowView is { IsVisible: true }, 120).ConfigureAwait(true))
        {
            shell.NewProjectModel?.Cancel();
            return;
        }

        var model = shell.NewProjectModel!;
        var view = shell.NewProjectWindowView!;
        model.SearchDelay = TimeSpan.Zero;
        model.ValidationDelay = TimeSpan.Zero;
        Task Shoot(string page, string state) => GalleryThemesAsync(async theme =>
        {
            await Task.Delay(100).ConfigureAwait(true);
            await RenderedAsync(view).ConfigureAwait(true);
            GalleryShot("newproject-" + page, state, theme, null, view);
        });

        async Task<bool> ChooseAsync(string shortName)
        {
            if (model.Templates.FirstOrDefault(r => r.Group.ShortName == shortName && r.Group.Language == "C#") is not { } row)
            {
                return false;
            }

            model.SelectedTemplate = row;
            await model.WaitForTemplateAsync().ConfigureAwait(true);
            return true;
        }

        async Task<bool> ConfigureAsync(string shortName)
        {
            if (!await ChooseAsync(shortName).ConfigureAwait(true))
            {
                return false;
            }

            await model.GoForwardAsync().ConfigureAwait(true);
            return await SoftWaitAsync(() => model.Page == NewProjectPage.Configure && !model.IsValidating, 30).ConfigureAwait(true);
        }

        try
        {
            if (solutionOpen)
            {
                if (await ConfigureAsync("console").ConfigureAwait(true))
                {
                    await Shoot("configure", "solution").ConfigureAwait(true);
                }

                return;
            }

            if (await ChooseAsync("console").ConfigureAwait(true))
            {
                await Shoot("choose", "data").ConfigureAwait(true);
            }

            model.Search = "zqxwv";
            await Shoot("choose", "nomatch").ConfigureAwait(true);
            model.ClearAllCommand.Execute(null);
            if (model.Templates.FirstOrDefault(r => r.IsPack) is { } pack)
            {
                model.SelectedTemplate = pack;
                await Shoot("choose", "install").ConfigureAwait(true);
            }

            if (await ConfigureAsync("blazor").ConfigureAwait(true))
            {
                var name = model.ProjectName;
                model.ProjectName = "CON";
                await Shoot("configure", "error").ConfigureAwait(true);
                model.ProjectName = name;
                if (await SoftWaitAsync(() => model.CanGoNext, 30).ConfigureAwait(true))
                {
                    await model.GoForwardAsync().ConfigureAwait(true);
                    if (model.AuthChoices.FirstOrDefault(a => a.Value == "Individual") is { } individual)
                    {
                        model.SelectedAuth = individual;
                    }

                    await Shoot("additional", "data").ConfigureAwait(true);
                }
            }
        }
        finally
        {
            model.Cancel();
            await SoftWaitAsync(() => !shell.IsNewProjectOpen, 10).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// M34h: what Find Results and the HTTP Response show in the gallery, as their designs do: two Find Results pages (the
    /// first kept) with the Testing solution's matches grouped by path and file, one selected; and a history of requests
    /// from a .http file to a loopback mock (a JSON list, a created item, a 404, a refused connection).
    /// </summary>
    private async Task GallerySampleResultsAsync()
    {
        var find = shell.FindReplace;
        var results = shell.FindResults;
        try
        {
            find.MatchCase = true;
            find.WholeWord = true;
            find.LookIn = FindScopes.EntireSolution;
            find.FileTypes = "*.cs";
            find.Query = "Divide";
            await find.FindAllAsync().ConfigureAwait(true);
            results.CurrentPage.KeepResults = true;
            find.Query = "Add";
            await find.FindAllAsync().ConfigureAwait(true);
            var page = results.CurrentPage;
            page.FilesGroupBy = FindResultsGrouping.PathAndFile;
            page.SelectedRow = page.Rows.Where(r => !r.IsHeader).Skip(2).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            shell.Workbench.Log.AppendLine($"[smoke] gallery: Find Results' sample search failed: {ex.Message}");
        }
        finally
        {
            find.Close();
        }

        const string Todos = """[{"id":1,"title":"Write the fixture","done":false},{"id":2,"title":"Test the endpoints","done":false},{"id":3,"title":"From the .http file","done":false}]""";
        await using var server = new Tests.MockHttpServer(request => request.Method == "POST"
            ? Tests.MockHttpResponse.Json("""{"id":4,"title":"From the .http file","done":false}""", 201)
            : request.Path.EndsWith("/42", StringComparison.Ordinal) ? Tests.MockHttpResponse.NotFound() : Tests.MockHttpResponse.Json(Todos));
        var file = Path.Combine(Work, "Http", "WebApi.http");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var text = $"""
            @host = {server.BaseUrl}

            GET http://127.0.0.1:1/ping

            ###
            GET {"{{host}}"}/api/todos/42

            ###
            # @name create
            POST {"{{host}}"}/api/todos/
            Content-Type: application/json

            {"{"}"title": "From the .http file"{"}"}

            ###
            GET {"{{host}}"}/api/todos/
            Accept: application/json

            """;
        await File.WriteAllTextAsync(file, text).ConfigureAwait(true);
        shell.Http.ClearHistory();
        var lines = text.Split('\n');
        foreach (var request in new[] { "GET http://127.0.0.1:1/ping", "GET {{host}}/api/todos/42", "POST {{host}}/api/todos/", "GET {{host}}/api/todos/" })
        {
            var line = Array.FindIndex(lines, l => l.TrimEnd('\r') == request) + 1;
            if (line > 0)
            {
                await shell.SendHttpRequestAsync(file, text, line).ConfigureAwait(true);
            }
        }

        await SoftWaitAsync(() => shell.HttpResponse.History.Count >= 4, 30).ConfigureAwait(true);
        shell.HttpResponse.Selected = shell.HttpResponse.History.FirstOrDefault();
    }

    /// <summary>Removes the gallery's sample requests and Find Results pages (and the HTTP Response, if it wasn't open).</summary>
    private void ClearGallerySampleResults(bool httpWasOpen)
    {
        shell.Http.ClearHistory();
        if (!httpWasOpen)
        {
            shell.Layout.CloseToolWindow(HttpResponseViewModel.ToolWindowId);
        }

        var results = shell.FindResults;
        foreach (var page in results.Pages.ToList())
        {
            results.Close(page);
        }

        Check(shell.Http.History.Count == 0 && results.Pages.Count == 1 && results.Rows.Count == 0, "the gallery's sample requests and searches are cleared");
    }

    /// <summary>Greet's form as the design shows it: Ada, 36, Happy, and the tags math and engines.</summary>
    private static void FillGreet(WcfTestClientViewModel client)
    {
        if (client.Row("person.Name") is not { } name)
        {
            return;
        }

        name.Value = "Ada";
        client.Row("person.Age")!.Value = "36";
        client.Row("person.Mood")!.Value = "Happy";
        if (client.Row("person.Tags.string") is { CanAdd: true } tags)
        {
            tags.AddCommand.Execute(null);
            client.Rows[^1].Value = "math";
            tags = client.Row("person.Tags.string")!;
            tags.AddCommand.Execute(null);
            client.Rows[^1].Value = "engines";
        }
    }

    /// <summary>
    /// M34h: the test client's response and fault without a running service (shell.galleryEmpty), from a loopback mock
    /// that serves the samples' WSDL and answers as CoreWCF does; <c>wcf.testClient</c> photographs the real service's.
    /// </summary>
    private async Task GalleryWcfMockedAsync(WcfTestClientViewModel client, string wsdl, Dock.Model.Core.IDockable? document)
    {
        const string Greet = """<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><GreetResponse xmlns="http://samples.codalus.dev/calculator"><GreetResult>Hello Ada, 36 and happy (math, engines)!</GreetResult></GreetResponse></s:Body></s:Envelope>""";
        const string Fault = """<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>Division by zero</faultstring><detail><CalculationFault xmlns="http://samples.codalus.dev/calculator" xmlns:i="http://www.w3.org/2001/XMLSchema-instance"><Operation>Divide</Operation><Problem>Division by zero</Problem></CalculationFault></detail></s:Fault></s:Body></s:Envelope>""";
        Tests.MockHttpServer? server = null;
        server = new Tests.MockHttpServer(request => request.Method == "GET"
            ? Tests.MockHttpResponse.Text(File.ReadAllText(wsdl).Replace("http://localhost:5280/", server!.BaseUrl + "/", StringComparison.Ordinal), contentType: "text/xml; charset=utf-8")
            : request.Body.Contains("<Divide", StringComparison.Ordinal)
                ? Tests.MockHttpResponse.Text(Fault, 500, "text/xml; charset=utf-8")
                : Tests.MockHttpResponse.Text(Greet, 200, "text/xml; charset=utf-8"));
        await using (server.ConfigureAwait(true))
        {
            client.Address = server.BaseUrl + "/CalculatorService.svc?wsdl";
            await client.LoadAsync().ConfigureAwait(true);
            if (client.Error is not null || !client.Operations.Any(o => o.Name == "Greet"))
            {
                return;
            }

            client.Select("Greet", Workbench.Wcf.SoapVersion.Soap11);
            FillGreet(client);
            await client.SendAsync().ConfigureAwait(true);
            await GalleryDocumentAsync("wcf-test-client", "data", document).ConfigureAwait(true);
            client.Select("Divide", Workbench.Wcf.SoapVersion.Soap11);
            client.Row("a")!.Value = "10";
            client.Row("b")!.Value = "0";
            await client.SendAsync().ConfigureAwait(true);
            await GalleryDocumentAsync("wcf-test-client", "fault", document).ConfigureAwait(true);
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

    /// <summary>
    /// The Windows Forms designer with a new button selected, the Toolbox with the Button armed (drawn as the selection,
    /// the designer's status line saying what a click does: #66) and the Properties window (Windows).
    /// </summary>
    private async Task GalleryWinFormsAsync(WinFormsDesignerDocument designer)
    {
        shell.Toolbox.Armed = shell.Toolbox.Items.FirstOrDefault(i => i.Type == "System.Windows.Forms.Button");
        try
        {
            await GalleryThemesAsync(async theme =>
            {
                GalleryShot("winforms-designer", "data", theme, PaneOf(designer));
                GalleryTool(ToolboxViewModel.ToolWindowId, "data", theme);
                GalleryTool(PropertiesWindowViewModel.ToolWindowId, "data", theme);
                await Task.CompletedTask.ConfigureAwait(true);
            }, settle: true).ConfigureAwait(true);
        }
        finally
        {
            shell.Toolbox.Disarm();
        }

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
