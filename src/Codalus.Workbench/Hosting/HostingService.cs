using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Serialization;
using Codalus.Plugins.Protocol;
using Codalus.Rpc;
using Codalus.Workbench.Git;
using Codalus.Workbench.Plugins;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Hosting;

/// <summary>A hosting provider a plugin declares (<c>contributes.hostingProviders</c>).</summary>
public sealed record HostingProviderInfo(string Id, string Label, string PluginId, IReadOnlyList<string> Hosts, string? Icon)
{
    /// <summary>Its plugin is active and registered it.</summary>
    public bool IsRegistered { get; init; }

    /// <summary>The setting with its server's URL (<c>serverSetting</c>), if it has one.</summary>
    public string? ServerSetting { get; init; }

    /// <summary>The server in <see cref="ServerSetting"/> is self-hosted: its host isn't one of the provider's own.</summary>
    public bool IsSelfHosted { get; init; }

    /// <summary>It signs in as the operating system's user on a self-hosted server (<c>integratedSignIn</c>, API 1.2.0).</summary>
    public bool IntegratedSignIn { get; init; }
}

/// <summary>A self-hosted server a remote is on, for a provider not yet set up for it: sign in there (ADR 0087).</summary>
public sealed record HostingServerSuggestion(HostingProviderInfo Provider, string ServerUrl);

/// <summary>A signed-in account (not a secret: the token is in the OS credential store, kept by the plugin).</summary>
public sealed record HostingAccountRecord(string ProviderId, string Name, string Host)
{
    public string? DisplayName { get; init; }

    public string? Url { get; init; }
}

/// <summary>The open repository on a host: the provider, and the repository it resolved from a remote.</summary>
public sealed record HostingMatch(HostingProviderInfo Provider, HostingRepositoryDto Repository);

/// <summary>The user must sign in (again) to <see cref="ProviderId"/>.</summary>
public sealed class HostingSignInRequiredException(string providerId, string message) : Exception(message)
{
    public string ProviderId { get; } = providerId;
}

/// <summary>A hosting request failed: the host refused, is unreachable, or the provider can't do it.</summary>
public sealed class HostingRequestException(string message, bool notSupported = false) : Exception(message)
{
    public bool NotSupported { get; } = notSupported;
}

/// <summary>
/// Git hosting in the workbench (ADR 0031): the providers plugins declare, the signed-in accounts, which provider the
/// open repository belongs to, and requests to the providers. A provider's plugin is activated only when it is needed:
/// to sign in, or for a repository on a host where the user is signed in. Events come on any thread.
/// </summary>
public sealed class HostingService : IHostingSink
{
    public static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(15);

    private readonly PluginManager _plugins;
    private readonly string _accountsFile;
    private readonly Func<bool> _enabled;
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, IHostingConnection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IHostingConnection>> _waiting = new(StringComparer.Ordinal);
    private List<HostingAccountRecord>? _accounts;

    /// <param name="accountsFile">Where the non-secret account list is kept (the state folder's <c>hosting-accounts.json</c>).</param>
    /// <param name="enabled">The <c>hosting.enabled</c> setting.</param>
    public HostingService(PluginManager plugins, string accountsFile, Func<bool>? enabled = null)
    {
        _plugins = plugins;
        _accountsFile = accountsFile;
        _enabled = enabled ?? (() => true);
        _plugins.HostingSink = this;
        _plugins.PluginsChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Providers, registrations or accounts changed.</summary>
    public event EventHandler? Changed;

    /// <summary>A sign-in shows a device code.</summary>
    public event EventHandler<HostingDeviceCodeParams>? DeviceCodeReceived;

    /// <summary>A provider's session expired or was revoked (the provider's id): its account was removed.</summary>
    public event EventHandler<string>? SignInRequired;

    public bool IsEnabled => _enabled();

    /// <summary>The declared providers, with the host of their configured server (<c>serverSetting</c>) among their hosts.</summary>
    public IReadOnlyList<HostingProviderInfo> Providers =>
        [.. _plugins.HostingProviderDeclarations
            .GroupBy(d => d.Provider.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(d => new HostingProviderInfo(d.Provider.Id, d.Provider.Label, d.Plugin.Id, [.. d.Provider.Hosts ?? [], .. ServerHost(d.Provider.ServerSetting)], d.Provider.Icon)
            {
                IsRegistered = _connections.ContainsKey(d.Provider.Id),
                ServerSetting = d.Provider.ServerSetting,
                IsSelfHosted = ServerHost(d.Provider.ServerSetting).Any(host => !(d.Provider.Hosts ?? []).Any(pattern => HostMatches(host, pattern))),
                IntegratedSignIn = d.Provider.IntegratedSignIn,
            })];

    /// <summary>Reads a setting's text (the provider's server URL); set by the shell.</summary>
    public Func<string, string?>? ReadSetting { get; set; }

    private IEnumerable<string> ServerHost(string? setting) =>
        setting is not null && ReadSetting?.Invoke(setting) is { Length: > 0 } url && HostOf(url.Trim().TrimEnd('/') + "/x.git") is { } host ? [host] : [];

    public HostingProviderInfo? Provider(string id) => Providers.FirstOrDefault(p => p.Id == id);

    public IReadOnlyList<HostingAccountRecord> Accounts
    {
        get
        {
            lock (_gate)
            {
                return [.. LoadAccounts()];
            }
        }
    }

    public HostingAccountRecord? AccountFor(string providerId) => Accounts.FirstOrDefault(a => a.ProviderId == providerId);

    // ------------------------------------------------------------------ IHostingSink

    void IHostingSink.ProviderRegistered(string pluginId, string providerId, IHostingConnection connection)
    {
        _connections[providerId] = connection;
        if (_waiting.TryRemove(providerId, out var waiting))
        {
            waiting.TrySetResult(connection);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    void IHostingSink.ProviderUnregistered(string providerId)
    {
        _connections.TryRemove(providerId, out _);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void IHostingSink.DeviceCodeReceived(HostingDeviceCodeParams code) => DeviceCodeReceived?.Invoke(this, code);

    // ------------------------------------------------------------------ hosts and repositories

    /// <summary>The host of a remote URL: <c>https://host[:port]/…</c>, <c>ssh://[user@]host[:port]/…</c> or scp-like <c>[user@]host:path</c>.</summary>
    public static string? HostOf(string remoteUrl)
    {
        var url = remoteUrl.Trim();
        if (url.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            {
                return null;
            }

            var keepPort = uri.Scheme is "http" or "https" && !uri.IsDefaultPort;
            return (keepPort ? uri.Host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture) : uri.Host).ToLowerInvariant();
        }

        // scp-like: [user@]host:path (a Windows drive letter is a path, not a host).
        var colon = url.IndexOf(':', StringComparison.Ordinal);
        var slash = url.IndexOf('/', StringComparison.Ordinal);
        if (colon > 1 && (slash < 0 || colon < slash))
        {
            var host = url[..colon];
            var at = host.LastIndexOf('@');
            return (at >= 0 ? host[(at + 1)..] : host).ToLowerInvariant();
        }

        return null;
    }

    /// <summary>Whether a host matches a provider's host pattern (exact, or <c>*.example.com</c>).</summary>
    public static bool HostMatches(string host, string pattern) =>
        pattern.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
            : string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);

    private bool Serves(HostingProviderInfo provider, string host) =>
        provider.Hosts.Any(pattern => HostMatches(host, pattern))
        || Accounts.Any(a => a.ProviderId == provider.Id && HostMatches(host, a.Host));

    /// <summary>Remotes in the order to try: <c>origin</c>, <c>upstream</c>, then the others.</summary>
    public static IReadOnlyList<GitRemote> Ordered(IReadOnlyList<GitRemote> remotes) =>
        [.. remotes.OrderBy(r => r.Name switch { "origin" => 0, "upstream" => 1, _ => 2 }).ThenBy(r => r.Name, StringComparer.Ordinal)];

    /// <summary>A provider that serves one of the remotes' hosts, signed in or not (no plugin is activated).</summary>
    public HostingProviderInfo? SuggestFor(IReadOnlyList<GitRemote> remotes)
    {
        var hosts = Ordered(remotes).Select(r => HostOf(r.FetchUrl)).OfType<string>().ToList();
        return hosts.Select(host => Providers.FirstOrDefault(p => Serves(p, host))).OfType<HostingProviderInfo>().FirstOrDefault();
    }

    /// <summary>
    /// A self-hosted server one of the remotes is on, when no provider serves its host yet: an Azure DevOps Server, whose
    /// remotes say what they are (<see cref="AzureDevOpsServerOf"/>), for the provider <c>azure-devops</c> if it is installed.
    /// </summary>
    public HostingServerSuggestion? SuggestServerFor(IReadOnlyList<GitRemote> remotes)
    {
        var providers = Providers;
        foreach (var remote in Ordered(remotes))
        {
            if (HostOf(remote.FetchUrl) is not { } host || providers.Any(p => Serves(p, host)))
            {
                continue;
            }

            if (AzureDevOpsServerOf(remote.FetchUrl) is { } server && providers.FirstOrDefault(p => p.Id == "azure-devops" && p.ServerSetting is not null) is { } provider)
            {
                return new HostingServerSuggestion(provider, server);
            }
        }

        return null;
    }

    /// <summary>
    /// The Azure DevOps Server a remote URL is on, from its path: <c>…/{collection}/{project}/_git/{repository}</c> or
    /// <c>…/{collection}/_git/{repository}</c>, the server being what comes before the collection
    /// (<c>https://tfs.contoso.com/tfs</c>; with two segments before <c>_git</c>, a first <c>tfs</c> is the server's path).
    /// An SSH remote's server is HTTPS on the same host. Null for any other remote.
    /// </summary>
    public static string? AzureDevOpsServerOf(string remoteUrl)
    {
        var url = remoteUrl.Trim();
        string origin, path;
        if (url.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            {
                return null;
            }

            origin = uri.Scheme is "http" or "https" ? $"{uri.Scheme}://{uri.Authority}" : $"https://{uri.Host}";
            path = uri.AbsolutePath;
        }
        else if (HostOf(url) is { } host)
        {
            origin = "https://" + host;
            path = url[(url.IndexOf(':', StringComparison.Ordinal) + 1)..];
        }
        else
        {
            return null;
        }

        var segments = Uri.UnescapeDataString(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var git = Array.IndexOf(segments, "_git");
        if (git < 1 || git != segments.Length - 2)
        {
            return null;
        }

        var prefix = git switch
        {
            >= 3 => git - 2,
            2 when string.Equals(segments[0], "tfs", StringComparison.OrdinalIgnoreCase) => 1,
            _ => 0,
        };
        return origin + string.Concat(segments[..prefix].Select(s => "/" + Uri.EscapeDataString(s)));
    }

    /// <summary>
    /// The provider and repository of the remotes: the first provider the user is signed in to whose host one of them is
    /// on, asked to resolve them (activating its plugin). Null when there is none, or hosting is turned off.
    /// </summary>
    public async Task<HostingMatch?> MatchAsync(IReadOnlyList<GitRemote> remotes, CancellationToken cancellationToken)
    {
        if (!IsEnabled || remotes.Count == 0)
        {
            return null;
        }

        var ordered = Ordered(remotes);
        var hosts = ordered.Select(r => HostOf(r.FetchUrl)).OfType<string>().ToList();
        foreach (var provider in Providers.Where(p => AccountFor(p.Id) is not null && hosts.Any(h => Serves(p, h))))
        {
            var result = await InvokeAsync(
                provider.Id,
                HostingMethods.Resolve,
                new HostingResolveParams(provider.Id, [.. ordered.Select(r => new HostingRemoteDto(r.Name, r.FetchUrl))]),
                PluginJsonContext.Default.HostingResolveParams,
                PluginJsonContext.Default.HostingResolveResult,
                cancellationToken).ConfigureAwait(false);
            if (result.Repository is { } repository)
            {
                return new HostingMatch(Provider(provider.Id) ?? provider, repository);
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ accounts

    /// <summary>
    /// Signs in: the device flow (the provider sends a code, see <see cref="DeviceCodeReceived"/>), a token, or with
    /// <paramref name="integrated"/> the operating system's account (<see cref="HostingProviderInfo.IntegratedSignIn"/>).
    /// </summary>
    public async Task<HostingAccountRecord> SignInAsync(string providerId, string? token, CancellationToken cancellationToken, bool integrated = false)
    {
        var account = await InvokeAsync(providerId, HostingMethods.SignIn, new HostingSignInParams(providerId, token) { Integrated = integrated }, PluginJsonContext.Default.HostingSignInParams, PluginJsonContext.Default.HostingAccountDto, cancellationToken).ConfigureAwait(false);
        var record = new HostingAccountRecord(providerId, account.Name, account.Host) { DisplayName = account.DisplayName, Url = account.Url };
        UpdateAccounts(list =>
        {
            list.RemoveAll(a => a.ProviderId == providerId);
            list.Add(record);
        });
        return record;
    }

    /// <summary>Signs out: the provider deletes its secrets, and the account is forgotten.</summary>
    public async Task SignOutAsync(string providerId, CancellationToken cancellationToken)
    {
        try
        {
            await InvokeAsync(providerId, HostingMethods.SignOut, new HostingIdParams(providerId), PluginJsonContext.Default.HostingIdParams, PluginJsonContext.Default.EmptyResult, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            UpdateAccounts(list => list.RemoveAll(a => a.ProviderId == providerId));
        }
    }

    /// <summary>Asks the provider whether its stored session is still there (its plugin is activated).</summary>
    public async Task<HostingAccountDto?> CheckAccountAsync(string providerId, CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(providerId, HostingMethods.Account, new HostingIdParams(providerId), PluginJsonContext.Default.HostingIdParams, PluginJsonContext.Default.HostingAccountResult, cancellationToken).ConfigureAwait(false);
        if (result.Account is null)
        {
            UpdateAccounts(list => list.RemoveAll(a => a.ProviderId == providerId));
        }

        return result.Account;
    }

    private List<HostingAccountRecord> LoadAccounts() =>
        _accounts ??= [.. StateFile.Read(_accountsFile, HostingJsonContext.Default.HostingAccountsState)?.Accounts ?? []];

    private void UpdateAccounts(Action<List<HostingAccountRecord>> change)
    {
        lock (_gate)
        {
            var list = LoadAccounts();
            change(list);
            StateFile.Write(_accountsFile, new HostingAccountsState { Accounts = [.. list] }, HostingJsonContext.Default.HostingAccountsState);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ requests

    public Task<HostingRepositoryInfoDto> RepositoryInfoAsync(HostingMatch match, CancellationToken cancellationToken) =>
        InvokeAsync(match.Provider.Id, HostingMethods.RepositoryInfo, new HostingRepositoryParams(match.Provider.Id, match.Repository), PluginJsonContext.Default.HostingRepositoryParams, PluginJsonContext.Default.HostingRepositoryInfoDto, cancellationToken);

    /// <summary>Open pull requests; <paramref name="filter"/> is <c>all</c>, <c>mine</c> or <c>reviewRequested</c>.</summary>
    public async Task<IReadOnlyList<PullRequestDto>> PullRequestsAsync(HostingMatch match, string filter, CancellationToken cancellationToken) =>
        (await InvokeAsync(match.Provider.Id, HostingMethods.PullRequests, new HostingPullRequestsParams(match.Provider.Id, match.Repository, filter), PluginJsonContext.Default.HostingPullRequestsParams, PluginJsonContext.Default.PullRequestsResult, cancellationToken).ConfigureAwait(false)).PullRequests;

    public Task<PullRequestDetailsDto> PullRequestAsync(HostingMatch match, string number, CancellationToken cancellationToken) =>
        InvokeAsync(match.Provider.Id, HostingMethods.PullRequest, new HostingPullRequestParams(match.Provider.Id, match.Repository, number), PluginJsonContext.Default.HostingPullRequestParams, PluginJsonContext.Default.PullRequestDetailsDto, cancellationToken);

    public Task<PullRequestCheckoutDto> CheckoutAsync(HostingMatch match, PullRequestDto pullRequest, CancellationToken cancellationToken) =>
        InvokeAsync(match.Provider.Id, HostingMethods.Checkout, new HostingCheckoutParams(match.Provider.Id, match.Repository, pullRequest), PluginJsonContext.Default.HostingCheckoutParams, PluginJsonContext.Default.PullRequestCheckoutDto, cancellationToken);

    public Task<PullRequestDto> CreatePullRequestAsync(HostingMatch match, string title, string description, string sourceBranch, string targetBranch, bool isDraft, CancellationToken cancellationToken) =>
        InvokeAsync(
            match.Provider.Id,
            HostingMethods.CreatePullRequest,
            new HostingCreatePullRequestParams(match.Provider.Id, match.Repository, title, description, sourceBranch, targetBranch, isDraft),
            PluginJsonContext.Default.HostingCreatePullRequestParams,
            PluginJsonContext.Default.PullRequestDto,
            cancellationToken);

    public Task<BranchStatusDto> BranchStatusAsync(HostingMatch match, string branch, string? commit, CancellationToken cancellationToken) =>
        InvokeAsync(match.Provider.Id, HostingMethods.BranchStatus, new HostingBranchStatusParams(match.Provider.Id, match.Repository, branch, commit), PluginJsonContext.Default.HostingBranchStatusParams, PluginJsonContext.Default.BranchStatusDto, cancellationToken);

    public async Task<IReadOnlyList<WorkItemDto>> WorkItemsAsync(HostingMatch match, bool assignedToMe, string? text, int limit, CancellationToken cancellationToken) =>
        (await InvokeAsync(match.Provider.Id, HostingMethods.WorkItems, new HostingWorkItemsParams(match.Provider.Id, match.Repository, assignedToMe, text, limit), PluginJsonContext.Default.HostingWorkItemsParams, PluginJsonContext.Default.WorkItemsResult, cancellationToken).ConfigureAwait(false)).WorkItems;

    public async Task<IReadOnlyList<HostingOwnerDto>> OwnersAsync(string providerId, CancellationToken cancellationToken) =>
        (await InvokeAsync(providerId, HostingMethods.Owners, new HostingIdParams(providerId), PluginJsonContext.Default.HostingIdParams, PluginJsonContext.Default.HostingOwnersResult, cancellationToken).ConfigureAwait(false)).Owners;

    public Task<PublishedRepositoryDto> PublishAsync(string providerId, string owner, string name, string? description, bool isPrivate, CancellationToken cancellationToken) =>
        InvokeAsync(providerId, HostingMethods.Publish, new HostingPublishParams(providerId, owner, name, description, isPrivate), PluginJsonContext.Default.HostingPublishParams, PluginJsonContext.Default.PublishedRepositoryDto, cancellationToken);

    /// <summary>Activates the provider's plugin if needed, waits for the provider, and sends the request; errors become <see cref="HostingRequestException"/> or <see cref="HostingSignInRequiredException"/>.</summary>
    private async Task<TResult> InvokeAsync<TParams, TResult>(
        string providerId,
        string method,
        TParams parameters,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TParams> paramsInfo,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResult> resultInfo,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            throw new HostingRequestException("Hosting integration is turned off (the setting hosting.enabled).");
        }

        var connection = await ConnectAsync(providerId, cancellationToken).ConfigureAwait(false);
        try
        {
            return await connection.InvokeAsync(method, parameters, paramsInfo, resultInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcRemoteException ex) when (ex.Code == HostingErrorCodes.NotSignedIn)
        {
            UpdateAccounts(list => list.RemoveAll(a => a.ProviderId == providerId));
            SignInRequired?.Invoke(this, providerId);
            throw new HostingSignInRequiredException(providerId, ex.Message);
        }
        catch (JsonRpcRemoteException ex)
        {
            throw new HostingRequestException(ex.Message, ex.Code == HostingErrorCodes.NotSupported);
        }
        catch (JsonRpcConnectionLostException ex)
        {
            throw new HostingRequestException("The plugin host stopped: " + ex.Message);
        }
    }

    /// <summary>The provider's connection: activates its plugin (<c>onHostingProvider:&lt;id&gt;</c>) and waits for the registration.</summary>
    private async Task<IHostingConnection> ConnectAsync(string providerId, CancellationToken cancellationToken)
    {
        if (_connections.TryGetValue(providerId, out var connection))
        {
            return connection;
        }

        var declaration = _plugins.HostingProviderDeclarations.FirstOrDefault(d => d.Provider.Id == providerId);
        if (declaration.Plugin is null)
        {
            throw new HostingRequestException($"No plugin provides '{providerId}'.");
        }

        var waiting = _waiting.GetOrAdd(providerId, _ => new TaskCompletionSource<IHostingConnection>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (_connections.TryGetValue(providerId, out connection))
        {
            return connection;
        }

        if (!await _plugins.ActivateAsync(declaration.Plugin, "onHostingProvider:" + providerId, cancellationToken).ConfigureAwait(false))
        {
            _waiting.TryRemove(providerId, out _);
            throw new HostingRequestException($"The {declaration.Provider.Label} plugin isn't available: {declaration.Plugin.Error}");
        }

        try
        {
            return await waiting.Task.WaitAsync(RegistrationTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _waiting.TryRemove(providerId, out _);
            throw new HostingRequestException($"The {declaration.Provider.Label} plugin didn't register its provider.");
        }
    }
}

internal sealed record HostingAccountsState
{
    public IReadOnlyList<HostingAccountRecord>? Accounts { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HostingAccountsState))]
internal sealed partial class HostingJsonContext : JsonSerializerContext;
