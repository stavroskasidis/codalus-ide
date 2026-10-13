using Codalus.Plugins.Proposed;

namespace Codalus.Plugins.GitHub;

/// <summary>The built-in GitHub plugin (ADR 0031): registers the GitHub hosting provider when the shell needs it.</summary>
public sealed class GitHubPlugin : ICodalusPlugin
{
    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var provider = new GitHubProvider(context);
        context.Subscriptions.Add(context.Proposed().Hosting.RegisterProvider(provider));
        context.Subscriptions.Add(provider);
        return Task.CompletedTask;
    }
}
