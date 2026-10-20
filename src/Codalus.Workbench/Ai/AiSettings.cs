using Codalus.Workbench.Commands;
using Codalus.Workbench.Settings;

namespace Codalus.Workbench.Ai;

/// <summary>AI assistance's settings (ADR 0062), registered by the shell (and by tests that use <see cref="AiService"/>).</summary>
public static class AiSettings
{
    public static IReadOnlyList<SettingDescriptor> Descriptors(ContributionSource source) =>
    [
        SettingDescriptor.Create(AiService.EnabledSetting, "false", SettingType.Boolean, source) with
        {
            Title = "Enable AI Assistance",
            Category = "AI",
            Description = "Turn on AI assistance: the AI Chat window and the AI commands. Off by default; nothing is sent anywhere until you also choose a provider. A workspace's settings can turn it off there, never on.",
            Order = 1,
        },
        SettingDescriptor.Create(AiService.ProviderSetting, "\"\"", SettingType.String, source) with
        {
            Title = "Provider",
            Category = "AI",
            Description = "The language model provider requests go to (a provider plugin's id: openai, ollama, anthropic, or a third-party plugin's). Empty: none, and nothing is sent.",
            Scope = SettingScope.User,
            Order = 2,
        },
        SettingDescriptor.Create(AiService.ModelSetting, "\"\"", SettingType.String, source) with
        {
            Title = "Model",
            Category = "AI",
            Description = "The provider's model to use. Empty: the provider's first model.",
            Scope = SettingScope.User,
            Order = 3,
        },
        SettingDescriptor.Create(AiService.ExcludeSetting, "[]", SettingType.StringArray, source) with
        {
            Title = "Exclude",
            Category = "AI",
            Description = "Files never sent to a language model, as .gitignore patterns relative to the workspace (for example secrets/ or **/*.local.json). Files that .gitignore or a .codalusignore excludes, and files that hold secrets by name (.env, *.pfx, *.key, secrets.json…), are never sent either.",
            Order = 4,
        },
        SettingDescriptor.Create(AiService.MaxContextSetting, "120000", SettingType.Integer, source) with
        {
            Title = "Context Size Limit",
            Category = "AI",
            Description = "The most characters of context (selection, files, errors, build output) sent with a question. What goes over is cut, and the chat says so.",
            Minimum = 1000,
            Scope = SettingScope.User,
            Order = 5,
        },
        SettingDescriptor.Create(AiCompletions.EnabledSetting, "false", SettingType.Boolean, source) with
        {
            Title = "Inline Suggestions",
            Category = "AI",
            Description = "Suggest code as you type, shown in gray at the caret: Tab accepts, Ctrl+Right accepts a word, Esc dismisses. It sends the text around the caret to the provider as you type, so it has its own switch, off by default.",
            Order = 10,
        },
        SettingDescriptor.Create(AiCompletions.ModelSetting, "\"\"", SettingType.String, source) with
        {
            Title = "Inline Suggestions Model",
            Category = "AI",
            Description = "The provider's model for inline suggestions, when it should be a faster one than the chat's. Empty: the chat's model.",
            Scope = SettingScope.User,
            Order = 11,
        },
        SettingDescriptor.Create(AiCompletions.DelaySetting, "400", SettingType.Integer, source) with
        {
            Title = "Inline Suggestions Delay",
            Category = "AI",
            Description = "How long typing pauses, in milliseconds, before a suggestion is asked for.",
            Minimum = 100,
            Maximum = 5000,
            Scope = SettingScope.User,
            Order = 12,
        },
        SettingDescriptor.Create(AiNextEdit.EnabledSetting, "false", SettingType.Boolean, source) with
        {
            Title = "Next Edit Suggestions",
            Category = "AI",
            Description = "After you edit, suggest the next edit nearby (the same rename further down, a matching change): the lines show highlighted with the new text beside them, Tab applies it, Esc dismisses it. It sends your recent edits and the lines around the caret, so it has its own switch, off by default.",
            Order = 13,
        },
        SettingDescriptor.Create("ai.agent.maxSteps", "25", SettingType.Integer, source) with
        {
            Title = "Agent Step Limit",
            Category = "AI",
            Description = "The most tool calls agent mode makes for one question before it stops and summarizes.",
            Minimum = 1,
            Maximum = 200,
            Scope = SettingScope.User,
            Order = 20,
        },
        SettingDescriptor.Create("ai.agent.commandTimeoutSeconds", "120", SettingType.Integer, source) with
        {
            Title = "Agent Command Timeout",
            Category = "AI",
            Description = "How long, in seconds, a command the agent runs (after you allow it) may take before it is stopped.",
            Minimum = 5,
            Maximum = 3600,
            Scope = SettingScope.User,
            Order = 21,
        },
        SettingDescriptor.Create(Mcp.McpServers.ServersSetting, "{}", SettingType.Object, source) with
        {
            Title = "MCP Servers",
            Category = "AI",
            Description = "MCP servers whose tools agent mode may use, each call after you allow it: { \"name\": { \"command\": \"…\", \"args\": [\"…\"], \"env\": { } } }. Read from your user settings only.",
            Scope = SettingScope.User,
            Order = 22,
        },
        SettingDescriptor.Create(Acp.AcpAgents.AgentsSetting, "{}", SettingType.Object, source) with
        {
            Title = "External Agents",
            Category = "AI",
            Description = "External coding agents AI Chat can run over the Agent Client Protocol (ACP), each your own CLI: { \"name\": { \"command\": \"…\", \"args\": [\"…\"], \"env\": { } } }. They use their own provider and data policy; Codalus checks what they ask the IDE for. They run only while AI assistance is on, in trusted folders. Read from your user settings only.",
            Scope = SettingScope.User,
            Order = 24,
        },
        SettingDescriptor.Create("ai.mcpServer.enabled", "false", SettingType.Boolean, source) with
        {
            Title = "MCP Server",
            Category = "AI",
            Description = "Let AI agents that run outside the IDE reach it through `codalus mcp` (an MCP server over stdio): the solution, the open files, the Error List and the build output; a build asks you first. For your user only; off by default.",
            Scope = SettingScope.User,
            Order = 23,
        },
    ];
}
