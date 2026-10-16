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
          { "key": "alt+down", "command": "fsharp.moveDown", "when": "focusedView == solutionExplorer && explorerItemOrdered" },
          { "key": "primary+e primary+e", "command": "fsi.sendSelection", "when": "editorTextFocus && editorLanguageId == fsharp" },
          { "key": "alt+'", "command": "fsi.sendLine", "when": "editorTextFocus && editorLanguageId == fsharp" }
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
        SettingDescriptor.Create("fsharp.fsiExtraParameters", "\"\"", SettingType.String, ContributionSource.Shell) with
        {
            Title = "F# Interactive Options",
            Category = "F#",
            Description = "More options for dotnet fsi, separated by spaces (e.g. --langversion:preview --optimize+). They apply when F# Interactive starts.",
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
        SettingDescriptor.Create("fsharp.analyzers", "false", SettingType.Boolean, ContributionSource.Shell) with
        {
            Title = "F# Analyzers",
            Category = "F#",
            Description = "Run F# analyzers (FSharp.Analyzers.SDK) as you type, from the F# Analyzers Path folders. Analyzers are code, so they run only in trusted folders, like the language service.",
        },
        SettingDescriptor.Create("fsharp.analyzersPath", "\"packages/analyzers;analyzers\"", SettingType.String, ContributionSource.Shell) with
        {
            Title = "F# Analyzers Path",
            Category = "F#",
            Description = "The folders F# analyzers are loaded from, separated by semicolons, relative to the solution's folder.",
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

        // F# Interactive (M36b): dotnet fsi in a terminal.
        Add("fsi.show", "F# Interactive", () =>
        {
            shell.ShowFSharpInteractive();
            return Task.CompletedTask;
        });
        Add("fsi.sendSelection", "Send to F# Interactive", () =>
        {
            shell.SendToFSharpInteractive();
            return Task.CompletedTask;
        }, "editorLanguageId == fsharp");
        Add("fsi.sendLine", "Send Line to F# Interactive", () =>
        {
            shell.SendToFSharpInteractive(lineOnly: true);
            return Task.CompletedTask;
        }, "editorLanguageId == fsharp");
        Add("fsi.sendFile", "Send File to F# Interactive", () =>
        {
            shell.SendFileToFSharpInteractive();
            return Task.CompletedTask;
        }, "editorLanguageId == fsharp");
        Add("fsi.runScript", "Run Script", () =>
        {
            shell.RunFSharpScript();
            return Task.CompletedTask;
        }, "editorExtension == .fsx || editorExtension == .fsscript");
        Add("fsi.sendReferences", "Send References to F# Interactive", () => shell.SendReferencesToFSharpInteractiveAsync(), "solutionTrusted");
        Add("fsi.reset", "Reset F# Interactive", () =>
        {
            shell.ResetFSharpInteractive();
            return Task.CompletedTask;
        });

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
            Item(Context, "fsi.sendReferences", "5-path", 5, "explorerItemKind == project"),
            Item(OtherWindowsMenu, "fsi.show", "windows", AlphabeticalOrder("F# Interactive")),
        ]);
    }
}
