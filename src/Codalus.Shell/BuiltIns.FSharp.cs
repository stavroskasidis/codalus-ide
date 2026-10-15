using Codalus.Shell.Views;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Context;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Settings;

namespace Codalus.Shell;

/// <summary>F# (M36, ADR 0067): the language service's install and settings, and the compile order in Solution Explorer.</summary>
internal static partial class BuiltIns
{
    public const string FSharpKeybindings = """
        [
          { "key": "alt+up", "command": "fsharp.moveUp", "when": "focusedView == solutionExplorer && explorerItemOrdered" },
          { "key": "alt+down", "command": "fsharp.moveDown", "when": "focusedView == solutionExplorer && explorerItemOrdered" }
        ]
        """;

    public static IReadOnlyList<SettingDescriptor> FSharpSettings() =>
    [
        SettingDescriptor.Create(ShellHost.FSharpLanguageServiceSetting, "\"ask\"", SettingType.String, ContributionSource.Shell) with
        {
            Title = "F# Language Service",
            Category = "F#",
            Description = "F# editing uses FsAutoComplete and Fantomas, installed once into the cache folder with dotnet tool install: ask before installing, install without asking, or don't use them.",
            Enum = ["ask", "install", "off"],
            EnumDescriptions = ["Ask the first time an F# file opens", "Install without asking", "Don't use them"],
        },
        SettingDescriptor.Create("fsharp.completeUnopenedNamespaces", "true", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Complete Unopened Namespaces",
            Category = "F#",
            Description = "Completion offers types and modules of namespaces that aren't opened yet, and adds the open declaration.",
        },
        SettingDescriptor.Create("fsharp.linter", "true", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Linter",
            Category = "F#",
            Description = "Report FSharpLint's suggestions as you type.",
        },
        SettingDescriptor.Create("fsharp.inlayHints", "true", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Inlay Hints",
            Category = "F#",
            Description = "Show inferred types and parameter names in the editor.",
        },
        SettingDescriptor.Create("fsharp.signatureCodeLens", "true", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "Signature CodeLens",
            Category = "F#",
            Description = "Show each function's inferred signature above it.",
        },
        SettingDescriptor.Create("fsharp.referencesCodeLens", "true", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "References CodeLens",
            Category = "F#",
            Description = "Show how many references each definition has above it.",
        },
    ];

    public static void RegisterFSharpCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, Func<Task> handler, string? enablement = null, bool palette = true) =>
            commands.Register(
                new CommandDescriptor(id, title, ContributionSource.Shell) { Category = "F#", Enablement = WhenClause.Parse(enablement), ShowInPalette = palette },
                async (_, _) =>
                {
                    await Infrastructure.Ui.RunAsync(handler).ConfigureAwait(false);
                    return null;
                });

        Add("fsharp.moveUp", "Move Up", () => shell.MoveInCompileOrderAsync(down: false), "explorerItemOrdered && solutionTrusted");
        Add("fsharp.moveDown", "Move Down", () => shell.MoveInCompileOrderAsync(down: true), "explorerItemOrdered && solutionTrusted");
        Add("fsharp.addAbove", "Add Above…", () =>
        {
            shell.AddInCompileOrder(below: false);
            return Task.CompletedTask;
        }, "explorerItemOrdered && solutionTrusted");
        Add("fsharp.addBelow", "Add Below…", () =>
        {
            shell.AddInCompileOrder(below: true);
            return Task.CompletedTask;
        }, "explorerItemOrdered && solutionTrusted");
        Add("fsharp.installLanguageService", "Install the F# Language Service", shell.InstallFSharpAsync);

        // FsAutoComplete's references CodeLens runs this; the caret is already on the definition.
        Add("fsharp.showReferences", "Show References", shell.FindReferencesAsync, palette: false);
    }

    public static void RegisterFSharpMenus(MenuRegistry menus)
    {
        var shell = ContributionSource.Shell;
        MenuItemRule Item(string location, string command, string group, int order, string? when = null) =>
            new(location, command, shell) { Group = group, Order = order, When = WhenClause.Parse(when) };

        const string Context = SolutionExplorerView.ContextMenuLocation;
        menus.Add(
        [
            Item(SolutionExplorerView.AddMenuLocation, "fsharp.addAbove", "2-order", 1, "explorerItemOrdered"),
            Item(SolutionExplorerView.AddMenuLocation, "fsharp.addBelow", "2-order", 2, "explorerItemOrdered"),
            Item(Context, "fsharp.moveUp", "3-order", 1, "explorerItemOrdered"),
            Item(Context, "fsharp.moveDown", "3-order", 2, "explorerItemOrdered"),
        ]);
    }
}
