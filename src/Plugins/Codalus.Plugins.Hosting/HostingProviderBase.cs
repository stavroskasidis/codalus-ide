using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Codalus.Plugins.Proposed;

namespace Codalus.Plugins.Hosting;

/// <summary>
/// A signed-in session, kept with <see cref="ISecrets"/> (the OS credential store) as JSON. <see cref="Scheme"/> is
/// <c>bearer</c> (OAuth tokens, most personal access tokens) or <c>basic</c> (Azure DevOps personal access tokens).
/// </summary>
public sealed record HostingSession
{
    public required string Host { get; init; }

    public required string AccessToken { get; init; }

    public string? RefreshToken { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public string Scheme { get; init; } = "bearer";

    public required string Account { get; init; }

    public string? DisplayName { get; init; }

    public string? Url { get; init; }

    /// <summary>The host's id of the user (Azure DevOps and GitLab filter by it).</summary>
    public string? UserId { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HostingSession))]
internal sealed partial class HostingSessionJsonContext : JsonSerializerContext;

/// <summary>
/// What the built-in providers share (ADR 0031): the session in the OS credential store (one per provider, for the
/// configured server), the Authorization header on API calls (refreshing an expiring OAuth token first), sign-out.
/// </summary>
public abstract class HostingProviderBase : HostingProvider, IDisposable
{
    private const string SessionKey = "session";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HostingSession? _session;
    private bool _loaded;

    protected HostingProviderBase(string id, IPluginContext context)
        : base(id)
    {
        Context = context;
        Secrets = context.Secrets;
        Http = new HostingHttp($"Codalus/{context.Plugin.Version}");
        Http.Authorize = AuthorizeAsync;
        context.Settings.Changed += (_, e) =>
        {
            if (e.Affects(SettingsSection))
            {
                _loaded = false; // another server: its session, if any
            }
        };
    }

    protected IPluginContext Context { get; }

    protected ISettings Settings => Context.Settings;

    protected ISecrets Secrets { get; }

    protected HostingHttp Http { get; }

    /// <summary>The settings section of the provider (<c>github</c>, <c>azureDevOps</c>…): a change there may mean another server.</summary>
    protected abstract string SettingsSection { get; }

    /// <summary>The host of the configured server (sessions belong to one).</summary>
    protected abstract string ServerHost { get; }

    /// <summary>Renews an expiring OAuth session; by default the user must sign in again.</summary>
    protected virtual Task<HostingSession> RefreshAsync(HostingSession session, CancellationToken cancellationToken) =>
        throw new HostingAuthenticationException("The session expired. Sign in again.");

    /// <summary>The stored session for the configured server, or null.</summary>
    protected async Task<HostingSession?> SessionAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return _session;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loaded)
            {
                var text = await Secrets.GetAsync(SessionKey, cancellationToken).ConfigureAwait(false);
                HostingSession? session = null;
                try
                {
                    session = text is null ? null : JsonSerializer.Deserialize(text, HostingSessionJsonContext.Default.HostingSession);
                }
                catch (JsonException)
                {
                }

                _session = session is not null && string.Equals(session.Host, ServerHost, StringComparison.OrdinalIgnoreCase) ? session : null;
                _loaded = true;
            }

            return _session;
        }
        finally
        {
            _gate.Release();
        }
    }

    protected async Task SaveSessionAsync(HostingSession session, CancellationToken cancellationToken)
    {
        await Secrets.StoreAsync(SessionKey, JsonSerializer.Serialize(session, HostingSessionJsonContext.Default.HostingSession), cancellationToken).ConfigureAwait(false);
        _session = session;
        _loaded = true;
    }

    protected static HostingAccount AccountOf(HostingSession session) =>
        new(session.Account, session.Host) { DisplayName = session.DisplayName, Url = session.Url };

    public override async Task<HostingAccount?> GetAccountAsync(CancellationToken cancellationToken) =>
        await SessionAsync(cancellationToken).ConfigureAwait(false) is { } session ? AccountOf(session) : null;

    public override async Task SignOutAsync(CancellationToken cancellationToken)
    {
        await Secrets.DeleteAsync(SessionKey, cancellationToken).ConfigureAwait(false);
        _session = null;
        _loaded = true;
    }

    /// <summary>The signed-in session (refreshed when it is about to expire); throws when there is none.</summary>
    protected async Task<HostingSession> RequireSessionAsync(CancellationToken cancellationToken)
    {
        var session = await SessionAsync(cancellationToken).ConfigureAwait(false) ?? throw new HostingAuthenticationException("Not signed in.");
        if (session.ExpiresAt is { } expires && expires - TimeSpan.FromMinutes(1) < DateTimeOffset.UtcNow)
        {
            if (session.RefreshToken is null)
            {
                throw new HostingAuthenticationException("The session expired. Sign in again.");
            }

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_session is { } current && current.ExpiresAt == session.ExpiresAt)
                {
                    var renewed = await RefreshAsync(session, cancellationToken).ConfigureAwait(false);
                    await Secrets.StoreAsync(SessionKey, JsonSerializer.Serialize(renewed, HostingSessionJsonContext.Default.HostingSession), cancellationToken).ConfigureAwait(false);
                    _session = renewed;
                }

                session = _session ?? session;
            }
            finally
            {
                _gate.Release();
            }
        }

        return session;
    }

    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is not null)
        {
            return; // set by the caller (checking a token before it is stored)
        }

        var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = Authorization(session.Scheme, session.AccessToken);
    }

    /// <summary>The Authorization header for a token.</summary>
    public static AuthenticationHeaderValue Authorization(string scheme, string token) =>
        scheme == "basic"
            ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + token)))
            : new AuthenticationHeaderValue("Bearer", token);

    /// <summary>An API call made with a given token (before it is stored as the session).</summary>
    protected Task<JsonElement> GetWithTokenAsync(string url, string scheme, string token, CancellationToken cancellationToken) =>
        Http.SendAsync(HttpMethod.Get, url, null, cancellationToken, new Dictionary<string, string> { ["Authorization"] = Authorization(scheme, token).ToString() });

    /// <summary>A setting's text, trimmed of a trailing slash; the fallback when empty.</summary>
    protected string Setting(string key, string fallback) =>
        Settings.GetValue(key, fallback) is { Length: > 0 } value ? value.Trim().TrimEnd('/') : fallback;

    public void Dispose()
    {
        Http.Dispose();
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }
}
