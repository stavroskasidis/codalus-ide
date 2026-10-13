using Codalus.Plugins.Proposed;

namespace Codalus.Plugins.Hosting;

/// <summary>An OAuth app's device-flow endpoints (RFC 8628).</summary>
/// <param name="DeviceCodeUrl">Where to ask for a device code.</param>
/// <param name="TokenUrl">Where to poll for the token (and refresh it).</param>
/// <param name="ClientId">The OAuth app's client id.</param>
/// <param name="Scope">The scopes asked for, space-separated.</param>
public sealed record DeviceFlowEndpoints(string DeviceCodeUrl, string TokenUrl, string ClientId, string Scope);

/// <summary>Tokens from a sign-in or a refresh.</summary>
public sealed record OAuthToken(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt);

/// <summary>
/// The OAuth 2.0 device authorization grant (RFC 8628), as GitHub, GitLab and Microsoft Entra ID implement it: ask for a
/// code, show it, then poll the token endpoint at the host's interval (slower after <c>slow_down</c>) until the user
/// approves, declines or the code expires.
/// </summary>
public static class DeviceFlow
{
    public const string GrantType = "urn:ietf:params:oauth:grant-type:device_code";

    /// <summary>The shortest wait between polls, whatever a host (or a mock server) asks for.</summary>
    public static TimeSpan MinimumInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    public static async Task<OAuthToken> SignInAsync(HostingHttp http, DeviceFlowEndpoints endpoints, IDeviceCodePrompt prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpoints.ClientId))
        {
            throw new InvalidOperationException("Device sign-in needs the OAuth app's client id (a setting); sign in with a personal access token instead.");
        }

        var (status, code) = await http.PostFormAsync(
            endpoints.DeviceCodeUrl,
            new Dictionary<string, string> { ["client_id"] = endpoints.ClientId, ["scope"] = endpoints.Scope },
            cancellationToken).ConfigureAwait(false);
        if (code.Str("device_code") is not { } deviceCode || code.Str("user_code") is not { } userCode
            || (code.Str("verification_uri") ?? code.Str("verification_url")) is not { } verificationUri)
        {
            throw new HostingHttpException(status, "The host didn't start the sign-in: " + (code.Str("error_description") ?? code.Str("error") ?? code.Str("message") ?? $"status {status}"));
        }

        var expiresAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(code.Long("expires_in") ?? 900);
        var interval = TimeSpan.FromSeconds(code.Long("interval") ?? 5);
        await prompt.ShowAsync(
            new DeviceCode(userCode, verificationUri, expiresAt) { VerificationUriComplete = code.Str("verification_uri_complete"), Message = code.Str("message") },
            cancellationToken).ConfigureAwait(false);

        var form = new Dictionary<string, string> { ["grant_type"] = GrantType, ["device_code"] = deviceCode, ["client_id"] = endpoints.ClientId };
        while (true)
        {
            await Task.Delay(interval < MinimumInterval ? MinimumInterval : interval, cancellationToken).ConfigureAwait(false);
            var (tokenStatus, token) = await http.PostFormAsync(endpoints.TokenUrl, form, cancellationToken).ConfigureAwait(false);
            if (token.Str("access_token") is { } accessToken)
            {
                return ToToken(token, accessToken);
            }

            switch (token.Str("error"))
            {
                case "authorization_pending":
                    break;
                case "slow_down":
                    interval = token.Long("interval") is { } longer ? TimeSpan.FromSeconds(longer) : interval + TimeSpan.FromSeconds(5);
                    break;
                case "expired_token" or "code_expired":
                    throw new HostingAuthenticationException("The code expired before it was entered. Sign in again.");
                case "access_denied" or "authorization_declined":
                    throw new HostingAuthenticationException("The sign-in was declined.");
                default:
                    throw new HostingHttpException(tokenStatus, "Sign-in failed: " + (token.Str("error_description") ?? token.Str("error") ?? $"status {tokenStatus}"));
            }

            if (DateTimeOffset.UtcNow > expiresAt)
            {
                throw new HostingAuthenticationException("The code expired before it was entered. Sign in again.");
            }
        }
    }

    /// <summary>Refreshes tokens (<c>grant_type=refresh_token</c> plus the host's own fields).</summary>
    public static async Task<OAuthToken> RefreshAsync(HostingHttp http, string tokenUrl, IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken)
    {
        var (status, token) = await http.PostFormAsync(tokenUrl, form, cancellationToken).ConfigureAwait(false);
        return token.Str("access_token") is { } accessToken
            ? ToToken(token, accessToken)
            : throw new HostingAuthenticationException($"The session couldn't be renewed ({token.Str("error_description") ?? token.Str("error") ?? $"status {status}"}). Sign in again.");
    }

    private static OAuthToken ToToken(System.Text.Json.JsonElement token, string accessToken) =>
        new(accessToken, token.Str("refresh_token"), token.Long("expires_in") is { } seconds ? DateTimeOffset.UtcNow + TimeSpan.FromSeconds(seconds) : null);
}
