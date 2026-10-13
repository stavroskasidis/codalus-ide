using System.Collections.Concurrent;
using Codalus.Plugins.Proposed;
using Codalus.Plugins.Protocol;
using Codalus.Rpc;

namespace Codalus.Plugins.Host.Proposed;

/// <summary>
/// The host's hosting providers (ADR 0031): registrations go to the shell, and the shell's <c>hosting/*</c> requests come
/// to the provider they name. A provider's exceptions become error codes the shell understands.
/// </summary>
internal sealed class HostHostingRegistry(PluginHostServer server)
{
    private readonly ConcurrentDictionary<string, HostingProvider> _providers = new(StringComparer.Ordinal);

    public IDisposable Register(string pluginId, HostingProvider provider)
    {
        _providers[provider.Id] = provider;
        server.Post(HostingMethods.RegisterProvider, new HostingProviderParams(pluginId, provider.Id), PluginJsonContext.Default.HostingProviderParams);
        return new PluginHostServer.Disposable(() =>
        {
            if (_providers.TryRemove(new KeyValuePair<string, HostingProvider>(provider.Id, provider)))
            {
                server.Post(HostingMethods.UnregisterProvider, new HostingProviderParams(pluginId, provider.Id), PluginJsonContext.Default.HostingProviderParams);
            }
        });
    }

    public void RegisterHandlers(JsonRpcConnection connection)
    {
        var json = PluginJsonContext.Default;
        connection.OnRequest(HostingMethods.Resolve, json.HostingResolveParams, json.HostingResolveResult, (p, _) => Call(p.ProviderId, provider =>
        {
            foreach (var remote in p.Remotes)
            {
                if (provider.ResolveRepository(remote.Url) is { } repository)
                {
                    return Task.FromResult(new HostingResolveResult(ToDto(repository with { Remote = remote.Remote })));
                }
            }

            return Task.FromResult(new HostingResolveResult(null));
        }));
        connection.OnRequest(HostingMethods.Account, json.HostingIdParams, json.HostingAccountResult, (p, ct) => Call(p.ProviderId, async provider =>
            new HostingAccountResult(await provider.GetAccountAsync(ct).ConfigureAwait(false) is { } account ? ToDto(account) : null)));
        connection.OnRequest(HostingMethods.SignIn, json.HostingSignInParams, json.HostingAccountDto, (p, ct) => Call(p.ProviderId, async provider =>
            ToDto(p.Token is { Length: > 0 } token
                ? await provider.SignInWithTokenAsync(token, ct).ConfigureAwait(false)
                : await provider.SignInAsync(new DeviceCodePrompt(server, provider.Id), ct).ConfigureAwait(false))));
        connection.OnRequest(HostingMethods.SignOut, json.HostingIdParams, json.EmptyResult, (p, ct) => Call(p.ProviderId, async provider =>
        {
            await provider.SignOutAsync(ct).ConfigureAwait(false);
            return new EmptyResult();
        }));
        connection.OnRequest(HostingMethods.RepositoryInfo, json.HostingRepositoryParams, json.HostingRepositoryInfoDto, (p, ct) => Call(p.ProviderId, async provider =>
        {
            var info = await provider.GetRepositoryInfoAsync(FromDto(p.Repository), ct).ConfigureAwait(false);
            return new HostingRepositoryInfoDto(info.DefaultBranch, info.WebUrl, info.IsPrivate);
        }));
        connection.OnRequest(HostingMethods.PullRequests, json.HostingPullRequestsParams, json.PullRequestsResult, (p, ct) => Call(p.ProviderId, async provider =>
        {
            var filter = p.Filter switch { "mine" => PullRequestFilter.Mine, "reviewRequested" => PullRequestFilter.ReviewRequested, _ => PullRequestFilter.All };
            var pullRequests = await provider.GetPullRequestsAsync(FromDto(p.Repository), filter, ct).ConfigureAwait(false);
            return new PullRequestsResult([.. pullRequests.Select(ToDto)]);
        }));
        connection.OnRequest(HostingMethods.PullRequest, json.HostingPullRequestParams, json.PullRequestDetailsDto, (p, ct) => Call(p.ProviderId, async provider =>
        {
            var details = await provider.GetPullRequestAsync(FromDto(p.Repository), p.Number, ct).ConfigureAwait(false);
            return new PullRequestDetailsDto(
                ToDto(details.PullRequest),
                details.Description,
                [.. details.Threads.Select(t => new ReviewThreadDto(t.Id, t.Path, t.Line, t.StartLine, t.IsResolved, t.IsOutdated, t.Url, [.. t.Comments.Select(ToDto)]))],
                [.. details.Comments.Select(ToDto)]);
        }));
        connection.OnRequest(HostingMethods.Checkout, json.HostingCheckoutParams, json.PullRequestCheckoutDto, (p, ct) => Call(p.ProviderId, async provider =>
        {
            var checkout = await provider.GetCheckoutAsync(FromDto(p.Repository), FromDto(p.PullRequest), ct).ConfigureAwait(false);
            return new PullRequestCheckoutDto(checkout.Remote, checkout.SourceRef, checkout.LocalBranch, checkout.Track);
        }));
        connection.OnRequest(HostingMethods.CreatePullRequest, json.HostingCreatePullRequestParams, json.PullRequestDto, (p, ct) => Call(p.ProviderId, async provider =>
            ToDto(await provider.CreatePullRequestAsync(
                FromDto(p.Repository),
                new NewPullRequest(p.Title, p.SourceBranch, p.TargetBranch) { Description = p.Description, IsDraft = p.IsDraft },
                ct).ConfigureAwait(false))));
        connection.OnRequest(HostingMethods.BranchStatus, json.HostingBranchStatusParams, json.BranchStatusDto, (p, ct) => Call(p.ProviderId, async provider =>
        {
            var status = await provider.GetBranchStatusAsync(FromDto(p.Repository), p.Branch, p.Commit, ct).ConfigureAwait(false);
            return new BranchStatusDto(status.State, [.. status.Checks.Select(c => new StatusCheckDto(c.Name, c.State, c.Description, c.Url))], status.Url);
        }));
        connection.OnRequest(HostingMethods.WorkItems, json.HostingWorkItemsParams, json.WorkItemsResult, (p, ct) => Call(p.ProviderId, async provider =>
        {
            var items = await provider.GetWorkItemsAsync(FromDto(p.Repository), new WorkItemQuery { AssignedToMe = p.AssignedToMe, Text = p.Text, Limit = p.Limit }, ct).ConfigureAwait(false);
            return new WorkItemsResult([.. items.Select(i => new WorkItemDto(i.Number, i.Title, i.Reference, i.WebUrl, i.State, i.Type, i.AssignedTo))]);
        }));
        connection.OnRequest(HostingMethods.Owners, json.HostingIdParams, json.HostingOwnersResult, (p, ct) => Call(p.ProviderId, async provider =>
            new HostingOwnersResult([.. (await provider.GetOwnersAsync(ct).ConfigureAwait(false)).Select(o => new HostingOwnerDto(o.Id, o.Name, o.Kind))])));
        connection.OnRequest(HostingMethods.Publish, json.HostingPublishParams, json.PublishedRepositoryDto, (p, ct) => Call(p.ProviderId, async provider =>
        {
            var published = await provider.PublishAsync(new NewRepository(p.Owner, p.Name) { Description = p.Description, IsPrivate = p.IsPrivate }, ct).ConfigureAwait(false);
            return new PublishedRepositoryDto(published.CloneUrl, published.WebUrl, published.SshUrl);
        }));
    }

    /// <summary>Runs a request on a provider; its failures become the hosting error codes (cancellation stays cancellation).</summary>
    private async Task<T> Call<T>(string providerId, Func<HostingProvider, Task<T>> body)
    {
        if (!_providers.TryGetValue(providerId, out var provider))
        {
            throw new JsonRpcHandlerException(HostingErrorCodes.NoProvider, $"No hosting provider '{providerId}' is registered.");
        }

        try
        {
            return await body(provider).ConfigureAwait(false);
        }
        catch (HostingAuthenticationException ex)
        {
            throw new JsonRpcHandlerException(HostingErrorCodes.NotSignedIn, ex.Message);
        }
        catch (NotSupportedException ex)
        {
            throw new JsonRpcHandlerException(HostingErrorCodes.NotSupported, ex.Message.Length > 0 ? ex.Message : "Not supported.");
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or JsonRpcHandlerException or OutOfMemoryException))
        {
            server.Log(null, "warning", $"hosting provider '{providerId}': {ex}");
            throw new JsonRpcHandlerException(HostingErrorCodes.Failed, ex.Message);
        }
    }

    private static HostingRepositoryDto ToDto(HostingRepository r) => new(r.Id, r.Owner, r.Name, r.WebUrl, r.Host, r.Remote);

    private static HostingRepository FromDto(HostingRepositoryDto r) => new(r.Id, r.Owner, r.Name, r.WebUrl) { Host = r.Host, Remote = r.Remote };

    private static HostingAccountDto ToDto(HostingAccount a) => new(a.Name, a.Host, a.DisplayName, a.Url);

    private static ReviewCommentDto ToDto(ReviewComment c) => new(c.Author, c.Body, c.CreatedAt, c.Url);

    internal static PullRequestDto ToDto(PullRequest p) => new(
        p.Number, p.Title, p.Author, p.SourceBranch, p.TargetBranch, p.WebUrl, p.Reference, p.IsDraft, p.State,
        p.CreatedAt, p.UpdatedAt, p.SourceCommit, p.SourceRepository, p.SourceCloneUrl, [.. p.Reviewers]);

    private static PullRequest FromDto(PullRequestDto p) => new(p.Number, p.Title, p.Author, p.SourceBranch, p.TargetBranch, p.WebUrl)
    {
        Reference = p.Reference,
        IsDraft = p.IsDraft,
        State = p.State,
        CreatedAt = p.CreatedAt,
        UpdatedAt = p.UpdatedAt,
        SourceCommit = p.SourceCommit,
        SourceRepository = p.SourceRepository,
        SourceCloneUrl = p.SourceCloneUrl,
        Reviewers = p.Reviewers,
    };

    /// <summary>Tells the shell's sign-in dialog the code to show.</summary>
    private sealed class DeviceCodePrompt(PluginHostServer server, string providerId) : IDeviceCodePrompt
    {
        public Task ShowAsync(DeviceCode code, CancellationToken cancellationToken)
        {
            server.Post(
                HostingMethods.DeviceCode,
                new HostingDeviceCodeParams(providerId, code.UserCode, code.VerificationUri, code.VerificationUriComplete, code.ExpiresAt, code.Message),
                PluginJsonContext.Default.HostingDeviceCodeParams);
            return Task.CompletedTask;
        }
    }
}

/// <summary>A plugin's secrets, through the shell's credential store.</summary>
internal sealed class HostSecrets(PluginHostServer server, string pluginId) : ISecrets
{
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
        (await server.RequestAsync(HostingMethods.SecretsGet, new SecretParams(pluginId, key, null), PluginJsonContext.Default.SecretParams, PluginJsonContext.Default.SecretResult, cancellationToken).ConfigureAwait(false)).Value;

    public Task StoreAsync(string key, string value, CancellationToken cancellationToken) =>
        server.RequestAsync(HostingMethods.SecretsStore, new SecretParams(pluginId, key, value), PluginJsonContext.Default.SecretParams, PluginJsonContext.Default.EmptyResult, cancellationToken);

    public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        server.RequestAsync(HostingMethods.SecretsDelete, new SecretParams(pluginId, key, null), PluginJsonContext.Default.SecretParams, PluginJsonContext.Default.EmptyResult, cancellationToken);
}
