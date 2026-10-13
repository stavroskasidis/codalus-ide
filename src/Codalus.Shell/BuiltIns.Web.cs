using System.Text.Json;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Settings;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Shell;

/// <summary>ASP.NET Core (M15a): the Endpoints Explorer, .http files, the environment, the development certificate and user secrets.</summary>
internal static partial class BuiltIns
{
    public const string WebKeybindings = """
        [
          { "key": "ctrl+alt+r", "command": "http.sendRequest", "when": "editorTextFocus && editorLanguageId == http" },
          { "win": "ctrl+alt+backspace", "mac": "ctrl+alt+backspace", "linux": "ctrl+shift+alt+r", "command": "npm.taskRunner", "when": "keyboardScheme != vscode" }
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
        SettingDescriptor.Create(Workbench.Node.Eslint.Setting, "true", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "ESLint",
            Category = "Web",
            Description = "Lint JavaScript and TypeScript files with the project's own ESLint, where an ESLint configuration covers them (with the web language servers).",
        },
        SettingDescriptor.Create(Workbench.Node.Prettier.Setting, "\"auto\"", SettingType.String, ContributionSource.Shell) with
        {
            Title = "Prettier",
            Category = "Web",
            Description = "Format Document and format on save use the project's own Prettier when it is installed (node_modules) and configured (.prettierrc, prettier.config.js or package.json).",
            Enum = ["auto", "off"],
            EnumDescriptions = ["When the project uses Prettier", "Never; the language servers format"],
        },
        SettingDescriptor.Create(Workbench.Node.Npm.RuntimeSetting, "\"auto\"", SettingType.String, ContributionSource.Shell) with
        {
            Title = "npm",
            Category = "Web",
            Description = "The npm that runs scripts and restores packages: yours (on PATH), else the built-in Node.js; or always the built-in Node.js.",
            Enum = ["auto", "builtIn"],
            EnumDescriptions = ["The npm on PATH, else the built-in Node.js", "The built-in Node.js"],
        },
        SettingDescriptor.Create(Workbench.Node.Npm.RestoreOnOpenSetting, "\"ask\"", SettingType.String, ContributionSource.Shell) with
        {
            Title = "Restore npm Packages on Open",
            Category = "Web",
            Description = "When a trusted solution opens with a package.json whose packages aren't installed: ask, restore (npm install), or do nothing.",
            Enum = ["ask", "always", "never"],
            EnumDescriptions = ["Ask", "Restore without asking", "Do nothing"],
        },
    ];

    public static IEnumerable<ToolWindowDescriptor> WebToolWindows()
    {
        var shell = ContributionSource.Shell;
        yield return new ToolWindowDescriptor(EndpointsExplorerViewModel.ToolWindowId, "Endpoints Explorer", ToolWindowLocation.Right, ToolWindowKind.BuiltIn, shell) { Icon = "globe" };
        yield return new ToolWindowDescriptor(HttpResponseViewModel.ToolWindowId, "HTTP Response", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "sync" };
        yield return new ToolWindowDescriptor(TaskRunnerViewModel.ToolWindowId, "Task Runner Explorer", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, shell) { Icon = "task-list" };
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
        Add("web.addScaffoldedItem", "Add New Scaffolded Item…", "ASP.NET Core", project => { shell.OpenAddScaffoldedItem(project); return Task.CompletedTask; }, "solutionOpen && solutionTrusted");
        Add("web.serviceReferences", "Service References (OpenAPI, gRPC)…", "ASP.NET Core", project => { shell.OpenServiceReferences(project); return Task.CompletedTask; }, "solutionOpen && solutionTrusted");
        Add("web.installLanguageServers", "Install the Web Language Servers (Node.js)", "Web", _ => shell.InstallWebLanguagesAsync());
        Add("npm.taskRunner", "Task Runner Explorer", "npm", _ => shell.ShowTaskRunnerAsync());
        Add("npm.runScript", "Run npm Script…", "npm", _ => shell.PickNpmScriptAsync(), "solutionOpen");
        Add("npm.restore", "Restore npm Packages", "npm", async path => await shell.RestoreNpmAsync(path ?? shell.SelectedPackageJson()).ConfigureAwait(false), "solutionOpen && solutionTrusted");
        Add("debug.webPageFile", "Debug in Browser", "Debug", path => shell.DebugWebPageAsync(path ?? shell.Explorer.Selected?.Node.Path), "!debugActive");
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
            Item(SolutionExplorerView.AddMenuLocation, "web.addScaffoldedItem", "1-new", 0, "(explorerItemKind == project || explorerItemKind == folder) && !explorerFolderView", "New Scaffolded Item…"),
            Item(SolutionExplorerView.AddMenuLocation, "web.serviceReferences", "2-references", 2, "explorerItemKind == project", "Service References…"),
            Item(SolutionExplorerView.ContextMenuLocation, "web.manageUserSecrets", "2-references", 4, "explorerItemKind == project"),
            Item(OtherWindowsMenu, "npm.taskRunner", "windows", AlphabeticalOrder("Task Runner Explorer")),
            Item(SolutionExplorerView.ContextMenuLocation, "npm.restore", "3-web", 2, "explorerItemName == 'package.json'", "Restore Packages"),
            Item(SolutionExplorerView.ContextMenuLocation, "debug.webPageFile", "3-web", 3, "explorerItemName =~ /(?i)\\.html?$/"),
            Item(MenuLocations.EditorContext, "http.sendRequest", "0-http", 0, "editorLanguageId == http"),
            Item(MenuLocations.EditorContext, "http.selectEnvironment", "0-http", 1, "editorLanguageId == http"),
        ]);
    }
}
