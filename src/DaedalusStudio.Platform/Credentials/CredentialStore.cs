using System.Collections.Concurrent;

namespace DaedalusStudio.Platform.Credentials;

/// <summary>
/// The OS's secret store (ADR 0031): Windows Credential Manager, the macOS Keychain, or the Secret Service through
/// libsecret on Linux. Daedalus Studio never writes a secret anywhere else. A secret is found by a service (the
/// product, <c>Daedalus Studio</c>) and an account (what the secret is for). Calls never block the caller's thread.
/// </summary>
public interface ICredentialStore
{
    /// <summary>What the store is, for messages: "Windows Credential Manager", "macOS Keychain"…</summary>
    string Name { get; }

    /// <summary>Whether secrets outlive the process (false for the in-memory store).</summary>
    bool IsPersistent { get; }

    /// <summary>The secret, or null when there is none.</summary>
    /// <exception cref="CredentialStoreUnavailableException">The store can't be reached.</exception>
    Task<string?> GetAsync(string service, string account, CancellationToken cancellationToken = default);

    /// <summary>Stores or replaces the secret.</summary>
    /// <exception cref="CredentialStoreUnavailableException">The store can't be reached.</exception>
    Task SetAsync(string service, string account, string secret, CancellationToken cancellationToken = default);

    /// <summary>Deletes the secret; false when there was none.</summary>
    /// <exception cref="CredentialStoreUnavailableException">The store can't be reached.</exception>
    Task<bool> DeleteAsync(string service, string account, CancellationToken cancellationToken = default);
}

/// <summary>The OS store isn't there or refused access (no libsecret or Secret Service, a locked keychain…).</summary>
public sealed class CredentialStoreUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Chooses the credential store for this OS.</summary>
public static class CredentialStore
{
    /// <summary><c>memory</c> keeps secrets in this process only (tests, the smoke test). Documented in docs/user-guide/git.md.</summary>
    public const string EnvironmentVariable = "DAEDALUS_CREDENTIAL_STORE";

    /// <summary>The OS's store, or the in-memory store when <see cref="EnvironmentVariable"/> says <c>memory</c>.</summary>
    public static ICredentialStore Create(Func<string, string?>? getEnvironment = null)
    {
        var choice = (getEnvironment ?? Environment.GetEnvironmentVariable)(EnvironmentVariable);
        if (string.Equals(choice, "memory", StringComparison.OrdinalIgnoreCase))
        {
            return new InMemoryCredentialStore();
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsCredentialStore();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacKeychainCredentialStore();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LibsecretCredentialStore();
        }

        return new InMemoryCredentialStore();
    }
}

/// <summary>Secrets kept in this process only, never written anywhere: for tests, and when no OS store is available.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly ConcurrentDictionary<(string Service, string Account), string> _secrets = new();

    public string Name => "memory (this session only)";

    public bool IsPersistent => false;

    public Task<string?> GetAsync(string service, string account, CancellationToken cancellationToken = default) =>
        Task.FromResult(_secrets.TryGetValue((service, account), out var secret) ? secret : null);

    public Task SetAsync(string service, string account, string secret, CancellationToken cancellationToken = default)
    {
        _secrets[(service, account)] = secret;
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string service, string account, CancellationToken cancellationToken = default) =>
        Task.FromResult(_secrets.TryRemove((service, account), out _));
}
