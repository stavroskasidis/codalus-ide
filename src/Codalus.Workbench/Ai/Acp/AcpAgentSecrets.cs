using Codalus.Workbench.Secrets;

namespace Codalus.Workbench.Ai.Acp;

/// <summary>
/// External agents' keys (<c>ai.agents.&lt;id&gt;.secretEnv</c>, ADR 0083): kept in the OS credential store through
/// <see cref="SecretStorage"/> under the id <see cref="Id"/> and the key <c>&lt;agent&gt;/&lt;NAME&gt;</c>, never in a file.
/// The id starts with <see cref="ShellPrefix"/>, which the plugin hosts' <c>secrets/*</c> requests may never name, so no
/// plugin can read them. Settings hold the variables' names only.
/// </summary>
public sealed class AcpAgentSecrets(SecretStorage storage)
{
    /// <summary>The ids the shell keeps for itself: a plugin's <c>secrets/*</c> request naming one is refused.</summary>
    public const string ShellPrefix = "codalus.shell.";

    /// <summary>The id agents' keys are kept under.</summary>
    public const string Id = ShellPrefix + "agents";

    private const string ProbeKey = "probe/CODALUS_PROBE";

    /// <summary>Where the keys go now (Windows Credential Manager, macOS Keychain, Secret Service, or memory).</summary>
    public string StoreName => storage.StoreName;

    /// <summary>Whether a key survives a restart: false once the OS store failed, or for the in-memory store.</summary>
    public bool IsPersistent => storage.IsPersistent;

    /// <summary>An agent's key was stored or deleted (any thread): a session started with the old one restarts.</summary>
    public event EventHandler<string>? Changed;

    /// <summary>
    /// Asks the store once (a key that isn't there), so a store that can't be reached falls back to memory before the
    /// dialog says where a key would go: <see cref="SecretStorage.IsPersistent"/> is true until a call fails.
    /// </summary>
    public async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        await storage.GetAsync(Id, ProbeKey, cancellationToken).ConfigureAwait(false);
        return storage.IsPersistent;
    }

    public Task<string?> GetAsync(string agent, string name, CancellationToken cancellationToken) =>
        storage.GetAsync(Id, Key(agent, name), cancellationToken);

    public async Task StoreAsync(string agent, string name, string value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        await storage.StoreAsync(Id, Key(agent, name), value, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, agent);
    }

    public async Task<bool> DeleteAsync(string agent, string name, CancellationToken cancellationToken)
    {
        var deleted = await storage.DeleteAsync(Id, Key(agent, name), cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, agent);
        return deleted;
    }

    /// <summary>Deletes each of <paramref name="names"/> of <paramref name="agent"/> (Disconnect, a failed or cancelled Connect).</summary>
    public async Task DeleteAllAsync(string agent, IEnumerable<string> names, CancellationToken cancellationToken)
    {
        foreach (var name in names)
        {
            await storage.DeleteAsync(Id, Key(agent, name), cancellationToken).ConfigureAwait(false);
        }

        Changed?.Invoke(this, agent);
    }

    /// <summary>
    /// The values of <paramref name="names"/>, read once for a session; the names that have none are in
    /// <c>Missing</c>.
    /// </summary>
    public async Task<(IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Missing)> ResolveAsync(string agent, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var name in names)
        {
            if (await GetAsync(agent, name, cancellationToken).ConfigureAwait(false) is { Length: > 0 } value)
            {
                values[name] = value;
            }
            else
            {
                missing.Add(name);
            }
        }

        return (values, missing);
    }

    private static string Key(string agent, string name) => agent + "/" + name;
}
