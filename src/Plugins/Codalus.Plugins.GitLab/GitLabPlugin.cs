using Codalus.Plugins.Proposed;

namespace Codalus.Plugins.GitLab;

/// <summary>The built-in GitLab plugin (ADR 0031): registers the GitLab hosting provider when the shell needs it.</summary>
public sealed class GitLabPlugin : ICodalusPlugin
{
    public Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        var provider = new GitLabProvider(context);
        context.Subscriptions.Add(context.Proposed().Hosting.RegisterProvider(provider));
        context.Subscriptions.Add(provider);
        return Task.CompletedTask;
    }
}
