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
    ];
}
