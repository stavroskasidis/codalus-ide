using System.Text.Json;

namespace Codalus.Plugins.Protocol;

#pragma warning disable CA1819 // Properties should not return arrays: wire DTOs.

// ------------------------------------------------------------------ lifecycle

public sealed record HostInitializeParams(string ProtocolVersion, string ShellVersion, string HostGroup, Dictionary<string, JsonElement> Settings);

public sealed record HostInitializeResult(string ProtocolVersion, string HostVersion);

/// <summary><see cref="EnableProposedApi"/>: the plugin may use the proposed APIs (since protocol 0.2).</summary>
public sealed record PluginLoadEntry(string Id, string Version, string InstallPath, string Assembly, string? EntryType, bool EnableProposedApi = false);

public sealed record LoadPluginsParams(PluginLoadEntry[] Plugins);

public sealed record PluginLoadResult(string Id, bool Loaded, string? Error);

public sealed record LoadPluginsResult(PluginLoadResult[] Results);

public sealed record ActivatePluginParams(string Id, string Reason);

public sealed record ActivatePluginResult(bool Activated, string? Error);

public sealed record PluginIdParams(string Id);

public sealed record EmptyParams;

public sealed record EmptyResult;

// ------------------------------------------------------------------ commands

public sealed record CommandRegistrationParams(string PluginId, string Command);

public sealed record ExecuteCommandParams(string Command, JsonElement[] Args);

public sealed record ExecuteCommandResult(JsonElement? Result);

// ------------------------------------------------------------------ window

public sealed record ShowNotificationParams(string PluginId, string Severity, string Message, string[] Actions);

public sealed record ShowNotificationResult(string? Action);

public sealed record QuickPickItemDto(string Label, string? Description, string? Detail);

public sealed record ShowQuickPickParams(string PluginId, QuickPickItemDto[] Items, string? Placeholder);

/// <summary>The index of the chosen item, or null when cancelled.</summary>
public sealed record ShowQuickPickResult(int? Index);

public sealed record ShowInputBoxParams(string PluginId, string Title, string? Prompt, string? Value);

/// <summary>The entered text, or null when cancelled.</summary>
public sealed record ShowInputBoxResult(string? Value);

/// <summary>A plugin's task (API 1.0): <see cref="Id"/> is the host's, unique within it.</summary>
public sealed record ProgressStartParams(string PluginId, string Id, string Title, bool Cancellable);

public sealed record ProgressReportParams(string PluginId, string Id, string? Message, double? Percent);

public sealed record ProgressIdParams(string PluginId, string Id);

/// <summary>A declarative view's element on the wire (the API's <c>ViewNode</c>).</summary>
public sealed record ViewNodeDto(string Kind)
{
    public string? Id { get; init; }
    public string? Text { get; init; }
    public string? Style { get; init; }
    public string? Value { get; init; }
    public string? Placeholder { get; init; }
    public bool? Checked { get; init; }
    public bool Disabled { get; init; }
    public string[]? Options { get; init; }
    public string[]? Columns { get; init; }
    public string[][]? Rows { get; init; }
    public ViewNodeDto[]? Children { get; init; }
    public string? Orientation { get; init; }
}

public sealed record ViewSetParams(string PluginId, string ViewId, ViewNodeDto Root);

public sealed record ViewEventParams(string ViewId, string ElementId, string Kind, string? Value, int? Row);

public sealed record CustomEditorOpenParams(string ViewType, string ViewId, string Path, string Text);

public sealed record ViewIdParams(string ViewId);

public sealed record CustomEditorTextParams(string PluginId, string ViewId, string Text);

public sealed record FsSchemeParams(string PluginId, string Scheme);

public sealed record AuthProviderParams(string PluginId, string ProviderId, string Label);

public sealed record ScmProviderParams(string PluginId, string ProviderId, string Label, string Root);

public sealed record ScmResourceDto(string Path, string Status, string? Tooltip);

public sealed record ScmGroupDto(string Id, string Label, ScmResourceDto[] Resources);

public sealed record ScmUpdateParams(string ProviderId, ScmGroupDto[] Groups, string? InputPlaceholder, string? AcceptLabel, string? Status);

public sealed record ScmRequestParams(string ProviderId)
{
    public string? Message { get; init; }
    public string? Path { get; init; }
}

public sealed record ScmOriginalResult(string? Text);

public sealed record AuthGetSessionParams(string PluginId, string ProviderId, string[] Scopes, bool CreateIfNone);

/// <summary>A provider operation: <see cref="Scopes"/> for listing and creating, <see cref="SessionId"/> for removing.</summary>
public sealed record AuthRequestParams(string ProviderId)
{
    public string[]? Scopes { get; init; }
    public string? SessionId { get; init; }
}

public sealed record AuthSessionDto(string Id, string AccessToken, string Account, string[] Scopes);

public sealed record AuthSessionResult(AuthSessionDto? Session);

public sealed record AuthSessionsResult(AuthSessionDto[] Sessions);

public sealed record FsChangedParams(string Scheme, string[] Paths, string Kind);

/// <summary>A provider operation: <see cref="Path"/> in the scheme's terms; the others as the operation needs them.</summary>
public sealed record FsRequestParams(string Scheme, string Path)
{
    public string? Target { get; init; }
    public byte[]? Content { get; init; }
    public bool Recursive { get; init; }
    public bool Overwrite { get; init; }
}

public sealed record FsStatResult(bool Exists, bool IsDirectory, long Size, DateTimeOffset Modified);

public sealed record FsEntryDto(string Name, bool IsDirectory, long Size, DateTimeOffset Modified);

public sealed record FsReadDirectoryResult(FsEntryDto[] Entries);

public sealed record FsReadFileResult(byte[] Content);

public sealed record OutputParams(string PluginId, string Channel, string? Text);

public sealed record StatusBarUpdateParams(string PluginId, string ItemId, string Text, string? Tooltip, string? Command, string Alignment, int Priority, bool Visible);

public sealed record StatusBarRemoveParams(string PluginId, string ItemId);

public sealed record TreeViewRefreshParams(string ViewId, string? Handle);

public sealed record GetTreeChildrenParams(string ViewId, string? Handle);

public sealed record TreeItemCommandDto(string Id, JsonElement[]? Args);

/// <summary><see cref="Collapsible"/> is <c>none</c>, <c>collapsed</c> or <c>expanded</c>.</summary>
public sealed record TreeItemDto(string Handle, string Label, string? Description, string? Tooltip, string? Icon, string Collapsible, TreeItemCommandDto? Command);

public sealed record GetTreeChildrenResult(TreeItemDto[] Items);

public sealed record MarkdownViewSetParams(string ViewId, string Markdown);

/// <summary><see cref="Level"/> is <c>trace</c>, <c>debug</c>, <c>info</c>, <c>warning</c> or <c>error</c>.</summary>
public sealed record LogParams(string? PluginId, string Level, string Message);

// ------------------------------------------------------------------ settings

/// <summary>A null value means the setting was reset to its default (the host receives the default in the next snapshot).</summary>
public sealed record SettingsDidChangeParams(Dictionary<string, JsonElement> Changes);

#pragma warning restore CA1819
