using System.Text;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Http;
using Codalus.Workbench.Web;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// The ASP.NET Core loop (M15a): the Endpoints Explorer, <c>.http</c> files and their responses, the environment
/// selector, the HTTPS development certificate and user secrets.
/// </summary>
public sealed partial class ShellHost
{
    public const string HttpLanguageId = "http";

    private static readonly string[] s_environments = ["Development", "Staging", "Production"];
    private bool _devCertificateOffered;
    private Avalonia.Threading.DispatcherTimer? _endpointsRefresh;

    public HttpService Http { get; private set; } = null!;

    public WebDevelopmentService WebDevelopment { get; private set; } = null!;

    public EndpointsExplorerViewModel Endpoints { get; private set; } = null!;

    public HttpResponseViewModel HttpResponse { get; private set; } = null!;

    private void InitializeWeb()
    {
        Http = new HttpService();
        WebDevelopment = new WebDevelopmentService(() => Terminals.DotnetRoot);
        Endpoints = new EndpointsExplorerViewModel(
            ct => Languages.RunningClient(CSharpLanguageId) is { } client ? client.AspNetEndpointsAsync(null, ct) : Task.FromResult<AspNetEndpointsResult?>(null),
            OpenLocationWithHistoryAsync,
            GenerateRequestAsync);
        HttpResponse = new HttpResponseViewModel(Http, EditorFont, this)
        {
            // M34h: Send Again, the link to the request, and saving or opening the body.
            SendAgain = SendHttpRequestAgainAsync,
            OpenRequest = (file, line) => OpenFileAsync(file, preview: false, new LanguageServices.Protocol.Position(Math.Max(0, line - 1), 0)),
            SaveBody = SaveHttpBodyAsync,
            OpenBody = OpenHttpBodyAsync,
        };
        _endpointsRefresh = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _endpointsRefresh.Tick += (_, _) =>
        {
            _endpointsRefresh.Stop();
            if (Layout.IsVisible(EndpointsExplorerViewModel.ToolWindowId))
            {
                _ = Endpoints.RefreshAsync();
            }
        };
        Languages.StatusChanged += (_, e) => Ui.Post(() =>
        {
            if (e.LanguageId == CSharpLanguageId && e.Status.State == LanguageServices.Protocol.WorkspaceStates.Ready)
            {
                RestartEndpointsRefresh();
            }
        });
    }

    /// <summary>A C# file was saved: the endpoints may have changed.</summary>
    private void OnWebDocumentSaved(string path)
    {
        if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            RestartEndpointsRefresh();
        }
    }

    private void RestartEndpointsRefresh()
    {
        _endpointsRefresh?.Stop();
        _endpointsRefresh?.Start();
    }

    // ------------------------------------------------------------------ Endpoints Explorer

    public async Task ShowEndpointsAsync()
    {
        Layout.ShowToolWindow(EndpointsExplorerViewModel.ToolWindowId, focus: true);
        await Endpoints.RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Appends a request for <paramref name="endpoint"/> to the project's <c>&lt;Project&gt;.http</c> file (created with
    /// a host variable from the launch profile's first URL), and opens it at the request.
    /// </summary>
    public async Task GenerateRequestAsync(AspNetEndpoint endpoint)
    {
        var folder = Path.GetDirectoryName(endpoint.ProjectPath)!;
        var file = Path.Combine(folder, endpoint.Project + ".http");
        var variable = endpoint.Project.Replace('.', '_').Replace('-', '_') + "_HostAddress";
        var existing = File.Exists(file) ? await File.ReadAllTextAsync(file).ConfigureAwait(true) : string.Empty;
        var text = new StringBuilder(existing);
        if (!existing.Contains("@" + variable, StringComparison.Ordinal))
        {
            var host = await HostAddressAsync(endpoint.ProjectPath).ConfigureAwait(true);
            text.Insert(0, $"@{variable} = {host}\n\n");
        }

        if (text.Length > 0 && text[^1] != '\n')
        {
            text.Append('\n');
        }

        if (existing.Trim().Length > 0)
        {
            text.Append("\n###\n\n");
        }

        var method = endpoint.HttpMethod is "*" ? "GET" : endpoint.HttpMethod.Split(',')[0];
        text.Append(GeneratedRequest(method, "{{" + variable + "}}", endpoint.Route));
        var requestLine = text.ToString().Split('\n').ToList().FindLastIndex(l => l.StartsWith(method + " ", StringComparison.Ordinal));
        await File.WriteAllTextAsync(file, text.ToString().Replace("\n", Environment.NewLine, StringComparison.Ordinal)).ConfigureAwait(true);
        if (Editors.FirstOrDefault(e => SolutionPaths.Comparer.Equals(e.Model.Path, file)) is { } open && !open.Model.IsModified)
        {
            await open.Model.ReloadAsync(CancellationToken.None).ConfigureAwait(true);
        }

        await OpenFileAsync(file, preview: false, new Position(Math.Max(0, requestLine), 0)).ConfigureAwait(true);
    }

    /// <summary>A request for a route: route parameters become placeholders to fill in; POST, PUT and PATCH get a JSON body.</summary>
    public static string GeneratedRequest(string method, string host, string route)
    {
        var path = System.Text.RegularExpressions.Regex.Replace(route, @"\{\*?([A-Za-z_]\w*)[^}]*\}", "{{$1}}");
        var text = new StringBuilder($"{method} {host}{path}\nAccept: application/json\n");
        if (method is "POST" or "PUT" or "PATCH")
        {
            text.Append("Content-Type: application/json\n\n{\n}\n");
        }

        return text.ToString();
    }

    private async Task<string> HostAddressAsync(string projectPath)
    {
        try
        {
            var (plan, _, _) = await Runs.PlanAsync(projectPath, CancellationToken.None).ConfigureAwait(true);
            if (plan.Environment.TryGetValue("ASPNETCORE_URLS", out var urls))
            {
                var all = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return all.FirstOrDefault(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault() ?? "http://localhost:5000";
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException)
        {
            // No launch profile: the default Kestrel address.
        }

        return "http://localhost:5000";
    }

    // ------------------------------------------------------------------ .http files

    /// <summary>Sends the request of a .http file at <paramref name="line"/> (1-based) and shows its response.</summary>
    public async Task SendHttpRequestAsync(string path, string text, int line)
    {
        Layout.ShowToolWindow(HttpResponseViewModel.ToolWindowId, focus: false);
        StatusBar.ShowMessage(Localizer.Get("msg.web.sendingTheRequest", "Sending the request…"));
        try
        {
            var exchange = await Http.SendAsync(path, text, line, CancellationToken.None).ConfigureAwait(true);
            StatusBar.ShowMessage(exchange switch
            {
                null => "There is no request on this line.",
                { Response: { } response } => $"{response.StatusCode} {response.ReasonPhrase} in {exchange.Elapsed.TotalMilliseconds:0} ms",
                _ => "The request failed: " + exchange.Error,
            }, TimeSpan.FromSeconds(6));
        }
        catch (OperationCanceledException)
        {
            StatusBar.ClearMessage();
        }
    }

    Task IEditorServices.SendHttpRequestAsync(string path, string text, int line) => SendHttpRequestAsync(path, text, line);

    /// <summary>Send Again: the request from its .http file (the editor's text when it is open, else read off the UI thread).</summary>
    private async Task SendHttpRequestAgainAsync(HttpExchange exchange)
    {
        var text = OpenTextOf(exchange.File);
        if (text is null)
        {
            try
            {
                text = await File.ReadAllTextAsync(exchange.File).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowMessage(Localizer.Format("msg.web.httpFileUnreadable", "The request's file can't be read: {0}", ex.Message));
                return;
            }
        }

        await SendHttpRequestAsync(exchange.File, text, exchange.Line).ConfigureAwait(true);
    }

    /// <summary>Save Response Body…: the body to a file the user picks.</summary>
    private async Task SaveHttpBodyAsync(string name, string text)
    {
        if (Window is null || await Window.SaveFileAsync(Localizer.Get("msg.web.saveResponseBody", "Save Response Body"), name, Localizer.Get("msg.web.allFiles", "All files"), ["*"]).ConfigureAwait(true) is not { } path)
        {
            return;
        }

        await File.WriteAllTextAsync(path, text).ConfigureAwait(true);
    }

    /// <summary>Open in Editor: the body as a file of its own in the temporary folder, opened as a document.</summary>
    private async Task OpenHttpBodyAsync(string name, string text)
    {
        var folder = Path.Combine(Path.GetTempPath(), "codalus-http", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        await File.WriteAllTextAsync(path, text).ConfigureAwait(true);
        await OpenFileAsync(path, preview: false).ConfigureAwait(true);
    }

    /// <summary>Ctrl+Alt+R in a .http file: sends the request at the caret.</summary>
    public Task SendHttpRequestAtCaretAsync()
    {
        if (ActiveEditor is not { Controller: { } controller } editor || editor.Model.LanguageId != HttpLanguageId)
        {
            ShowMessage("Open a .http file and put the caret in a request.");
            return Task.CompletedTask;
        }

        return SendHttpRequestAsync(editor.Model.Path, editor.Model.Text.Text, controller.Editor.TextArea.Caret.Line);
    }

    /// <summary>Picks the environment (from http-client.env.json) for the active .http file.</summary>
    public async Task SelectHttpEnvironmentAsync()
    {
        if (ActiveEditor is not { } editor || editor.Model.LanguageId != HttpLanguageId)
        {
            ShowMessage("Open a .http file to choose its environment.");
            return;
        }

        var environments = HttpEnvironments.Load(editor.Model.Path);
        if (environments.Names.Count == 0)
        {
            ShowMessage("No http-client.env.json next to this file or in a parent folder. Add one to define environments.");
            return;
        }

        var current = Http.EnvironmentFor(environments.File) ?? environments.Names[0];
        var items = environments.Names.Select(n => new QuickPickItem(n) { Tag = n, Detail = n == current ? "current" : null }).ToList();
        if (await Palette.ShowAsync(items, "Environment for .http requests").ConfigureAwait(true) is { Tag: string chosen })
        {
            Http.SelectEnvironment(environments.File, chosen);
            StatusBar.ShowMessage(Localizer.Format("msg.web.requestsUseTheEnvironment", "Requests use the '{0}' environment.", chosen), TimeSpan.FromSeconds(4));
        }
    }

    // ------------------------------------------------------------------ environment, certificate, secrets

    /// <summary>The ASP.NET Core environment for runs and debug sessions (over the launch profile's).</summary>
    public async Task SelectAspNetEnvironmentAsync(string? argument = null)
    {
        var chosen = argument;
        if (chosen is null)
        {
            var current = Runs.EnvironmentOverride;
            var items = new List<QuickPickItem> { new("Use the launch profile's") { Tag = string.Empty, Detail = current is null ? "current" : null } };
            items.AddRange(s_environments.Select(e => new QuickPickItem(e) { Tag = e, Detail = e == current ? "current" : null }));
            items.Add(new QuickPickItem("Other…") { Tag = "?" });
            if (await Palette.ShowAsync(items, "ASP.NET Core environment for Run and Debug").ConfigureAwait(true) is not { Tag: string tag })
            {
                return;
            }

            chosen = tag;
            if (tag == "?")
            {
                var other = await Dialogs.ShowAsync(new DialogRequest("ASP.NET Core Environment", "The environment's name (ASPNETCORE_ENVIRONMENT):", ["OK", "Cancel"])
                {
                    Input = current ?? string.Empty,
                    Validate = text => text.Trim().Length == 0 ? "Enter a name." : null,
                }).ConfigureAwait(true);
                if (other.Button != "OK")
                {
                    return;
                }

                chosen = other.Input!.Trim();
            }
        }

        Runs.EnvironmentOverride = string.IsNullOrWhiteSpace(chosen) ? null : chosen.Trim();
        SaveEditorSession();
        StatusBar.ShowMessage(Runs.EnvironmentOverride is { } environment
            ? $"Run and Debug use the '{environment}' environment."
            : "Run and Debug use the launch profile's environment.", TimeSpan.FromSeconds(5));
    }

    /// <summary>Checks the HTTPS development certificate and offers to trust it.</summary>
    public async Task CheckDevCertificateAsync()
    {
        StatusBar.ShowMessage(Localizer.Get("msg.web.checkingTheHttpsDevelopmentCertificate", "Checking the HTTPS development certificate…"));
        var state = await WebDevelopment.CheckCertificateAsync(CancellationToken.None).ConfigureAwait(true);
        StatusBar.ClearMessage();
        switch (state)
        {
            case DevCertificateState.Trusted:
                Workbench.Notifications.Info(Localizer.Get("msg.web.theAspNetCoreHttpsDevelopment", "The ASP.NET Core HTTPS development certificate is installed and trusted."));
                break;
            case DevCertificateState.Unknown:
                Workbench.Notifications.Warning(Localizer.Get("msg.web.theCertificateCouldNotBeChecked", "The certificate could not be checked: the .NET SDK was not found."));
                break;
            default:
                await OfferTrustAsync(state).ConfigureAwait(true);
                break;
        }
    }

    /// <summary>Before running a project with an https URL, once per session: offers to trust the development certificate.</summary>
    private async Task OfferDevCertificateAsync(string project)
    {
        if (_devCertificateOffered)
        {
            return;
        }

        try
        {
            var (plan, _, _) = await Runs.PlanAsync(project, CancellationToken.None).ConfigureAwait(true);
            if (!plan.Environment.TryGetValue("ASPNETCORE_URLS", out var urls) || !urls.Contains("https://", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _devCertificateOffered = true;
            var state = await WebDevelopment.CheckCertificateAsync(CancellationToken.None).ConfigureAwait(true);
            if (state is DevCertificateState.Missing or DevCertificateState.Untrusted)
            {
                await OfferTrustAsync(state).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or System.ComponentModel.Win32Exception)
        {
            // Not a web project, or no SDK: nothing to offer.
        }
    }

    private async Task OfferTrustAsync(DevCertificateState state)
    {
        var message = state == DevCertificateState.Missing
            ? "There is no ASP.NET Core HTTPS development certificate, so https URLs won't work."
            : "The ASP.NET Core HTTPS development certificate isn't trusted, so browsers will warn about https URLs.";
        if (await Workbench.Notifications.Warning(message, "Trust the Certificate", "Not Now").Result.ConfigureAwait(true) == "Trust the Certificate")
        {
            await TrustDevCertificateAsync().ConfigureAwait(true);
        }
    }

    public async Task TrustDevCertificateAsync()
    {
        StatusBar.ShowMessage(Localizer.Get("msg.web.trustingTheHttpsDevelopmentCertificateYour", "Trusting the HTTPS development certificate (your OS may ask you to confirm)…"));
        var (exitCode, output) = await WebDevelopment.TrustCertificateAsync(CancellationToken.None).ConfigureAwait(true);
        StatusBar.ClearMessage();
        BuildHostLog.AppendLine("> dotnet dev-certs https --trust");
        BuildHostLog.AppendLine(output.TrimEnd());
        if (exitCode == 0)
        {
            Workbench.Notifications.Info("The HTTPS development certificate is trusted." + (OperatingSystem.IsLinux() ? " " + WebDevelopmentService.LinuxTrustGuidance : string.Empty));
        }
        else
        {
            Workbench.Notifications.Error($"dotnet dev-certs https --trust failed (exit code {exitCode}); see the Build Host output." + (OperatingSystem.IsLinux() ? " " + WebDevelopmentService.LinuxTrustGuidance : string.Empty));
        }
    }

    /// <summary>Opens the project's secrets.json (adding a UserSecretsId first when it has none).</summary>
    public async Task ManageUserSecretsAsync(string? project = null)
    {
        project ??= Explorer.Selected?.Node.ProjectPath ?? (ActiveEditor is { } editor ? Solutions.Projects.Values.FirstOrDefault(p => editor.Model.Path.StartsWith(Path.GetDirectoryName(p.Path)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))?.Path : null) ?? Runs.StartupProject;
        if (project is null)
        {
            ShowMessage("Select a project in the Solution Explorer to manage its user secrets.");
            return;
        }

        try
        {
            var file = await WebDevelopment.SecretsFileAsync(project, CancellationToken.None).ConfigureAwait(true);
            await OpenFileAsync(file, preview: false, at: null).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.web.couldNotOpenTheUserSecrets", "Could not open the user secrets of {0}: {1}", Path.GetFileNameWithoutExtension(project), ex.Message));
        }
    }
}
