using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Wcf;

namespace Codalus.Shell.ViewModels;

/// <summary>
/// A row of the request form: one field of the message, indented by its depth. A list wrapper (a data contract's
/// <c>ArrayOfstring</c>: one element whose only child repeats) is one row with its items under it (M34h, the design's
/// typed grid: <c>Tags  string[]  2 items  +</c>); the items are named by their index.
/// </summary>
public sealed class SoapFieldRow : ObservableObject
{
    private readonly Action<SoapFieldRow> _changed;
    private readonly Action<SoapFieldRow> _add;
    private readonly Action<SoapFieldRow>? _remove;

    public SoapFieldRow(SoapField field, int depth, Action<SoapFieldRow> changed, Action<SoapFieldRow> add, Action<SoapFieldRow>? remove)
    {
        Field = field;
        Depth = depth;
        _changed = changed;
        _add = add;
        _remove = remove;
        AddCommand = new RelayCommand(() => _add(this));
        RemoveCommand = new RelayCommand(() => _remove?.Invoke(this));
        ToggleCommand = new RelayCommand(() => Toggle?.Invoke(this));
    }

    public SoapField Field { get; }

    /// <summary>The repeated element of a list wrapper shown as this one row (its items are this row's children), or null.</summary>
    public SoapField? Items { get; init; }

    /// <summary>The field items are added to: the list's repeated element, or the field itself when it repeats.</summary>
    public SoapField ItemsField => Items ?? Field;

    /// <summary>An item of a list: its index (the row is named <c>[0]</c>, <c>[1]</c>…), or -1.</summary>
    public int Index { get; init; } = -1;

    public int Depth { get; }

    public Thickness Indent => new(Depth * 18, 0, 0, 0);

    public string Name => Field.Name.LocalName;

    /// <summary>The name shown: the field's, or an item's index.</summary>
    public string Label => Index >= 0 ? $"[{Index.ToString(System.Globalization.CultureInfo.InvariantCulture)}]" : Name;

    /// <summary>The schema type shown: <c>int</c>, <c>Person</c>, <c>string[]</c> (a list's items' type).</summary>
    public string Type => Items?.TypeLabel ?? Field.TypeLabel;

    /// <summary>
    /// A value is expected (the red mark after the type): the schema requires the field, or it is a simple value that
    /// can't be nil (a data contract's <c>int</c> or enum). Not on a list's items.
    /// </summary>
    public bool IsRequired => Index < 0 && (!Field.Optional || (Field.Kind is SoapFieldKind.Simple or SoapFieldKind.Choice && !Field.Nillable));

    public bool IsText => Field.Kind == SoapFieldKind.Simple;

    public bool IsChoice => Field.Kind == SoapFieldKind.Choice;

    public bool IsGroup => Field.Kind is SoapFieldKind.Complex or SoapFieldKind.Repeated;

    /// <summary>A group's summary in the Value column: <c>Person: 4 members</c>, <c>2 items</c>.</summary>
    public string Summary => !IsGroup ? string.Empty
        : Items is not null || Field.Kind == SoapFieldKind.Repeated
            ? ItemsField.Children.Count == 1 ? Localizer.Get("msg.wcf.oneItem", "1 item") : Localizer.Format("msg.wcf.items", "{0} items", ItemsField.Children.Count)
        : Field.Truncated ? Localizer.Get("msg.wcf.nestedTooDeep", "(nested too deep: edit the XML)")
        : Field.Children.Count == 1 ? Localizer.Format("msg.wcf.oneMember", "{0}: 1 member", Type) : Localizer.Format("msg.wcf.members", "{0}: {1} members", Type, Field.Children.Count);

    /// <summary>A group with something under it collapses and expands with its chevron.</summary>
    public bool HasChevron => IsGroup && (Items ?? Field).Children.Count > 0;

    public bool IsExpanded { get; init; } = true;

    public string Chevron => IsExpanded ? "chevron-down" : "chevron-right";

    /// <summary>Collapses or expands the group (the view model's).</summary>
    public Action<SoapFieldRow>? Toggle { get; init; }

    public ICommand ToggleCommand { get; }

    public bool CanAdd => ItemsField.CanAddItem;

    public bool CanRemove => _remove is not null;

    public bool CanNil => Field.Nillable;

    public IReadOnlyList<string> Choices => Field.Choices;

    public string Placeholder => Field.Truncated ? Localizer.Get("msg.wcf.nestedTooDeep", "(nested too deep: edit the XML)") : Field.BuiltIn ?? string.Empty;

    /// <summary>The field's name and type, for its trimmed name (M34g).</summary>
    public string ToolTip => IsRequired ? Localizer.Format("msg.wcf.requiredField", "{0}: {1} (required)", Label, Type) : $"{Label}: {Type}";

    /// <summary>The chevron's name and tooltip: what it does to the group (<c>Collapse Tags</c>, <c>Expand Tags</c>).</summary>
    public string ToggleName => IsExpanded ? Localizer.Format("msg.wcf.collapseField", "Collapse {0}", Label) : Localizer.Format("msg.wcf.expandField", "Expand {0}", Label);

    /// <summary>The value box's and the nil box's names for screen readers.</summary>
    public string ValueName => Index >= 0 ? $"{Name} {Label}" : Label;

    public string NilName => Localizer.Format("msg.wcf.sendNil", "{0}: send xsi:nil", ValueName);

    public string? Value
    {
        get => Field.Value;
        set
        {
            if (Field.Value != value)
            {
                Field.Value = value;
                OnPropertyChanged();
                _changed(this);
            }
        }
    }

    public bool IsNil
    {
        get => Field.IsNil;
        set
        {
            if (Field.IsNil != value)
            {
                Field.IsNil = value;
                OnPropertyChanged();
                _changed(this);
            }
        }
    }

    public ICommand AddCommand { get; }

    public ICommand RemoveCommand { get; }
}

/// <summary>An operation in the test client's list, with its signature: <c>Greet(Person person) → string</c>.</summary>
public sealed record WcfOperationRow(WsdlOperation Operation, string Signature)
{
    public string Name => Operation.Name;

    public string Action => Operation.Action;
}

/// <summary>An endpoint in the test client's list.</summary>
public sealed record WcfEndpointRow(WsdlEndpoint Endpoint)
{
    public string Label => $"{Endpoint.Name} ({Endpoint.BindingLabel})";

    public string Address => Endpoint.Address;

    /// <summary>The whole name and address: the drop-down trims both (M34g).</summary>
    public string ToolTip => $"{Label}\n{Address}";
}

/// <summary>
/// The WCF Test Client (M42, ADR 0069), a document: a service's metadata from a URL, a WSDL file or a running CoreWCF
/// service, its endpoints and operations, a request form generated from the operation's message (or its XML, edited),
/// and the response formatted and as a tree. The work is the workbench's <see cref="WcfTestClient"/>.
/// </summary>
public sealed class WcfTestClientViewModel : ObservableObject, IDisposable
{
    private readonly WcfTestClient _client;
    private readonly Func<Task<IReadOnlyList<DiscoveredService>>> _discover;
    private readonly Func<Task<string?>> _browse;
    private CancellationTokenSource? _sending;
    private string _address;
    private WsdlDocument? _document;
    private WcfEndpointRow? _endpoint;
    private WsdlOperation? _operation;
    private SoapRequest? _request;
    private string _requestXml = string.Empty;
    private bool _xmlEdited;
    private int _requestTab;
    private int _responseView;
    private SoapResponse? _response;
    private readonly HashSet<SoapField> _collapsed = [];
    private DiscoveredService? _discovered;
    private bool _isBusy;
    private string? _error;
    private string? _status;

    public WcfTestClientViewModel(WcfTestClient client, string? address, Func<Task<IReadOnlyList<DiscoveredService>>> discover, Func<Task<string?>> browse)
    {
        _client = client;
        _discover = discover;
        _browse = browse;
        _address = address ?? string.Empty;
        LoadCommand = new RelayCommand(() => _ = LoadAsync());
        SendCommand = new RelayCommand(() => _ = SendAsync());
        CancelCommand = new RelayCommand(() => _sending?.Cancel());
        DiscoverCommand = new RelayCommand(() => _ = DiscoverAsync());
        BrowseCommand = new RelayCommand(() => _ = BrowseAsync());
        ResetCommand = new RelayCommand(() => SelectedOperation = _operation is { } op ? Reselect(op) : null);
    }

    public string Title => Localizer.Get("msg.wcf.title", "WCF Test Client");

    /// <summary>The editor's font, for the XML.</summary>
    public EditorFont? Font { get; init; }

    /// <summary>The metadata address (…/Service.svc, …?wsdl) or a WSDL file.</summary>
    public string Address
    {
        get => _address;
        set => SetField(ref _address, value ?? string.Empty);
    }

    public ObservableCollection<DiscoveredService> Discovered { get; } = [];

    public bool HasDiscovered => Discovered.Count > 0;

    /// <summary>Picking a running service loads its metadata.</summary>
    public DiscoveredService? SelectedDiscovered
    {
        get => _discovered;
        set
        {
            if (SetField(ref _discovered, value) && value is not null)
            {
                Address = value.MetadataUrl;
                _ = LoadAsync();
            }
        }
    }

    public WsdlDocument? Document => _document;

    public ObservableCollection<WcfEndpointRow> Endpoints { get; } = [];

    public WcfEndpointRow? SelectedEndpoint
    {
        get => _endpoint;
        set
        {
            if (SetField(ref _endpoint, value))
            {
                Operations.Clear();
                OperationRows.Clear();
                foreach (var operation in value?.Endpoint.Operations ?? [])
                {
                    Operations.Add(operation);
                    OperationRows.Add(new WcfOperationRow(operation, _document is null ? operation.Name : Signature(_document, value!.Endpoint, operation)));
                }

                OnPropertyChanged(nameof(OperationCount));
                OnPropertyChanged(nameof(HasOperations));

                OnPropertyChanged(nameof(EndpointProblem));
                OnPropertyChanged(nameof(HasEndpointProblem));
                SelectedOperation = Operations.FirstOrDefault(o => o.Name == _operation?.Name) ?? Operations.FirstOrDefault();
            }
        }
    }

    public string? EndpointProblem => _endpoint?.Endpoint.Problem;

    public bool HasEndpointProblem => EndpointProblem is not null;

    public ObservableCollection<WsdlOperation> Operations { get; } = [];

    /// <summary>The operations with their signatures, as the list shows them (M34h).</summary>
    public ObservableCollection<WcfOperationRow> OperationRows { get; } = [];

    public int OperationCount => Operations.Count;

    public bool HasOperations => Operations.Count > 0;

    /// <summary>The form has fields: its column headers show.</summary>
    public bool HasFields => Rows.Count > 0;

    public WsdlOperation? SelectedOperation
    {
        get => _operation;
        set
        {
            if (SetField(ref _operation, value))
            {
                OnPropertyChanged(nameof(SelectedOperationRow));
                OnPropertyChanged(nameof(HasOperation));
                OnPropertyChanged(nameof(OperationName));
                OnPropertyChanged(nameof(OperationAction));
                BuildRequest();
            }
        }
    }

    public WcfOperationRow? SelectedOperationRow
    {
        get => OperationRows.FirstOrDefault(r => ReferenceEquals(r.Operation, _operation));
        set => SelectedOperation = value?.Operation;
    }

    public bool HasOperation => _operation is not null;

    public string OperationName => _operation?.Name ?? string.Empty;

    public string OperationAction => _operation?.Action ?? string.Empty;

    /// <summary>
    /// An operation as C# would declare it: <c>Greet(Person person) → string</c>, a list as its items' type
    /// (<c>Person[]</c>), <c>void</c> for one-way operations and empty responses.
    /// </summary>
    public static string Signature(WsdlDocument document, WsdlEndpoint endpoint, WsdlOperation operation)
    {
        try
        {
            var parameters = new SoapRequest(document, endpoint, operation).Parameters;
            var result = operation.Output is { } output ? new SoapRequest(document, endpoint, operation with { Input = output }).Parameters : [];
            var returns = result.Count switch
            {
                0 => "void",
                1 => TypeOf(result[0]),
                _ => "(" + string.Join(", ", result.Select(TypeOf)) + ")",
            };
            return $"{operation.Name}({string.Join(", ", parameters.Select(p => $"{TypeOf(p)} {p.Name.LocalName}"))}) → {returns}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            return operation.Name;
        }

        static string TypeOf(SoapField field) => ListItems(field)?.TypeLabel ?? field.TypeLabel;
    }

    /// <summary>The repeated element of a list wrapper (a complex element whose only child repeats), or null.</summary>
    public static SoapField? ListItems(SoapField field) =>
        field is { Kind: SoapFieldKind.Complex, Truncated: false, Children: [{ Kind: SoapFieldKind.Repeated } items] } ? items : null;

    public SoapRequest? Request => _request;

    public ObservableCollection<SoapFieldRow> Rows { get; } = [];

    public bool HasNoParameters => _request is not null && Rows.Count == 0;

    /// <summary>No operation yet: the request area says how to get one (M34g: it was blank).</summary>
    public bool HasNoRequest => _request is null;

    /// <summary>The request's envelope: generated from the form, or edited (then sent as it is).</summary>
    public string RequestXml
    {
        get => _requestXml;
        set
        {
            if (SetField(ref _requestXml, value ?? string.Empty))
            {
                _xmlEdited = true;
            }
        }
    }

    /// <summary>0: the response as a tree; 1: its XML (the Tree or XML segmented control, M34h).</summary>
    public int ResponseView
    {
        get => _responseView;
        set
        {
            if (SetField(ref _responseView, value == 1 ? 1 : 0))
            {
                OnPropertyChanged(nameof(IsResponseTree));
                OnPropertyChanged(nameof(IsResponseXml));
            }
        }
    }

    public bool IsResponseTree => _responseView == 0;

    public bool IsResponseXml => _responseView == 1;

    /// <summary>0: the form; 1: the XML. The XML follows the form until it is edited.</summary>
    public int RequestTab
    {
        get => _requestTab;
        set
        {
            if (SetField(ref _requestTab, value))
            {
                OnPropertyChanged(nameof(IsFormTab));
                OnPropertyChanged(nameof(IsXmlTab));
                if (value == 1 && !_xmlEdited)
                {
                    RegenerateXml();
                }
            }
        }
    }

    public SoapResponse? Response
    {
        get => _response;
        private set
        {
            if (SetField(ref _response, value))
            {
                ResponseTree.Clear();
                if (value?.Tree is { } tree)
                {
                    ResponseTree.Add(tree);
                }

                OnPropertyChanged(nameof(ResponseText));
                OnPropertyChanged(nameof(ResponseSummary));
                OnPropertyChanged(nameof(HasResponse));
                OnPropertyChanged(nameof(HasNoResponse));
                OnPropertyChanged(nameof(IsFault));
                OnPropertyChanged(nameof(IsResponseOk));
                OnPropertyChanged(nameof(IsResponseError));
                OnPropertyChanged(nameof(ResponseChip));
                OnPropertyChanged(nameof(ResponseReason));
                OnPropertyChanged(nameof(HasResponseReason));
                OnPropertyChanged(nameof(ResponseMeta));
                OnPropertyChanged(nameof(IsLoaded));
            }
        }
    }

    public ObservableCollection<XmlTreeNode> ResponseTree { get; } = [];

    public string ResponseText => _response?.Formatted ?? string.Empty;

    public string ResponseSummary => _response?.Summary ?? string.Empty;

    public bool HasResponse => _response is not null;

    public bool IsFault => _response?.IsFault == true;

    public bool HasNoResponse => _response is null;

    public bool IsFormTab => _requestTab == 0;

    public bool IsXmlTab => _requestTab == 1;

    /// <summary>The response's chip is green: a 2xx without a fault.</summary>
    public bool IsResponseOk => _response is { IsFault: false, StatusCode: >= 200 and < 300 };

    /// <summary>The response's chip is red: a fault, or an HTTP error.</summary>
    public bool IsResponseError => _response is not null && !IsResponseOk;

    /// <summary>The response's chip: <c>200 OK</c>, or the fault's code (<c>Fault s:Client</c>).</summary>
    public string ResponseChip => _response is not { } response ? string.Empty
        : response.IsFault ? Localizer.Format("msg.wcf.faultChip", "Fault {0}", response.FaultCode ?? string.Empty).TrimEnd()
        : StatusLabel(response.StatusCode);

    /// <summary>A fault's reason, beside its chip.</summary>
    public string ResponseReason => _response?.IsFault == true ? _response.FaultReason ?? string.Empty : string.Empty;

    public bool HasResponseReason => ResponseReason.Length > 0;

    /// <summary>The status and time after the chip: <c>14 ms</c>, or <c>500 · 11 ms</c> after a fault.</summary>
    public string ResponseMeta => _response is not { } response ? string.Empty
        : response.IsFault ? $"{response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture)} · {(int)response.Elapsed.TotalMilliseconds} ms"
        : $"{(int)response.Elapsed.TotalMilliseconds} ms";

    /// <summary>A status code with its usual reason phrase (<c>200 OK</c>), or alone.</summary>
    public static string StatusLabel(int code) => code switch
    {
        200 => "200 OK",
        201 => "201 Created",
        202 => "202 Accepted",
        204 => "204 No Content",
        400 => "400 Bad Request",
        401 => "401 Unauthorized",
        403 => "403 Forbidden",
        404 => "404 Not Found",
        405 => "405 Method Not Allowed",
        415 => "415 Unsupported Media Type",
        500 => "500 Internal Server Error",
        502 => "502 Bad Gateway",
        503 => "503 Service Unavailable",
        _ => code.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>The metadata is loaded and nothing went wrong: the status line shows its check mark.</summary>
    public bool IsLoaded => _document is not null && !_isBusy && _error is null && _status is not null && _response?.IsFault != true;

    public string Warnings => _document is null ? string.Empty : string.Join("\n", _document.Warnings);

    public bool HasWarnings => _document is { Warnings.Count: > 0 };

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsLoaded));
            }
        }
    }

    public string? Error
    {
        get => _error;
        set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(IsLoaded));
            }
        }
    }

    public bool HasError => _error is not null;

    public string? Status
    {
        get => _status;
        private set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(IsLoaded));
            }
        }
    }

    public ICommand LoadCommand { get; }

    public ICommand SendCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand DiscoverCommand { get; }

    public ICommand BrowseCommand { get; }

    public ICommand ResetCommand { get; }

    public async Task LoadAsync()
    {
        if (Address.Trim().Length == 0)
        {
            Error = Localizer.Get("msg.wcf.noAddress", "Enter the service's metadata address (…/Service.svc or …?wsdl) or choose a WSDL file.");
            return;
        }

        Error = null;
        IsBusy = true;
        Status = Localizer.Get("msg.wcf.reading", "Reading the metadata…");
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var document = await _client.LoadAsync(Address, limit.Token).ConfigureAwait(true);
            SetDocument(document);
            Status = Localizer.Format("msg.wcf.loaded", "Endpoints: {0}. Operations: {1}. Documents read: {2}.", document.Endpoints.Count, document.Endpoints.SelectMany(e => e.Operations).Select(o => o.Name).Distinct(StringComparer.Ordinal).Count(), document.DocumentCount);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException or UriFormatException)
        {
            Error = ex is OperationCanceledException ? Localizer.Get("msg.wcf.noAnswer", "The service didn't answer within 2 minutes.") : ex.Message;
            Status = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void SetDocument(WsdlDocument document)
    {
        _document = document;
        Endpoints.Clear();
        foreach (var endpoint in document.Endpoints)
        {
            Endpoints.Add(new WcfEndpointRow(endpoint));
        }

        OnPropertyChanged(nameof(Document));
        OnPropertyChanged(nameof(Warnings));
        OnPropertyChanged(nameof(HasWarnings));
        _endpoint = null;
        SelectedEndpoint = Endpoints.FirstOrDefault(e => e.Endpoint.Problem is null) ?? Endpoints.FirstOrDefault();
    }

    /// <summary>Selects an operation by name (the palette, the tests, the smoke test).</summary>
    public void Select(string operation, SoapVersion? soap = null)
    {
        if (soap is { } version && Endpoints.FirstOrDefault(e => e.Endpoint.Soap == version) is { } endpoint)
        {
            SelectedEndpoint = endpoint;
        }

        SelectedOperation = Operations.FirstOrDefault(o => o.Name == operation) ?? SelectedOperation;
    }

    /// <summary>The row of a field, by its path of names below the message ("person.Tags.string").</summary>
    public SoapFieldRow? Row(string path)
    {
        var names = path.Split('.');
        IEnumerable<SoapField> level = _request?.Parameters ?? [];
        SoapField? field = null;
        foreach (var name in names)
        {
            field = level.FirstOrDefault(f => f.Name.LocalName == name);
            if (field is null)
            {
                return null;
            }

            level = field.Children;
        }

        return Rows.FirstOrDefault(r => ReferenceEquals(r.Field, field) || ReferenceEquals(r.Items, field));
    }

    private WsdlOperation? Reselect(WsdlOperation operation)
    {
        _operation = null;
        return operation;
    }

    private void BuildRequest()
    {
        _request = _document is not null && _endpoint is not null && _operation is not null ? new SoapRequest(_document, _endpoint.Endpoint, _operation) : null;
        OnPropertyChanged(nameof(Request));
        _xmlEdited = false;
        _collapsed.Clear();
        RebuildRows();
        RegenerateXml();
        Response = null;
    }

    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var field in _request?.Parameters ?? [])
        {
            AddRows(field, 0, null);
        }

        OnPropertyChanged(nameof(HasNoParameters));
        OnPropertyChanged(nameof(HasNoRequest));
        OnPropertyChanged(nameof(HasFields));
    }

    private void AddRows(SoapField field, int depth, SoapField? repeatedParent, int index = -1)
    {
        var items = ListItems(field);
        var expanded = !_collapsed.Contains(field);
        Rows.Add(new SoapFieldRow(field, depth, OnFieldChanged, OnAdd, repeatedParent is null ? null : row => OnRemove(repeatedParent, row))
        {
            Items = items,
            Index = repeatedParent is null ? -1 : index,
            IsExpanded = expanded,
            Toggle = OnToggle,
        });
        if (!expanded)
        {
            return;
        }

        var children = (items ?? field).Children;
        var parent = items ?? (field.Kind == SoapFieldKind.Repeated ? field : null);
        for (var i = 0; i < children.Count; i++)
        {
            AddRows(children[i], depth + 1, parent, i);
        }
    }

    /// <summary>Collapses or expands a group's rows.</summary>
    private void OnToggle(SoapFieldRow row)
    {
        if (!_collapsed.Remove(row.Field))
        {
            _collapsed.Add(row.Field);
        }

        RebuildRows();
    }

    private void OnFieldChanged(SoapFieldRow row)
    {
        if (!_xmlEdited)
        {
            RegenerateXml();
        }
    }

    private void OnAdd(SoapFieldRow row)
    {
        row.ItemsField.NewItem();
        _collapsed.Remove(row.Field);
        RebuildRows();
        OnFieldChanged(row);
    }

    private void OnRemove(SoapField repeated, SoapFieldRow row)
    {
        repeated.Children.Remove(row.Field);
        RebuildRows();
        OnFieldChanged(row);
    }

    private void RegenerateXml()
    {
        _requestXml = _request?.BuildEnvelope() ?? string.Empty;
        OnPropertyChanged(nameof(RequestXml));
    }

    /// <summary>
    /// Sends the request: the form's (checked against the schema's types) or, on the XML tab after an edit, the XML as it is.
    /// </summary>
    public async Task SendAsync()
    {
        if (_request is not { } request)
        {
            Error = Localizer.Get("msg.wcf.noOperation", "Load the service's metadata and choose an operation.");
            return;
        }

        if (request.Endpoint.Problem is { } problem)
        {
            Error = problem;
            return;
        }

        string envelope;
        if (_xmlEdited && _requestTab == 1)
        {
            envelope = RequestXml;
        }
        else
        {
            if (request.Validate() is { Count: > 0 } problems)
            {
                Error = string.Join("\n", problems);
                return;
            }

            RegenerateXml();
            envelope = _requestXml;
        }

        Error = null;
        IsBusy = true;
        Status = Localizer.Format("msg.wcf.calling", "Calling {0}…", request.Operation.Name);
        _sending?.Dispose();
        _sending = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            Response = await _client.SendAsync(request, envelope, _sending.Token).ConfigureAwait(true);
            Status = Response.Summary;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or OperationCanceledException or IOException)
        {
            Error = ex is OperationCanceledException ? Localizer.Get("msg.wcf.canceled", "Canceled, or no answer within 2 minutes.") : ex.Message;
            Status = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Looks for running CoreWCF services that publish metadata. With <paramref name="selectFirst"/> (the command) the
    /// first is loaded; without it (the Running Services menu, M34h) they are only listed, and the service loaded and
    /// its form stay until one is chosen (<see cref="LoadDiscoveredAsync"/>).
    /// </summary>
    public async Task DiscoverAsync(bool selectFirst = true)
    {
        var status = _status;
        Error = null;
        IsBusy = true;
        Status = Localizer.Get("msg.wcf.discovering", "Looking for running CoreWCF services…");
        try
        {
            var found = await _discover().ConfigureAwait(true);
            Discovered.Clear();
            foreach (var service in found)
            {
                Discovered.Add(service);
            }

            OnPropertyChanged(nameof(HasDiscovered));
            Status = found.Count == 0 ? Localizer.Get("msg.wcf.noneRunning", "No running CoreWCF service publishes metadata. Run the service (Ctrl+F5), with ServiceMetadataBehavior.HttpGetEnabled.")
                : selectFirst ? null : status;
            if (found.Count > 0 && selectFirst)
            {
                SelectedDiscovered = found[0];
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>A service chosen from Running Services: its metadata is loaded (again, when it is the one shown).</summary>
    public Task LoadDiscoveredAsync(DiscoveredService service)
    {
        _discovered = service;
        OnPropertyChanged(nameof(SelectedDiscovered));
        Address = service.MetadataUrl;
        return LoadAsync();
    }

    private async Task BrowseAsync()
    {
        if (await _browse().ConfigureAwait(true) is { } file)
        {
            Address = file;
            await LoadAsync().ConfigureAwait(true);
        }
    }

    public void Dispose()
    {
        _sending?.Dispose();
        _client.Dispose();
    }
}

/// <summary>Bindings for the WCF Test Client's view.</summary>
public static class WcfStyles
{
    /// <summary>The required mark after a field's type.</summary>
    public static Avalonia.Data.Converters.IValueConverter RequiredMark { get; } =
        new Avalonia.Data.Converters.FuncValueConverter<bool, string>(required => required ? "*" : string.Empty);
}
