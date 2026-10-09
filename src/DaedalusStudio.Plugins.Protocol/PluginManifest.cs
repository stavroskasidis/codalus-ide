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

    public required ManifestEntry Entry { get; init; }

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
