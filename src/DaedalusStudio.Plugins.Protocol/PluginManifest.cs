using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaedalusStudio.Plugins.Protocol;

#pragma warning disable CA1819 // Properties should not return arrays: manifest DTOs.

/// <summary>The contents of <c>daedalus-plugin.json</c> (ADR 0003).</summary>
public sealed record PluginManifest
{
    public const string FileName = "daedalus-plugin.json";

    public required string Id { get; init; }

    public required string Version { get; init; }

    public string? DisplayName { get; init; }

    public string? Publisher { get; init; }

    public string? Description { get; init; }

    public required ManifestEngines Engines { get; init; }

    /// <summary>The plugin's code. Optional since API 0.2.4: a declarative plugin (languages, grammars, language servers) has none.</summary>
    public ManifestEntry? Entry { get; init; }

    /// <summary><c>shared</c> (default) or <c>dedicated</c>.</summary>
    public string? Isolation { get; init; }

    public string[]? ActivationEvents { get; init; }

    public ManifestContributions? Contributes { get; init; }

    public bool EnableProposedApi { get; init; }
}

public sealed record ManifestEngines(string Daedalus);

/// <summary><see cref="Runtime"/> must be <c>dotnet</c> in v0.</summary>
public sealed record ManifestEntry(string Runtime, string Assembly)
{
    /// <summary>Optional full name of the <c>IDaedalusPlugin</c> type; otherwise the single implementing type is used.</summary>
    public string? Type { get; init; }
}

public sealed record ManifestContributions
{
    public ManifestCommand[]? Commands { get; init; }

    /// <summary>Location → items; locations are <c>commandPalette</c>, <c>menuBar/&lt;menu&gt;</c> and <c>toolWindow/title</c>.</summary>
    public Dictionary<string, ManifestMenuItem[]>? Menus { get; init; }

    public JsonElement[]? Keybindings { get; init; }

    public ManifestToolWindow[]? ToolWindows { get; init; }

    public ManifestSettings? Settings { get; init; }

    /// <summary>Languages: ids, file patterns and a <c>language-configuration.json</c> (API 0.2.4, ADR 0016).</summary>
    public ManifestLanguage[]? Languages { get; init; }

    /// <summary>TextMate grammars (JSON or plist files in the plugin's folder).</summary>
    public ManifestGrammar[]? Grammars { get; init; }

    /// <summary>LSP servers the shell starts and supervises for the plugin's languages (no plugin code is loaded).</summary>
    public ManifestLanguageServer[]? LanguageServers { get; init; }

    /// <summary>Snippet files in VS Code's format, for a language (API 0.2.5, M12b).</summary>
    public ManifestSnippets[]? Snippets { get; init; }
}

/// <summary>A snippet file, as in VS Code's <c>contributes.snippets</c>: <c>{ language, path }</c>, the path relative to the plugin's folder.</summary>
public sealed record ManifestSnippets(string Language, string Path);

/// <summary>A language, as in VS Code's <c>contributes.languages</c>. Paths are relative to the plugin's folder.</summary>
public sealed record ManifestLanguage(string Id)
{
    public string[]? Aliases { get; init; }

    public string[]? Extensions { get; init; }

    public string[]? Filenames { get; init; }

    public string[]? FilenamePatterns { get; init; }

    public string? FirstLine { get; init; }

    public string? Configuration { get; init; }
}

/// <summary>A TextMate grammar, as in VS Code's <c>contributes.grammars</c>.</summary>
public sealed record ManifestGrammar(string ScopeName, string Path)
{
    public string? Language { get; init; }

    public Dictionary<string, string>? EmbeddedLanguages { get; init; }
}

/// <summary>
/// An LSP server over stdio: <see cref="Command"/> is a file in the plugin's folder (a <c>.dll</c> runs with
/// <c>dotnet</c>) or a program on <c>PATH</c>; <c>${pluginDir}</c> in it and in <see cref="Args"/> is the plugin's folder.
/// </summary>
public sealed record ManifestLanguageServer(string Id, string[] Languages, string Command)
{
    public string? Name { get; init; }

    public string[]? Args { get; init; }

    /// <summary>Only <c>stdio</c> in this version.</summary>
    public string? Transport { get; init; }

    public JsonElement? InitializationOptions { get; init; }

    /// <summary>The settings section answered for <c>workspace/configuration</c> (e.g. <c>ini</c> for <c>ini.*</c> settings).</summary>
    public string? SettingsSection { get; init; }
}

public sealed record ManifestCommand(string Id, string Title)
{
    public string? Category { get; init; }

    public string? Enablement { get; init; }
}

public sealed record ManifestMenuItem(string Command)
{
    public string? Group { get; init; }

    public int Order { get; init; }

    public string? When { get; init; }
}

/// <summary><see cref="Location"/>: left, right or bottom. <see cref="Kind"/>: tree or markdown.</summary>
public sealed record ManifestToolWindow(string Id, string Title)
{
    public string? Location { get; init; }

    public string? Kind { get; init; }

    public string? Icon { get; init; }
}

public sealed record ManifestSettings
{
    public string? Title { get; init; }

    public Dictionary<string, ManifestSetting>? Properties { get; init; }
}

public sealed record ManifestSetting(string Type)
{
    public JsonElement? Default { get; init; }

    public string? Description { get; init; }

    public string[]? Enum { get; init; }

    public string[]? EnumDescriptions { get; init; }

    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    /// <summary><c>user</c> or <c>workspace</c> (default).</summary>
    public string? Scope { get; init; }

    public int Order { get; init; }
}

#pragma warning restore CA1819

/// <summary>Source-generated serialization for the protocol and the manifest (AOT-safe for the shell).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(HostInitializeParams))]
[JsonSerializable(typeof(HostInitializeResult))]
[JsonSerializable(typeof(LoadPluginsParams))]
[JsonSerializable(typeof(LoadPluginsResult))]
[JsonSerializable(typeof(ActivatePluginParams))]
[JsonSerializable(typeof(ActivatePluginResult))]
[JsonSerializable(typeof(PluginIdParams))]
[JsonSerializable(typeof(EmptyParams))]
[JsonSerializable(typeof(EmptyResult))]
[JsonSerializable(typeof(CommandRegistrationParams))]
[JsonSerializable(typeof(ExecuteCommandParams))]
[JsonSerializable(typeof(ExecuteCommandResult))]
[JsonSerializable(typeof(ShowNotificationParams))]
[JsonSerializable(typeof(ShowNotificationResult))]
[JsonSerializable(typeof(ShowQuickPickParams))]
[JsonSerializable(typeof(ShowQuickPickResult))]
[JsonSerializable(typeof(ShowInputBoxParams))]
[JsonSerializable(typeof(ShowInputBoxResult))]
[JsonSerializable(typeof(OutputParams))]
[JsonSerializable(typeof(StatusBarUpdateParams))]
[JsonSerializable(typeof(StatusBarRemoveParams))]
[JsonSerializable(typeof(TreeViewRefreshParams))]
[JsonSerializable(typeof(GetTreeChildrenParams))]
[JsonSerializable(typeof(GetTreeChildrenResult))]
[JsonSerializable(typeof(MarkdownViewSetParams))]
[JsonSerializable(typeof(LogParams))]
[JsonSerializable(typeof(SettingsDidChangeParams))]
[JsonSerializable(typeof(WorkspaceSolutionDto))]
[JsonSerializable(typeof(GetSolutionResult))]
[JsonSerializable(typeof(ProjectOutputParams))]
[JsonSerializable(typeof(ProjectOutputDto))]
[JsonSerializable(typeof(DocumentUrisParams))]
[JsonSerializable(typeof(WorkspacePackagesParams))]
[JsonSerializable(typeof(WorkspacePackagesResult))]
[JsonSerializable(typeof(WorkspacePropertiesParams))]
[JsonSerializable(typeof(WorkspacePropertiesResult))]
[JsonSerializable(typeof(TestCandidatesResultDto))]
[JsonSerializable(typeof(TypeOutlineParamsDto))]
[JsonSerializable(typeof(TypeOutlineResult))]
[JsonSerializable(typeof(BuildStartParams))]
[JsonSerializable(typeof(BuildResultDto))]
[JsonSerializable(typeof(BuildFinishedParams))]
[JsonSerializable(typeof(DebugLaunchParams))]
[JsonSerializable(typeof(DebugAttachParams))]
[JsonSerializable(typeof(DebugSessionDto))]
[JsonSerializable(typeof(DebugSessionParams))]
[JsonSerializable(typeof(DebugEndedParams))]
[JsonSerializable(typeof(TerminalCreateParams))]
[JsonSerializable(typeof(TerminalDto))]
[JsonSerializable(typeof(TerminalIdParams))]
[JsonSerializable(typeof(TerminalTextParams))]
[JsonSerializable(typeof(TerminalOutputParams))]
[JsonSerializable(typeof(TerminalExitedParams))]
[JsonSerializable(typeof(TestControllerParams))]
[JsonSerializable(typeof(TestControllerIdParams))]
[JsonSerializable(typeof(TestItemsUpdateParams))]
[JsonSerializable(typeof(TestProfilesParams))]
[JsonSerializable(typeof(TestRunParams))]
[JsonSerializable(typeof(TestRunStartedParams))]
[JsonSerializable(typeof(TestRunResultDto))]
[JsonSerializable(typeof(TestRunUpdateParams))]
[JsonSerializable(typeof(TestRunCoverageParams))]
[JsonSerializable(typeof(TestRunIdParams))]
public sealed partial class PluginJsonContext : JsonSerializerContext;
