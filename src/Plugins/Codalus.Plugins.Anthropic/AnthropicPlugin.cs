using Codalus.Plugins.Proposed;

namespace Codalus.Plugins.Anthropic;

/// <summary>The built-in Anthropic plugin (ADR 0062): registers the provider when the user picks it.</summary>
public sealed class AnthropicPlugin : ICodalusPlugin
{
    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var provider = new AnthropicProvider(context);
        context.Subscriptions.Add(context.Proposed().LanguageModels.RegisterProvider(provider));
        context.Subscriptions.Add(provider);
        return Task.CompletedTask;
    }
}
