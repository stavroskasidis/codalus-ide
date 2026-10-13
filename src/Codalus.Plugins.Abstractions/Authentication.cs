namespace Codalus.Plugins;

/// <summary>A signed-in session of an authentication provider.</summary>
/// <param name="Id">The session's id within its provider.</param>
/// <param name="AccessToken">The token to call the service with.</param>
/// <param name="Account">Who is signed in, as the user knows the account.</param>
/// <param name="Scopes">What the token allows.</param>
public sealed record AuthenticationSession(string Id, string AccessToken, string Account, IReadOnlyList<string> Scopes);

/// <summary>
/// Signs users in to a service (API 1.0, M22a), for its own plugin and any other that asks
/// (<see cref="IAuthentication.GetSessionAsync"/>). Keep tokens in <see cref="IPluginContext.Secrets"/>.
/// </summary>
public interface IAuthenticationProvider
{
    /// <summary>The sessions there are, those with every scope of <paramref name="scopes"/> when given.</summary>
    Task<IReadOnlyList<AuthenticationSession>> GetSessionsAsync(IReadOnlyList<string>? scopes, CancellationToken cancellationToken);

    /// <summary>Signs the user in (a browser, a device code, a prompt) and returns the new session.</summary>
    Task<AuthenticationSession> CreateSessionAsync(IReadOnlyList<string> scopes, CancellationToken cancellationToken);

    /// <summary>Signs the session out and forgets its token.</summary>
    Task RemoveSessionAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>Authentication providers (API 1.0, M22a): offering one, and asking any for a session.</summary>
public interface IAuthentication
{
    /// <summary>
    /// Offers <paramref name="provider"/> as <paramref name="id"/> (the manifest's <c>contributes.authentication</c>)
    /// until disposed. The Accounts page lists its sessions.
    /// </summary>
    IDisposable RegisterProvider(string id, string label, IAuthenticationProvider provider);

    /// <summary>
    /// A session of provider <paramref name="providerId"/> with <paramref name="scopes"/>: an existing one, or, with
    /// <paramref name="createIfNone"/>, a new sign-in. Another plugin's provider asks the user's consent first. Null when
    /// there's none, or the user declined.
    /// </summary>
    Task<AuthenticationSession?> GetSessionAsync(string providerId, IReadOnlyList<string> scopes, bool createIfNone, CancellationToken cancellationToken);

    /// <summary>The provider's sessions changed (a sign-in, a sign-out): the Accounts page refreshes.</summary>
    void NotifySessionsChanged(string providerId);
}
