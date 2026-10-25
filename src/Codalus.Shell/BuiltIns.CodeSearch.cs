using Codalus.Workbench.Commands;
using Codalus.Workbench.Settings;
using Codalus.Workbench.Search;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Shell;

/// <summary>Code Search's settings, tool window, commands and key bindings (M47, ADR 0078).</summary>
internal static partial class BuiltIns
{
    /// <summary>
    /// The classic IDE's Go To keys (Ctrl+1 then the kind's letter) for the chips; Ctrl+T, Ctrl+, (Go To All) and Ctrl+Q
    /// (the search box) are in the default bindings.
    /// </summary>
    public const string CodeSearchKeybindings = """
        [
          { "key": "primary+1 primary+f", "command": "workbench.action.gotoFile", "when": "keyboardScheme != vscode" },
          { "key": "primary+1 primary+t", "command": "workbench.action.gotoType", "when": "keyboardScheme != vscode" },
          { "key": "primary+1 primary+m", "command": "workbench.action.gotoMember", "when": "keyboardScheme != vscode" },
          { "key": "primary+1 primary+x", "command": "workbench.action.gotoText", "when": "keyboardScheme != vscode" }
        ]
        """;

    public static IEnumerable<SettingDescriptor> CodeSearchSettings()
    {
        var shell = ContributionSource.Shell;
        yield return SettingDescriptor.Create(ShellHost.CodeSearchScopeSetting, "\"entireSolution\"", SettingType.String, shell) with
        {
            Title = "Scope",
            Category = "Code Search",
            Description = "Where Code Search looks: the entire solution, the current document's project, or the current document. The scope picker in the window changes it.",
            Enum = ["entireSolution", "currentProject", "currentDocument"],
            EnumDescriptions = ["Entire solution", "Current project", "Current document"],
            TranslateEnumDescriptions = true,
            Scope = SettingScope.User,
            Order = 1,
        };
        yield return SettingDescriptor.Create(ShellHost.CodeSearchExternalSetting, "false", SettingType.Boolean, shell) with
        {
            Title = "Include External Items",
            Category = "Code Search",
            Description = "Also find the types of the referenced assemblies (NuGet packages and the framework); they open decompiled, or from their embedded or Source Link source.",
            Scope = SettingScope.User,
            Order = 2,
        };
        yield return SettingDescriptor.Create(ShellHost.CodeSearchGroupingSetting, "\"interleaved\"", SettingType.String, shell) with
        {
            Title = "Result Grouping",
            Category = "Code Search",
            Description = "Show results in one list ranked best first, or grouped by kind: files, types, members, then text.",
            Enum = ["interleaved", "grouped"],
            EnumDescriptions = ["Interleaved by rank", "Grouped by kind"],
            TranslateEnumDescriptions = true,
            Scope = SettingScope.User,
            Order = 3,
        };
        yield return SettingDescriptor.Create(ShellHost.CodeSearchTextResultsSetting, "100", SettingType.Integer, shell) with
        {
            Title = "Text Results",
            Category = "Code Search",
            Description = "The most lines a text search lists (Find in Files' engine, for queries of three characters or more, or with x:). 0 turns text results off.",
            Minimum = 0,
            Maximum = 1000,
            Scope = SettingScope.User,
            Order = 4,
        };
    }

    public static ToolWindowDescriptor CodeSearchToolWindow() =>
        new(ShellHost.CodeSearchToolWindowId, "Code Search", ToolWindowLocation.Bottom, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "search" };

    public static void RegisterCodeSearchCommands(ShellHost shell)
    {
        var commands = shell.Workbench.Commands;
        void Add(string id, string title, CommandHandler handler) =>
            commands.Register(new CommandDescriptor(id, title, ContributionSource.Shell) { Category = "Edit" }, handler);

        Add("workbench.action.gotoFile", "Go To File…", Ui(() => shell.ShowCodeSearch(CodeSearchFilter.Files)));
        Add("workbench.action.gotoType", "Go To Type…", Ui(() => shell.ShowCodeSearch(CodeSearchFilter.Types)));
        Add("workbench.action.gotoMember", "Go To Member…", Ui(() => shell.ShowCodeSearch(CodeSearchFilter.Members)));
        Add("workbench.action.gotoText", "Go To Text…", Ui(() => shell.ShowCodeSearch(CodeSearchFilter.Text)));
    }
}
