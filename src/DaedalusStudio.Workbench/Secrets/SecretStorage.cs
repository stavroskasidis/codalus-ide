using DaedalusStudio.Platform.Credentials;

namespace DaedalusStudio.Workbench.Secrets;

/// <summary>
/// Plugins' secrets (ADR 0031): kept in the OS credential store under the service <see cref="Service"/> and the account
/// <c>&lt;plugin id&gt;/&lt;key&gt;</c>, never in a file. When the OS store can't be reached, secrets are kept in memory
/// for the session and <see cref="FellBack"/> says why, once.
/// </summary>
public sealed class SecretStorage
{
    public const string Service = "Daedalus Studio";

    private readonly ICredentialStore _store;
    private readonly InMemoryCredentialStore _fallback = new();
    private int _fellBack;

    /// <param name="service">The service secrets are kept under: <see cref="Service"/>, or <see cref="ServiceFor"/> a user-data folder.</param>
    public SecretStorage(ICredentialStore store, string service = Service)
    {
        _store = store;
        ServiceName = service;
    }

    /// <summary>The service the secrets are kept under.</summary>
    public string ServiceName { get; }

    /// <summary>
    /// The service for a profile: <see cref="Service"/> for the default one, and a service of its own for a
    /// <c>--user-data-dir</c> folder (tests, the smoke test, portable installs), so their sign-ins stay apart.
    /// </summary>
    public static string ServiceFor(string? userDataDir) =>
        userDataDir is not { Length: > 0 }
            ? Service
            : $"{Service} ({Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(userDataDir).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..8].ToLowerInvariant()})";

    /// <summary>The store secrets go to now.</summary>
    public string StoreName => Volatile.Read(ref _fellBack) == 1 ? _fallback.Name : _store.Name;

    /// <summary>Whether secrets survive a restart.</summary>
    public bool IsPersistent => Volatile.Read(ref _fellBack) == 0 && _store.IsPersistent;

    /// <summary>The OS store failed and secrets are kept in memory from now on (the reason; raised once, on any thread).</summary>
    public event EventHandler<string>? FellBack;

    public Task<string?> GetAsync(string pluginId, string key, CancellationToken cancellationToken) =>
        UseAsync(store => store.GetAsync(ServiceName, Account(pluginId, key), cancellationToken));

    public Task StoreAsync(string pluginId, string key, string value, CancellationToken cancellationToken) =>
        UseAsync<object?>(async store =>
        {
            await store.SetAsync(ServiceName, Account(pluginId, key), value, cancellationToken).ConfigureAwait(false);
            return null;
        });

    public Task<bool> DeleteAsync(string pluginId, string key, CancellationToken cancellationToken) =>
        UseAsync(store => store.DeleteAsync(ServiceName, Account(pluginId, key), cancellationToken));

    private static string Account(string pluginId, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("A secret needs a key.", nameof(key));
        }

        return pluginId + "/" + key;
    }

    private async Task<T> UseAsync<T>(Func<ICredentialStore, Task<T>> operation)
    {
        if (Volatile.Read(ref _fellBack) == 0)
        {
            try
            {
                return await operation(_store).ConfigureAwait(false);
            }
            catch (CredentialStoreUnavailableException ex)
            {
                if (Interlocked.Exchange(ref _fellBack, 1) == 0)
                {
                    FellBack?.Invoke(this, ex.Message);
                }
            }
        }

        return await operation(_fallback).ConfigureAwait(false);
    }
}
