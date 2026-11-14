using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Codalus.Plugins.Proposed;

namespace Codalus.Plugins.Hosting;

/// <summary>
/// A signed-in session, kept with <see cref="ISecrets"/> (the OS credential store) as JSON. <see cref="Scheme"/> is
/// <c>bearer</c> (OAuth tokens, most personal access tokens), <c>basic</c> (Azure DevOps personal access tokens) or
/// <c>negotiate</c> (the operating system's account, with no token: <see cref="HostingProviderBase.IntegratedScheme"/>).
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

    /// <summary>When the user signed in (kept through refreshes; null for a session stored before it was recorded).</summary>
    public DateTimeOffset? SignedInAt { get; init; }
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
    /// <summary>The scheme of a session that signs in as the operating system's user (no token is stored).</summary>
    public const string IntegratedScheme = "negotiate";

    private const string SessionKey = "session";

    /// <summary>A request's option: the stored session authorized it (a 401 then may mean the session is gone).</summary>
    private static readonly HttpRequestOptionsKey<HostingSession> s_sessionRequest = new("codalus.session");
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
        Http.Refused = RefusedAsync;
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

    /// <summary>
    /// The API address that says who a token belongs to (GitHub's <c>/user</c>), or null. With one, a 401 on another
    /// request signs the user out only when this refuses the token too, and <see cref="ReusableAccountAsync"/> can keep a
    /// stored session the host still accepts instead of making a new token.
    /// </summary>
    protected virtual string? WhoAmIUrl => null;

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
        session = session with { SignedInAt = session.SignedInAt ?? DateTimeOffset.UtcNow };
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
                    renewed = renewed with { SignedInAt = renewed.SignedInAt ?? session.SignedInAt };
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
        if (request.Headers.Authorization is not null || request.Options.TryGetValue(HostingHttp.IntegratedHost, out _))
        {
            return; // set by the caller (checking a token, or the system's account, before it is stored)
        }

        var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
        if (session.Scheme == IntegratedScheme)
        {
            request.Options.Set(HostingHttp.IntegratedHost, session.Host);
            return;
        }

        request.Headers.Authorization = Authorization(session.Scheme, session.AccessToken);
        request.Options.Set(s_sessionRequest, session);
    }

    /// <summary>
    /// A 401 on a request the stored session authorized: with <see cref="WhoAmIUrl"/>, the token is asked about once more,
    /// and only a second refusal signs the user out (<see cref="HostingAuthenticationException"/>, saying the host's reason
    /// and how long ago the user signed in). A token the host still accepts, or one that couldn't be checked, keeps the
    /// session: the request fails as an ordinary error.
    /// </summary>
    private async Task<Exception?> RefusedAsync(HttpRequestMessage request, string reason, CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetValue(s_sessionRequest, out var session))
        {
            return null; // a token being checked, or a request without the session
        }

        var refused = $"{session.Host} refused the token of {session.Account}{SignedInAgo(session)} ({reason}).";
        if (WhoAmIUrl is not { } url)
        {
            return new HostingAuthenticationException(refused);
        }

        var what = $"{request.Method} {request.RequestUri?.AbsolutePath}: 401 {reason}";
        try
        {
            await GetWithTokenAsync(url, session.Scheme, session.AccessToken, cancellationToken).ConfigureAwait(false);
            return new HostingHttpException(401, $"{what} (the session is still valid: {session.Host} accepts the token of {session.Account}).");
        }
        catch (HostingAuthenticationException)
        {
            return new HostingAuthenticationException(refused);
        }
        catch (Exception ex) when (ex is HostingHttpException or HttpRequestException or JsonException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return new HostingHttpException(401, $"{what} (the session couldn't be checked, so it is kept: {ex.Message})");
        }
    }

    /// <summary>
    /// The stored session's account when the host still accepts its token (refreshed if it was expiring), so signing in
    /// again keeps it instead of making a new token: a host limits how many it keeps for a user and an app (GitHub
    /// revokes the oldest beyond ten). Null without <see cref="WhoAmIUrl"/>, a session, or a token the host accepts.
    /// </summary>
    protected async Task<HostingAccount?> ReusableAccountAsync(CancellationToken cancellationToken)
    {
        if (WhoAmIUrl is not { } url || await SessionAsync(cancellationToken).ConfigureAwait(false) is not { Scheme: not IntegratedScheme })
        {
            return null;
        }

        try
        {
            var session = await RequireSessionAsync(cancellationToken).ConfigureAwait(false);
            await GetWithTokenAsync(url, session.Scheme, session.AccessToken, cancellationToken).ConfigureAwait(false);
            return AccountOf(session);
        }
        catch (Exception ex) when (ex is HostingAuthenticationException or HostingHttpException or HttpRequestException or JsonException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>", signed in 3 days ago", or nothing when the session doesn't say when.</summary>
    internal static string SignedInAgo(HostingSession session)
    {
        if (session.SignedInAt is not { } at)
        {
            return string.Empty;
        }

        var age = DateTimeOffset.UtcNow - at;
        static string Count(double value, string unit) => ((int)value).ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + unit + ((int)value == 1 ? string.Empty : "s");
        return ", signed in " + (age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? Count(age.TotalMinutes, "minute") + " ago"
            : age.TotalDays < 1 ? Count(age.TotalHours, "hour") + " ago"
            : Count(age.TotalDays, "day") + " ago");
    }

    /// <summary>The Authorization header for a token.</summary>
    public static AuthenticationHeaderValue Authorization(string scheme, string token) =>
        scheme == "basic"
            ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + token)))
            : new AuthenticationHeaderValue("Bearer", token);

    /// <summary>An API call made with a given token (before it is stored as the session).</summary>
    protected Task<JsonElement> GetWithTokenAsync(string url, string scheme, string token, CancellationToken cancellationToken) =>
        Http.SendAsync(HttpMethod.Get, url, null, cancellationToken, new Dictionary<string, string> { ["Authorization"] = Authorization(scheme, token).ToString() });

    /// <summary>An API call made as the operating system's user, to the configured server only (before it is stored as the session).</summary>
    protected Task<JsonElement> GetIntegratedAsync(string url, CancellationToken cancellationToken) =>
        Http.SendAsync(HttpMethod.Get, url, null, cancellationToken, integratedHost: ServerHost);

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
