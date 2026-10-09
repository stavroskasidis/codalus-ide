using System.Text.Json;

namespace DaedalusStudio.Plugins.Protocol;

#pragma warning disable CA1819 // Properties should not return arrays: wire DTOs.

// ------------------------------------------------------------------ lifecycle

public sealed record HostInitializeParams(string ProtocolVersion, string ShellVersion, string HostGroup, Dictionary<string, JsonElement> Settings);

public sealed record HostInitializeResult(string ProtocolVersion, string HostVersion);

public sealed record PluginLoadEntry(string Id, string Version, string InstallPath, string Assembly, string? EntryType);

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
