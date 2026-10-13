using System.Collections.Concurrent;

namespace Codalus.Plugins.Testing;

/// <summary>File system providers in a test: the plugin's, by scheme, to call as the IDE would.</summary>
public sealed class TestFileSystems : IFileSystems
{
    private readonly ConcurrentDictionary<string, IFileSystemProvider> _providers = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<(string Scheme, IReadOnlyList<string> Paths, string Kind)> _changes = new();

    /// <summary>The registered providers, by scheme.</summary>
    public IReadOnlyDictionary<string, IFileSystemProvider> Providers => _providers;

    /// <summary>The changes the plugin announced, in order.</summary>
    public IReadOnlyList<(string Scheme, IReadOnlyList<string> Paths, string Kind)> Changes => [.. _changes];

    /// <summary>The provider of <paramref name="scheme"/>; throws when there's none.</summary>
    public IFileSystemProvider this[string scheme] =>
        _providers.TryGetValue(scheme, out var provider) ? provider : throw new InvalidOperationException($"No file system provider for '{scheme}'.");

    /// <inheritdoc/>
    public IDisposable RegisterProvider(string scheme, IFileSystemProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!_providers.TryAdd(scheme, provider))
        {
            throw new InvalidOperationException($"The scheme '{scheme}' is registered already.");
        }

        return new Registration(() => _providers.TryRemove(scheme, out _));
    }

    /// <summary>A root under the temporary folder, as the IDE's is a path on this machine.</summary>
    public string RootOf(string scheme) => Path.Combine(Path.GetTempPath(), "codalus-plugin", scheme);

    /// <inheritdoc/>
    public void NotifyChanged(string scheme, IReadOnlyList<string> paths, string kind = "changed") => _changes.Enqueue((scheme, paths, kind));
}

/// <summary>
/// Authentication in a test: the plugin's providers (call them as the IDE would), and the sessions it asks of other
/// providers, which the test supplies in <see cref="Sessions"/>.
/// </summary>
public sealed class TestAuthentication : IAuthentication
{
    private readonly ConcurrentDictionary<string, (string Label, IAuthenticationProvider Provider)> _providers = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _changed = new();

    /// <summary>The registered providers, by id.</summary>
    public IReadOnlyDictionary<string, (string Label, IAuthenticationProvider Provider)> Providers => _providers;

    /// <summary>Sessions of other plugins' providers that <see cref="GetSessionAsync"/> gives, by provider id.</summary>
    public ConcurrentDictionary<string, AuthenticationSession> Sessions { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether the user consents when the plugin asks for another plugin's session (default: yes).</summary>
    public bool Consent { get; set; } = true;

    /// <summary>The providers whose sessions the plugin said changed, in order.</summary>
    public IReadOnlyList<string> SessionsChanged => [.. _changed];

    /// <inheritdoc/>
    public IDisposable RegisterProvider(string id, string label, IAuthenticationProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers[id] = (label, provider);
        return new Registration(() => _providers.TryRemove(id, out _));
    }

    /// <summary>
    /// The plugin's own provider answers as it would in the IDE (an existing session with the scopes, or a new sign-in);
    /// another provider's session comes from <see cref="Sessions"/>, when <see cref="Consent"/> allows it.
    /// </summary>
    public async Task<AuthenticationSession?> GetSessionAsync(string providerId, IReadOnlyList<string> scopes, bool createIfNone, CancellationToken cancellationToken)
    {
        if (_providers.TryGetValue(providerId, out var own))
        {
            var sessions = await own.Provider.GetSessionsAsync(scopes, cancellationToken).ConfigureAwait(false);
            return sessions.Count > 0 ? sessions[0] : createIfNone ? await own.Provider.CreateSessionAsync(scopes, cancellationToken).ConfigureAwait(false) : null;
        }

        return Consent && Sessions.TryGetValue(providerId, out var session) ? session : null;
    }

    /// <inheritdoc/>
    public void NotifySessionsChanged(string providerId) => _changed.Enqueue(providerId);
}

/// <summary>A source control provider the plugin registered, and the state it shows.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Label">Its name.</param>
/// <param name="Root">Its folder.</param>
/// <param name="Provider">The provider, to commit or ask for originals.</param>
public sealed record TestSourceControlProvider(string Id, string Label, string Root, ISourceControlProvider Provider)
{
    /// <summary>What it shows now (null until its first update).</summary>
    public SourceControlState? State { get; internal set; }
}

/// <summary>Source control providers in a test.</summary>
public sealed class TestSourceControl : ISourceControl
{
    private readonly ConcurrentDictionary<string, TestSourceControlProvider> _providers = new(StringComparer.Ordinal);

    /// <summary>The registered providers, by id.</summary>
    public IReadOnlyDictionary<string, TestSourceControlProvider> Providers => _providers;

    /// <inheritdoc/>
    public IDisposable RegisterProvider(string id, string label, string root, ISourceControlProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers[id] = new TestSourceControlProvider(id, label, root, provider);
        return new Registration(() => _providers.TryRemove(id, out _));
    }

    /// <inheritdoc/>
    public void Update(string id, SourceControlState state)
    {
        if (!_providers.TryGetValue(id, out var provider))
        {
            throw new InvalidOperationException($"No source control provider '{id}' is registered.");
        }

        provider.State = state;
    }
}
