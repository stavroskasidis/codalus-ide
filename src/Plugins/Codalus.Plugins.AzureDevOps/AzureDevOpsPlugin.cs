using Codalus.Plugins.Proposed;

namespace Codalus.Plugins.AzureDevOps;

/// <summary>The built-in Azure DevOps plugin (ADR 0031): registers the Azure DevOps hosting provider when the shell needs it.</summary>
public sealed class AzureDevOpsPlugin : ICodalusPlugin
{
    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var provider = new AzureDevOpsProvider(context);
        context.Subscriptions.Add(context.Hosting.RegisterProvider(provider));
        context.Subscriptions.Add(provider);
        return Task.CompletedTask;
    }
}
