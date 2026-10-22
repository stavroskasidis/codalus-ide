using System.Collections.Concurrent;
using Codalus.Platform.Credentials;

namespace Codalus.Tests;

/// <summary>
/// A credential store that says it keeps secrets across restarts (as the OS's would) but holds them in memory, so a test
/// reaches no real Credential Manager, Keychain or Secret Service (ADR 0083). <see cref="Fail"/> makes every call throw
/// <see cref="CredentialStoreUnavailableException"/>, as a locked or missing OS store does; <see cref="Reads"/> counts reads.
/// </summary>
public sealed class FakePersistentCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<(string Service, string Account), string> _secrets = new();
    private int _reads;

    public string Name => "Test Credential Store";

    public bool IsPersistent => true;

    /// <summary>Every call fails as an unreachable OS store does.</summary>
    public bool Fail { get; set; }

    /// <summary>How many reads reached the store.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <summary>The secrets as stored (account → value), for assertions.</summary>
    public IReadOnlyDictionary<string, string> Stored => _secrets.ToDictionary(e => e.Key.Account, e => e.Value, StringComparer.Ordinal);

    public Task<string?> GetAsync(string service, string account, CancellationToken cancellationToken = default)
    {
        Check();
        Interlocked.Increment(ref _reads);
        return Task.FromResult(_secrets.TryGetValue((service, account), out var secret) ? secret : null);
    }

    public Task SetAsync(string service, string account, string secret, CancellationToken cancellationToken = default)
    {
        Check();
        _secrets[(service, account)] = secret;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string service, string account, CancellationToken cancellationToken = default)
    {
        Check();
        return Task.FromResult(_secrets.TryRemove((service, account), out _));
    }

    private void Check()
    {
        if (Fail)
        {
            throw new CredentialStoreUnavailableException("The test's credential store is locked.");
        }
    }
}
