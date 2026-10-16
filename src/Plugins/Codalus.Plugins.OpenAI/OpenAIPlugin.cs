using Codalus.Plugins.Proposed;

namespace Codalus.Plugins.OpenAI;

/// <summary>The built-in OpenAI-compatible plugin (ADR 0062): registers the provider the user picked.</summary>
public sealed class OpenAIPlugin : ICodalusPlugin
{
    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var models = context.Proposed().LanguageModels;
        foreach (var provider in new OpenAIProvider[]
        {
            new(OpenAIProvider.OpenAIId, context, "OpenAI-compatible endpoint", "ai.openai.endpoint", "https://api.openai.com/v1", LanguageModels.KeyUse.Optional),
            new(OpenAIProvider.OllamaId, context, "Ollama", "ai.ollama.endpoint", "http://localhost:11434/v1", LanguageModels.KeyUse.None),
        })
        {
            context.Subscriptions.Add(models.RegisterProvider(provider));
            context.Subscriptions.Add(provider);
        }

        return Task.CompletedTask;
    }
}
