using System.Text.Json;
using DaedalusStudio.Rpc;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Languages;
using DaedalusStudio.Workbench.Node;
using DaedalusStudio.Workbench.Notifications;

namespace DaedalusStudio.Shell;

/// <summary>
/// HTML, CSS and JavaScript/TypeScript language services (M16b, ADR 0024): VS Code's HTML and CSS servers and
/// <c>typescript-language-server</c>, run on a pinned Node.js runtime that is downloaded, with the servers, the first
/// time a web file is opened and the user agrees (or always, with the <c>web.languageServers</c> setting).
/// </summary>
public sealed partial class ShellHost
{
    public const string WebLanguageServersSetting = "web.languageServers";

    /// <summary>Points the Node.js and language server downloads at another folder (tests share one).</summary>
    public const string NodeCacheVariable = "DAEDALUS_NODE_CACHE";

    private static readonly (string Server, string Name, string[] Extensions, string[] Languages)[] s_webServers =
    [
        ("html", "HTML", [".html", ".htm"], ["html"]),
        ("css", "CSS", [".css", ".scss", ".less"], ["css", "scss", "less"]),
        ("typescript", "JavaScript and TypeScript", [".js", ".mjs", ".cjs", ".ts", ".mts", ".cts", ".jsx", ".tsx"], ["javascript", "typescript", "javascriptreact", "typescriptreact"]),
    ];

    private bool _webLanguagesOffered;
    private Task? _webLanguagesInstall;

    public NodeRuntime Node { get; private set; } = null!;

    public WebLanguageServers WebServers { get; private set; } = null!;

    /// <summary>Whether a language is served by one of the web language servers.</summary>
    public static bool IsWebLanguage(string languageId) => s_webServers.Any(s => s.Languages.Contains(languageId, StringComparer.Ordinal));

    private void InitializeWebLanguages()
    {
        var cache = Environment.GetEnvironmentVariable(NodeCacheVariable) is { Length: > 0 } shared ? shared : Paths.Cache;
        Node = new NodeRuntime(cache);
        WebServers = new WebLanguageServers(Node, Path.Combine(AppContext.BaseDirectory, "web-servers"), cache);
        InitializeBrowserDebugging(cache);
        foreach (var (server, name, extensions, languages) in s_webServers)
        {
            Workbench.LanguageServers.Register(new LanguageServiceDescriptor(server, name, extensions, null, () => WebServerLaunch(server))
            {
                Languages = languages,
                InitializationOptions = WebServerOptions(server),
            });
        }

        // ESLint (ADR 0027): a linter next to the TypeScript server, for files under an ESLint configuration.
        Workbench.LanguageServers.Register(new LanguageServiceDescriptor(EslintServerId, "ESLint", [], null,
            () => WebServers.IsInstalled ? new ProcessLaunchInfo(WebServers.NodePath, [WebServers.EslintServer, "--stdio"]) : null)
        {
            Languages = s_webServers.Single(s => s.Server == "typescript").Languages,
            IsLinter = true,
            Accepts = path => Workbench.Settings.GetBoolean(Eslint.Setting, true) && Eslint.ConfigFolder(path) is not null,
            ScopedConfiguration = (_, scope) => Eslint.Settings(scope),
            AcceptedRequests = Eslint.ServerRequests,
        });
    }

    public const string EslintServerId = "eslint";

    /// <summary>How a web server starts, or null while it isn't installed (the service tries again after the install).</summary>
    private ProcessLaunchInfo? WebServerLaunch(string server)
    {
        if (!WebServers.IsInstalled)
        {
            return null;
        }

        var script = server switch
        {
            "html" => WebServers.HtmlServer,
            "css" => WebServers.CssServer,
            _ => WebServers.TypeScriptServer,
        };
        return new ProcessLaunchInfo(WebServers.NodePath, [script, "--stdio"]);
    }

    private JsonElement WebServerOptions(string server)
    {
        var options = server switch
        {
            "html" => new System.Text.Json.Nodes.JsonObject
            {
                ["provideFormatter"] = true,
                ["embeddedLanguages"] = new System.Text.Json.Nodes.JsonObject { ["css"] = true, ["javascript"] = true },
            },
            "css" => new System.Text.Json.Nodes.JsonObject { ["provideFormatter"] = true },
            _ => new System.Text.Json.Nodes.JsonObject
            {
                ["hostInfo"] = "daedalus",
                ["tsserver"] = new System.Text.Json.Nodes.JsonObject { ["path"] = Path.Combine(WebServers.TypeScriptLibrary, "tsserver.js") },
            },
        };
        using var document = JsonDocument.Parse(options.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>A web file opened without a running server: install the servers (asking first, per the setting).</summary>
    private async Task OfferWebLanguagesAsync(string languageId)
    {
        if (!IsWebLanguage(languageId) || WebServers.IsInstalled || !WebServers.IsAvailable || NodeRuntime.Platform is null)
        {
            return;
        }

        var mode = Workbench.Settings.GetString(WebLanguageServersSetting, "ask");
        if (mode == "off" || _webLanguagesInstall is not null)
        {
            return;
        }

        if (mode != "install")
        {
            if (_webLanguagesOffered)
            {
                return;
            }

            _webLanguagesOffered = true;
            var choice = await Workbench.Notifications.Show(NotificationSeverity.Info,
                "Completion, errors and navigation in HTML, CSS, JavaScript and TypeScript need Node.js and the web language servers, which Daedalus Studio downloads once (about 70 MB, into its cache folder).",
                ["Install", "Not Now", "Never"]).Result.ConfigureAwait(true);
            if (choice == "Never")
            {
                await Workbench.Settings.SetAsync(WebLanguageServersSetting, "off", DaedalusStudio.Workbench.Settings.SettingScope.User, CancellationToken.None).ConfigureAwait(true);
                return;
            }

            if (choice != "Install")
            {
                return;
            }
        }

        await InstallWebLanguagesAsync().ConfigureAwait(true);
    }

    /// <summary>Downloads Node.js and the web language servers, then starts them for the open web files.</summary>
    public Task InstallWebLanguagesAsync() => _webLanguagesInstall ??= InstallWebLanguagesCoreAsync();

    private async Task InstallWebLanguagesCoreAsync()
    {
        var progress = new Progress<string>(message =>
        {
            StatusBar.ShowMessage(message);
            LanguageLog.AppendLine("[web] " + message);
        });
        try
        {
            await Task.Run(() => WebServers.EnsureAsync(progress, CancellationToken.None)).ConfigureAwait(true);
            StatusBar.ShowMessage("The web language services are ready.", TimeSpan.FromSeconds(5));
            foreach (var (server, _, _, _) in s_webServers)
            {
                await Languages.RetryAsync(server).ConfigureAwait(true);
            }

            await Languages.RetryAsync(EslintServerId).ConfigureAwait(true);

            RefreshEditors();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or PlatformNotSupportedException or TaskCanceledException or UnauthorizedAccessException)
        {
            StatusBar.ClearMessage();
            LanguageLog.AppendLine("[web] The install failed: " + ex);
            Workbench.Notifications.Error("The web language servers could not be installed: " + ex.Message);
        }
        finally
        {
            _webLanguagesInstall = null;
        }
    }
}
