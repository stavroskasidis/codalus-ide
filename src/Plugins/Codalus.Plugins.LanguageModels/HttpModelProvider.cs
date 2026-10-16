using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Codalus.Plugins.Proposed;

namespace Codalus.Plugins.LanguageModels;

/// <summary>One server-sent event: its <c>event:</c> name (null when none) and its <c>data:</c> lines joined.</summary>
public sealed record ServerSentEvent(string? Event, string Data);

/// <summary>
/// A provider that talks HTTP to a model service (ADR 0062): the endpoint from a setting, the key from the plugin's
/// secrets (asked for by Configure), streamed requests read as server-sent events, and the service's errors as
/// <see cref="LanguageModelException"/>s the user can act on.
/// </summary>
public abstract class HttpModelProvider : LanguageModelProvider, IDisposable
{
    private readonly HttpClient _client;

    /// <param name="id">The manifest's provider id.</param>
    /// <param name="context">The plugin's context (settings, secrets, prompts).</param>
    /// <param name="label">The service's name in prompts and messages.</param>
    /// <param name="endpointSetting">The setting holding the endpoint's base URL.</param>
    /// <param name="defaultEndpoint">The endpoint when the setting is empty.</param>
    /// <param name="key">Whether the service takes a key: required, optional (local servers), or none.</param>
    protected HttpModelProvider(string id, IPluginContext context, string label, string endpointSetting, string defaultEndpoint, KeyUse key)
        : base(id)
    {
        Context = context;
        Label = label;
        EndpointSetting = endpointSetting;
        DefaultEndpoint = defaultEndpoint;
        Key = key;
        _client = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AutomaticDecompression = DecompressionMethods.All })
        {
            // Long answers stream for minutes; cancellation, not a timeout, ends them.
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Codalus-IDE");
    }

    protected IPluginContext Context { get; }

    protected string Label { get; }

    protected string EndpointSetting { get; }

    protected string DefaultEndpoint { get; }

    protected KeyUse Key { get; }

    /// <summary>The endpoint's base URL, without a trailing slash.</summary>
    protected string Endpoint => (Context.Settings.GetValue(EndpointSetting, "") is { Length: > 0 } url ? url : DefaultEndpoint).Trim().TrimEnd('/');

    private string SecretKey => "apiKey:" + Id;

    /// <summary>The stored key, or null.</summary>
    protected async Task<string?> GetKeyAsync(CancellationToken cancellationToken) =>
        Key == KeyUse.None ? null : await Context.Secrets.GetAsync(SecretKey, cancellationToken).ConfigureAwait(false) is { Length: > 0 } key ? key : null;

    public override async Task<LanguageModelStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (Key == KeyUse.Required && await GetKeyAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return new LanguageModelStatus(false, $"No {Label} key is stored: Configure to enter one.");
        }

        return new LanguageModelStatus(true, Endpoint);
    }

    public override async Task<LanguageModelStatus> ConfigureAsync(CancellationToken cancellationToken)
    {
        if (Key == KeyUse.None)
        {
            return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }

        var stored = await GetKeyAsync(cancellationToken).ConfigureAwait(false) is not null;
        var prompt = Key == KeyUse.Required
            ? $"Kept in the OS credential store and sent only to {Endpoint}."
            : $"Kept in the OS credential store and sent only to {Endpoint}. Leave it empty for a server that takes none.";
        var entered = await Context.Window.ShowInputBoxAsync(
            new InputBoxOptions($"{Label} key")
            {
                Prompt = stored ? prompt + " A key is stored; a new one replaces it." : prompt,
            },
            cancellationToken).ConfigureAwait(false);
        if (entered is null)
        {
            return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }

        if (entered.Trim().Length == 0)
        {
            await Context.Secrets.DeleteAsync(SecretKey, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Context.Secrets.StoreAsync(SecretKey, entered.Trim(), cancellationToken).ConfigureAwait(false);
        }

        return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds the key to a request in the service's way.</summary>
    protected abstract void Authorize(HttpRequestMessage request, string key);

    /// <summary>The message of the service's JSON error body, when it has one.</summary>
    protected virtual string? ErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString();
                }

                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        return body.Length is > 0 and < 300 ? body.Trim() : null;
    }

    /// <summary>Sends a request with the key; the service's errors become <see cref="LanguageModelException"/>s.</summary>
    protected async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json, CancellationToken cancellationToken, bool stream = false)
    {
        var key = await GetKeyAsync(cancellationToken).ConfigureAwait(false);
        if (Key == KeyUse.Required && key is null)
        {
            throw new LanguageModelException($"No {Label} key is stored: Configure the provider to enter one.") { NeedsConfiguration = true };
        }

        using var request = new HttpRequestMessage(method, Endpoint + path);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (key is not null)
        {
            Authorize(request, key);
        }

        AddHeaders(request);
        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new LanguageModelException($"{Label} can't be reached at {Endpoint}: {ex.Message}", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var message = ErrorMessage(body) ?? response.ReasonPhrase ?? "error";
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new LanguageModelException($"{Label} refused the key ({(int)response.StatusCode} {message}). Configure the provider with a valid key.") { NeedsConfiguration = true };
            }

            throw new LanguageModelException($"{Label}: {(int)response.StatusCode} {message}");
        }
    }

    /// <summary>Headers every request sends (an API version).</summary>
    protected virtual void AddHeaders(HttpRequestMessage request)
    {
    }

    /// <summary>Sends a request and parses the JSON answer.</summary>
    protected async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new LanguageModelException($"{Label} answered with something that isn't JSON.", ex);
        }
    }

    /// <summary>The server-sent events of a streamed response, as they arrive.</summary>
    protected static async IAsyncEnumerable<ServerSentEvent> ReadEventsAsync(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? name = null;
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return new ServerSentEvent(name, data.ToString());
                }

                name = null;
                data.Clear();
                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            if (field == "event")
            {
                name = value;
            }
            else if (field == "data")
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(value);
            }
        }

        if (data.Length > 0)
        {
            yield return new ServerSentEvent(name, data.ToString());
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Whether a service takes a key.</summary>
public enum KeyUse
{
    /// <summary>Requests need one.</summary>
    Required,

    /// <summary>Some servers take one (a local OpenAI-compatible server may not).</summary>
    Optional,

    /// <summary>Never (Ollama).</summary>
    None,
}
