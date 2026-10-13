using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Http;

namespace Codalus.Shell.ViewModels;

/// <summary>An endpoint row (or a project header) in the Endpoints Explorer.</summary>
public sealed record EndpointRow(AspNetEndpoint? Endpoint, string? ProjectHeader)
{
    public bool IsHeader => ProjectHeader is not null;

    public string Method => Endpoint?.HttpMethod ?? string.Empty;

    public string Route => Endpoint?.Route ?? ProjectHeader ?? string.Empty;

    public string Handler => Endpoint?.Handler ?? string.Empty;

    public string AccessibleName => IsHeader ? $"Project {ProjectHeader}" : $"{Method} {Route}, handled by {Handler}";
}

/// <summary>
/// The Endpoints Explorer (M15a): the solution's ASP.NET Core endpoints from the C# language service, by project.
/// Double-click opens the handler; Generate Request appends a request to the project's <c>.http</c> file.
/// </summary>
public sealed class EndpointsExplorerViewModel : ObservableObject, IDisposable
{
    public const string ToolWindowId = "web.endpoints";

    private readonly Func<CancellationToken, Task<AspNetEndpointsResult?>> _load;
    private readonly Func<Location, Task> _open;
    private readonly Func<AspNetEndpoint, Task> _generate;
    private CancellationTokenSource? _cts;
    private string _filter = string.Empty;
    private string? _status = "Open a solution with ASP.NET Core projects.";
    private AspNetEndpoint[] _endpoints = [];

    public EndpointsExplorerViewModel(Func<CancellationToken, Task<AspNetEndpointsResult?>> load, Func<Location, Task> open, Func<AspNetEndpoint, Task> generate)
    {
        _load = load;
        _open = open;
        _generate = generate;
    }

    public ObservableCollection<EndpointRow> Rows { get; } = [];

    public IReadOnlyList<AspNetEndpoint> Endpoints => _endpoints;

    /// <summary>Shown instead of the list (loading, nothing found).</summary>
    public string? Status
    {
        get => _status;
        private set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => _status is not null;

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value ?? string.Empty))
            {
                Fill();
            }
        }
    }

    public async Task RefreshAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Status = Rows.Count == 0 ? "Finding endpoints…" : Status;
        try
        {
            var result = await _load(token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            _endpoints = result?.Endpoints ?? [];
            Fill();
        }
        catch (Exception ex) when (ex is OperationCanceledException or Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or InvalidOperationException)
        {
            if (!token.IsCancellationRequested)
            {
                Status = "The endpoints could not be found: " + ex.Message;
            }
        }
    }

    private void Fill()
    {
        Rows.Clear();
        var filter = _filter.Trim();
        foreach (var project in _endpoints
                     .Where(e => filter.Length == 0 || e.Route.Contains(filter, StringComparison.OrdinalIgnoreCase) || e.Handler.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                 || e.HttpMethod.Contains(filter, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(e => e.Project))
        {
            Rows.Add(new EndpointRow(null, project.Key));
            foreach (var endpoint in project)
            {
                Rows.Add(new EndpointRow(endpoint, null));
            }
        }

        Status = _endpoints.Length == 0 ? "No ASP.NET Core endpoints in the solution." : Rows.Count == 0 ? "No endpoint matches the search." : null;
    }

    public Task OpenAsync(EndpointRow row) => row.Endpoint is { } endpoint ? _open(endpoint.Location) : Task.CompletedTask;

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    public Task GenerateRequestAsync(EndpointRow row) => row.Endpoint is { } endpoint ? _generate(endpoint) : Task.CompletedTask;
}

/// <summary>A history row of the HTTP Response window.</summary>
public sealed record HttpHistoryRow(HttpExchange Exchange)
{
    public string Status => Exchange.Response is { } response ? response.StatusCode.ToString(CultureInfo.InvariantCulture) : "Error";

    public bool IsSuccess => Exchange.Response?.IsSuccess == true;

    public string Request => $"{Exchange.Request.Method} {Exchange.Request.Url}";

    public string Time => $"{Exchange.Elapsed.TotalMilliseconds:0} ms";

    public string AccessibleName => $"{Request}: {Status}, {Time}";
}

/// <summary>
/// The HTTP Response window (M15a): the last response of a <c>.http</c> request (status, timing, headers, the body
/// formatted by content type), and the history of requests.
/// </summary>
public sealed class HttpResponseViewModel : ObservableObject
{
    public const string ToolWindowId = "http.response";

    private readonly HttpService _http;
    private HttpHistoryRow? _selected;

    public HttpResponseViewModel(HttpService http, Infrastructure.EditorFont font, Editor.IEditorServices services)
    {
        _http = http;
        Font = font;
        Services = services;
        http.Completed += (_, exchange) => Ui.Post(() => Refresh(exchange));
        Refresh(null);
    }

    public Infrastructure.EditorFont Font { get; }

    public Editor.IEditorServices Services { get; }

    public ObservableCollection<HttpHistoryRow> History { get; } = [];

    public HttpHistoryRow? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                OnPropertyChanged(string.Empty);
                BodyChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The body to show changed (the view's code viewer follows).</summary>
    public event EventHandler? BodyChanged;

    public bool HasExchange => _selected is not null;

    public string Summary => _selected?.Exchange is not { } exchange
        ? "Send a request from a .http file (the ▶ next to a request, or Ctrl+Alt+R)."
        : exchange.Response is { } response
            ? $"{response.Version} {response.StatusCode.ToString(CultureInfo.InvariantCulture)} {response.ReasonPhrase} · {exchange.Elapsed.TotalMilliseconds:0} ms · {FormatSize(response.Size)}"
            : "Error · " + exchange.Error;

    public bool IsError => _selected is { Exchange.Response: null } or { Exchange.Response.IsSuccess: false };

    public string Headers => _selected?.Exchange is not { } exchange
        ? string.Empty
        : string.Join("\n", (exchange.Response?.Headers ?? []).Select(h => $"{h.Key}: {h.Value}"));

    /// <summary>The request as sent (variables replaced).</summary>
    public string RequestText
    {
        get
        {
            if (_selected?.Exchange is not { } exchange)
            {
                return string.Empty;
            }

            var text = new StringBuilder($"{exchange.Request.Method} {exchange.Request.Url}\n");
            foreach (var (name, value) in exchange.Request.Headers)
            {
                text.Append(name).Append(": ").Append(value).Append('\n');
            }

            if (exchange.Request.Body is { } body)
            {
                text.Append('\n').Append(body);
            }

            return text.ToString();
        }
    }

    /// <summary>The body, formatted by its content type, and the language that colors it.</summary>
    public (string Text, string? LanguageId) Body
    {
        get
        {
            if (_selected?.Exchange.Response is not { } response)
            {
                return (_selected?.Exchange.Error ?? string.Empty, null);
            }

            var type = response.ContentType ?? string.Empty;
            if (type.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                return (Visualizers.Format(VisualizerKind.Json, response.Body), "json");
            }

            if (type.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                return (response.Body, "html");
            }

            return type.Contains("xml", StringComparison.OrdinalIgnoreCase)
                ? (Visualizers.Format(VisualizerKind.Xml, response.Body), "xml")
                : (response.Body, null);
        }
    }

    private void Refresh(HttpExchange? latest)
    {
        History.Clear();
        foreach (var exchange in _http.History)
        {
            History.Add(new HttpHistoryRow(exchange));
        }

        Selected = latest is null ? History.FirstOrDefault() : History.FirstOrDefault(r => ReferenceEquals(r.Exchange, latest)) ?? History.FirstOrDefault();
    }

    public void ClearHistory() => _http.ClearHistory();

    private static string FormatSize(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.0} KB" : $"{bytes / (1024.0 * 1024):0.0} MB";
}
