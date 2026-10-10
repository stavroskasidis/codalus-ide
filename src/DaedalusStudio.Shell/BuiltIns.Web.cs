using System.Text.Json;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Shell.Views;
using DaedalusStudio.Workbench.Commands;
using DaedalusStudio.Workbench.Context;
using DaedalusStudio.Workbench.Menus;
using DaedalusStudio.Workbench.Settings;
using DaedalusStudio.Workbench.ToolWindows;

namespace DaedalusStudio.Shell;

/// <summary>ASP.NET Core (M15a): the Endpoints Explorer, .http files, the environment, the development certificate and user secrets.</summary>
internal static partial class BuiltIns
{
    public const string WebKeybindings = """
        [
          { "key": "ctrl+alt+r", "command": "http.sendRequest", "when": "editorTextFocus && editorLanguageId == http" }
        ]
        """;

    public static IReadOnlyList<SettingDescriptor> WebSettings() =>
    [
        SettingDescriptor.Create(ShellHost.WebLanguageServersSetting, "\"ask\"", SettingType.String, ContributionSource.Shell) with
        {
            Title = "Web Language Servers",
            Category = "Web",
            Description = "HTML, CSS, JavaScript and TypeScript editing uses language servers that run on Node.js, downloaded once into the cache folder: ask before downloading, download without asking, or don't use them.",
            Enum = ["ask", "install", "off"],
            EnumDescriptions = ["Ask the first time a web file opens", "Download without asking", "Don't use them"],
        },
    ];

    public static IEnumerable<ToolWindowDescriptor> WebToolWindows()
    {
        var shell = ContributionSource.Shell;
        yield return new ToolWindowDescriptor(EndpointsExplorerViewModel.ToolWindowId, "Endpoints Explorer", ToolWindowLocation.Right, ToolWindowKind.BuiltIn, shell) { Icon = "list" };
        yield return new ToolWindowDescriptor(HttpResponseViewModel.ToolWindowId, "HTTP Response", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "list" };
    }

    public static void RegisterWebCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, string category, Func<string?, Task> handler, string? enablement = null) =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell) { Category = category, Enablement = WhenClause.Parse(enablement) },
                async (args, _) =>
                {
                    // An optional first argument: a project path, or an environment's name.
                    var argument = args.Count > 0 && args[0].ValueKind == JsonValueKind.String ? args[0].GetString() : null;
                    await Infrastructure.Ui.RunAsync(() => handler(argument)).ConfigureAwait(false);
                    return null;
                });

        Add("web.endpoints", "Endpoints Explorer", "ASP.NET Core", _ => shell.ShowEndpointsAsync(), "solutionOpen");
        Add("web.selectEnvironment", "Select the ASP.NET Core Environment…", "ASP.NET Core", shell.SelectAspNetEnvironmentAsync);
        Add("web.checkDevCertificate", "Check the HTTPS Development Certificate", "ASP.NET Core", _ => shell.CheckDevCertificateAsync());
        Add("web.trustDevCertificate", "Trust the HTTPS Development Certificate", "ASP.NET Core", _ => shell.TrustDevCertificateAsync());
        Add("web.manageUserSecrets", "Manage User Secrets", "ASP.NET Core", shell.ManageUserSecretsAsync, "solutionOpen && solutionTrusted");
        Add("web.installLanguageServers", "Install the Web Language Servers (Node.js)", "Web", _ => shell.InstallWebLanguagesAsync());
        Add("http.sendRequest", "Send Request", "HTTP", _ => shell.SendHttpRequestAtCaretAsync(), "editorLanguageId == http");
        Add("http.selectEnvironment", "Select the Environment for .http Requests…", "HTTP", _ => shell.SelectHttpEnvironmentAsync(), "editorLanguageId == http");
        Add("http.showResponse", "HTTP Response", "HTTP", _ =>
        {
            shell.Layout.ShowToolWindow(HttpResponseViewModel.ToolWindowId, focus: true);
            return Task.CompletedTask;
        });
    }

    public static void RegisterWebMenus(MenuRegistry menus)
    {
        var shell = ContributionSource.Shell;
        MenuItemRule Item(string location, string command, string group, int order = 0, string? when = null, string? title = null) =>
            new(location, command, shell) { Group = group, Order = order, When = WhenClause.Parse(when), Title = title };

        menus.Add(
        [
            Item(OtherWindowsMenu, "web.endpoints", "windows", AlphabeticalOrder("Endpoints Explorer")),
            Item(MenuLocations.MenuBar("project"), "web.manageUserSecrets", "web", 1),
            Item(MenuLocations.MenuBar("debug"), "web.selectEnvironment", "6-web", 1),
            Item(MenuLocations.MenuBar("debug"), "web.checkDevCertificate", "6-web", 2),
            Item(MenuLocations.MenuBar("debug"), "web.trustDevCertificate", "6-web", 3),
            Item(SolutionExplorerView.ContextMenuLocation, "web.manageUserSecrets", "3-web", 1, "explorerItemKind == project"),
            Item(MenuLocations.EditorContext, "http.sendRequest", "0-http", 0, "editorLanguageId == http"),
            Item(MenuLocations.EditorContext, "http.selectEnvironment", "0-http", 1, "editorLanguageId == http"),
        ]);
    }
}
