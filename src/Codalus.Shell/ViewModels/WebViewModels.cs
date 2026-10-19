using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Http;
using Codalus.Workbench.Localization;

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

/// <summary>
/// How a status reads as a chip (M34h, the control set): success for 2xx, a warning for 3xx, an error for 4xx, 5xx and
/// a request that failed, information for 1xx.
/// </summary>
public enum HttpStatusKind
{
    Info,
    Success,
    Redirect,
    Error,
}

/// <summary>A history row of the HTTP Response window: the method and status as chips, the path, when and how long.</summary>
public sealed record HttpHistoryRow(HttpExchange Exchange)
{
    public string Status => Exchange.Response is { } response ? response.StatusCode.ToString(CultureInfo.InvariantCulture) : "Error";

    public bool IsSuccess => Exchange.Response?.IsSuccess == true;

    public string Request => $"{Exchange.Request.Method} {Exchange.Request.Url}";

    public string Time => $"{Exchange.Elapsed.TotalMilliseconds:0} ms";

    public string AccessibleName => $"{Request}: {StatusText}, {Time}";

    /// <summary>The status chip's text: <c>200 OK</c>, <c>404 Not Found</c>, or <c>Error</c>.</summary>
    public string StatusText => HttpResponseViewModel.StatusTextOf(Exchange);

    public HttpStatusKind StatusKind => HttpResponseViewModel.StatusKindOf(Exchange);

    public bool IsStatusSuccess => StatusKind == HttpStatusKind.Success;

    public bool IsStatusRedirect => StatusKind == HttpStatusKind.Redirect;

    public bool IsStatusError => StatusKind == HttpStatusKind.Error;

    public bool IsStatusInfo => StatusKind == HttpStatusKind.Info;

    public string Method => Exchange.Request.Method;

    /// <summary>The method chip: POST in the success color, DELETE in the error color, the others neutral blue.</summary>
    public bool IsMethodCreate => Method is "POST" or "PUT";

    public bool IsMethodDelete => Method == "DELETE";

    public bool IsMethodOther => !IsMethodCreate && !IsMethodDelete;

    /// <summary>The URL's path and query (<c>/api/todos/42</c>), the whole URL when it isn't one.</summary>
    public string Path => Uri.TryCreate(Exchange.Request.Url, UriKind.Absolute, out var uri) ? uri.PathAndQuery : Exchange.Request.Url;

    /// <summary>When it was sent, in local time (<c>14:32:08</c>).</summary>
    public string At => Exchange.When.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The request's name (<c># @name create</c>), else why it failed, else its host.</summary>
    public string Note => Exchange.Name is { Length: > 0 } name ? name
        : Exchange.Response is null && Exchange.Error is { Length: > 0 } error ? error
        : Uri.TryCreate(Exchange.Request.Url, UriKind.Absolute, out var uri) ? uri.Authority : string.Empty;
}

/// <summary>A response header (or a request's), for the Headers tab's grid.</summary>
public sealed record HttpHeaderRow(string Name, string Value);

/// <summary>
/// The HTTP Response window (M15a; redesigned in M34h): the selected exchange's status as a chip with its version,
/// time and size, its request line with a link to its <c>.http</c> file, the body (Pretty or Raw), the headers and the
/// request as tabs, and the history of requests.
/// </summary>
public sealed class HttpResponseViewModel : ObservableObject
{
    public const string ToolWindowId = "http.response";

    private readonly HttpService _http;
    private HttpHistoryRow? _selected;
    private int _tabIndex;
    private int _bodyViewIndex;

    public HttpResponseViewModel(HttpService http, Infrastructure.EditorFont font, Editor.IEditorServices services)
    {
        _http = http;
        Font = font;
        Services = services;
        SendAgainCommand = new RelayCommand(() => _ = SendAgainAsync(), () => _selected is not null && SendAgain is not null);
        OpenRequestCommand = new RelayCommand(() => _ = OpenRequestAsync(), () => _selected is not null && OpenRequest is not null);
        SaveBodyCommand = new RelayCommand(() => _ = SaveBodyAsync(), () => HasBody && SaveBody is not null);
        OpenBodyCommand = new RelayCommand(() => _ = OpenBodyAsync(), () => HasBody && OpenBody is not null);
        ClearHistoryCommand = new RelayCommand(ClearHistory);
        http.Completed += (_, exchange) => Ui.Post(() => Refresh(exchange));
        Refresh(null);
    }

    public Infrastructure.EditorFont Font { get; }

    public Editor.IEditorServices Services { get; }

    /// <summary>Sends the selected exchange's request again from its .http file (the shell's).</summary>
    public Func<HttpExchange, Task>? SendAgain
    {
        get;
        set
        {
            field = value;
            SendAgainCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Opens a .http file at a request's line (1-based; the shell's).</summary>
    public Func<string, int, Task>? OpenRequest
    {
        get;
        set
        {
            field = value;
            OpenRequestCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Saves a body to a file the user picks: a suggested name and the text (the shell's).</summary>
    public Func<string, string, Task>? SaveBody
    {
        get;
        set
        {
            field = value;
            SaveBodyCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Opens a body in an editor: a name and the text (the shell's).</summary>
    public Func<string, string, Task>? OpenBody
    {
        get;
        set
        {
            field = value;
            OpenBodyCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand SendAgainCommand { get; }

    public RelayCommand OpenRequestCommand { get; }

    public RelayCommand SaveBodyCommand { get; }

    public RelayCommand OpenBodyCommand { get; }

    public RelayCommand ClearHistoryCommand { get; }

    public ObservableCollection<HttpHistoryRow> History { get; } = [];

    public bool HasHistory => History.Count > 0;

    public HttpHistoryRow? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                OnPropertyChanged(string.Empty);
                SendAgainCommand.RaiseCanExecuteChanged();
                OpenRequestCommand.RaiseCanExecuteChanged();
                SaveBodyCommand.RaiseCanExecuteChanged();
                OpenBodyCommand.RaiseCanExecuteChanged();
                BodyChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The body to show changed (the view's code viewer follows).</summary>
    public event EventHandler? BodyChanged;

    public bool HasExchange => _selected is not null;

    public bool HasNoExchange => _selected is null;

    public string Summary => _selected?.Exchange is not { } exchange
        ? "Send a request from a .http file (the ▶ next to a request, or Ctrl+Alt+R)."
        : exchange.Response is { } response
            ? $"{response.Version} {response.StatusCode.ToString(CultureInfo.InvariantCulture)} {response.ReasonPhrase} · {exchange.Elapsed.TotalMilliseconds:0} ms · {FormatSize(response.Size)}"
            : "Error · " + exchange.Error;

    public bool IsError => _selected is { Exchange.Response: null } or { Exchange.Response.IsSuccess: false };

    // ------------------------------------------------------------------ the status line (M34h)

    /// <summary>The status chip: <c>200 OK</c>, <c>404 Not Found</c>, <c>Error</c>.</summary>
    public string StatusText => _selected is { } row ? row.StatusText : string.Empty;

    public bool IsStatusSuccess => _selected?.StatusKind == HttpStatusKind.Success;

    public bool IsStatusRedirect => _selected?.StatusKind == HttpStatusKind.Redirect;

    public bool IsStatusError => _selected?.StatusKind == HttpStatusKind.Error;

    public bool IsStatusInfo => _selected?.StatusKind == HttpStatusKind.Info;

    public bool HasResponse => _selected?.Exchange.Response is not null;

    /// <summary>The protocol version: <c>HTTP/1.1</c>.</summary>
    public string Version => _selected?.Exchange.Response?.Version ?? string.Empty;

    /// <summary>How long the request took: <c>38 ms</c>.</summary>
    public string Elapsed => _selected is { } row ? row.Time : string.Empty;

    /// <summary>The body's size as received: <c>178 B</c>.</summary>
    public string Size => _selected?.Exchange.Response is { } response ? FormatSize(response.Size) : string.Empty;

    /// <summary>Why the request failed (no response), or null.</summary>
    public string? Error => _selected?.Exchange is { Response: null } exchange ? exchange.Error : null;

    public string Method => _selected?.Exchange.Request.Method ?? string.Empty;

    public bool IsMethodCreate => _selected?.IsMethodCreate == true;

    public bool IsMethodDelete => _selected?.IsMethodDelete == true;

    public bool IsMethodOther => _selected?.IsMethodOther == true;

    public string Url => _selected?.Exchange.Request.Url ?? string.Empty;

    /// <summary>The link to the request in its file: <c>WebApi.http, line 9</c>.</summary>
    public string Source => _selected?.Exchange is { File.Length: > 0 } exchange
        ? Localizer.Format("msg.httpResponse.fileLine", "{0}, line {1}", System.IO.Path.GetFileName(exchange.File), exchange.Line)
        : string.Empty;

    public bool HasSource => Source.Length > 0;

    /// <summary>The response's content type, shown beside the body's view switch.</summary>
    public string ContentType => _selected?.Exchange.Response?.ContentType ?? string.Empty;

    // ------------------------------------------------------------------ tabs

    /// <summary>0: Body; 1: Headers; 2: Request.</summary>
    public int TabIndex
    {
        get => _tabIndex;
        set
        {
            if (SetField(ref _tabIndex, Math.Clamp(value, 0, 2)))
            {
                OnPropertyChanged(nameof(IsBodyTab));
                OnPropertyChanged(nameof(IsHeadersTab));
                OnPropertyChanged(nameof(IsRequestTab));
            }
        }
    }

    public bool IsBodyTab => _tabIndex == 0;

    public bool IsHeadersTab => _tabIndex == 1;

    public bool IsRequestTab => _tabIndex == 2;

    /// <summary>The body as formatted (0, Pretty) or as received (1, Raw).</summary>
    public int BodyViewIndex
    {
        get => _bodyViewIndex;
        set
        {
            if (SetField(ref _bodyViewIndex, value == 1 ? 1 : 0))
            {
                BodyChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool HasBody => _selected?.Exchange.Response is { Body.Length: > 0 };

    public string Headers => _selected?.Exchange is not { } exchange
        ? string.Empty
        : string.Join("\n", (exchange.Response?.Headers ?? []).Select(h => $"{h.Key}: {h.Value}"));

    /// <summary>The response's headers, for the Headers tab's grid.</summary>
    public IReadOnlyList<HttpHeaderRow> HeaderRows => [.. (_selected?.Exchange.Response?.Headers ?? []).Select(h => new HttpHeaderRow(h.Key, h.Value))];

    /// <summary>How many headers the response has (the Headers tab's badge).</summary>
    public int HeaderCount => _selected?.Exchange.Response?.Headers.Count ?? 0;

    public bool HasHeaders => HeaderCount > 0;

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

    /// <summary>The body, formatted by its content type (or as received with Raw), and the language that colors it.</summary>
    public (string Text, string? LanguageId) Body
    {
        get
        {
            if (_selected?.Exchange.Response is not { } response)
            {
                return (_selected?.Exchange.Error ?? string.Empty, null);
            }

            var type = response.ContentType ?? string.Empty;
            var raw = _bodyViewIndex == 1;
            if (type.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                return (raw ? response.Body : Visualizers.Format(VisualizerKind.Json, response.Body), "json");
            }

            if (type.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                return (response.Body, "html");
            }

            return type.Contains("xml", StringComparison.OrdinalIgnoreCase)
                ? (raw ? response.Body : Visualizers.Format(VisualizerKind.Xml, response.Body), "xml")
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

        OnPropertyChanged(nameof(HasHistory));
        Selected = latest is null ? History.FirstOrDefault() : History.FirstOrDefault(r => ReferenceEquals(r.Exchange, latest)) ?? History.FirstOrDefault();
    }

    public void ClearHistory() => _http.ClearHistory();

    private Task SendAgainAsync() => _selected is { } row && SendAgain is { } send ? send(row.Exchange) : Task.CompletedTask;

    private Task OpenRequestAsync() => _selected?.Exchange is { File.Length: > 0 } exchange && OpenRequest is { } open ? open(exchange.File, exchange.Line) : Task.CompletedTask;

    private Task SaveBodyAsync() => HasBody && SaveBody is { } save ? save(BodyFileName(), Body.Text) : Task.CompletedTask;

    private Task OpenBodyAsync() => HasBody && OpenBody is { } open ? open(BodyFileName(), Body.Text) : Task.CompletedTask;

    /// <summary>A name for the body's file, by its language: <c>response.json</c>.</summary>
    public string BodyFileName() => "response." + (Body.LanguageId switch
    {
        "json" => "json",
        "xml" => "xml",
        "html" => "html",
        _ => "txt",
    });

    internal static string StatusTextOf(HttpExchange exchange) => exchange.Response is { } response
        ? $"{response.StatusCode.ToString(CultureInfo.InvariantCulture)} {response.ReasonPhrase}".TrimEnd()
        : "Error";

    internal static HttpStatusKind StatusKindOf(HttpExchange exchange) => exchange.Response?.StatusCode switch
    {
        null => HttpStatusKind.Error,
        < 200 => HttpStatusKind.Info,
        < 300 => HttpStatusKind.Success,
        < 400 => HttpStatusKind.Redirect,
        _ => HttpStatusKind.Error,
    };

    private static string FormatSize(long bytes) => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.0} KB" : $"{bytes / (1024.0 * 1024):0.0} MB";
}
