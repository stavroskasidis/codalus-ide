using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Wcf;

namespace Codalus.Shell.ViewModels;

/// <summary>A row of the request form: one field of the message, indented by its depth.</summary>
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
    }

    public SoapField Field { get; }

    public int Depth { get; }

    public Thickness Indent => new(Depth * 16, 0, 0, 0);

    public string Name => Field.Name.LocalName;

    public string Type => Field.TypeLabel + (Field.Optional ? "" : " *");

    public bool IsText => Field.Kind == SoapFieldKind.Simple;

    public bool IsChoice => Field.Kind == SoapFieldKind.Choice;

    public bool IsGroup => Field.Kind is SoapFieldKind.Complex or SoapFieldKind.Repeated;

    public bool CanAdd => Field.CanAddItem;

    public bool CanRemove => _remove is not null;

    public bool CanNil => Field.Nillable;

    public IReadOnlyList<string> Choices => Field.Choices;

    public string Placeholder => Field.Truncated ? Localizer.Get("msg.wcf.nestedTooDeep", "(nested too deep: edit the XML)") : Field.BuiltIn ?? string.Empty;

    /// <summary>The field's name and type, for its trimmed name (M34g).</summary>
    public string ToolTip => $"{Name}: {Type}";

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
    private SoapResponse? _response;
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
                foreach (var operation in value?.Endpoint.Operations ?? [])
                {
                    Operations.Add(operation);
                }

                OnPropertyChanged(nameof(EndpointProblem));
                OnPropertyChanged(nameof(HasEndpointProblem));
                SelectedOperation = Operations.FirstOrDefault(o => o.Name == _operation?.Name) ?? Operations.FirstOrDefault();
            }
        }
    }

    public string? EndpointProblem => _endpoint?.Endpoint.Problem;

    public bool HasEndpointProblem => EndpointProblem is not null;

    public ObservableCollection<WsdlOperation> Operations { get; } = [];

    public WsdlOperation? SelectedOperation
    {
        get => _operation;
        set
        {
            if (SetField(ref _operation, value))
            {
                BuildRequest();
            }
        }
    }

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

    /// <summary>0: the form; 1: the XML. The XML follows the form until it is edited.</summary>
    public int RequestTab
    {
        get => _requestTab;
        set
        {
            if (SetField(ref _requestTab, value) && value == 1 && !_xmlEdited)
            {
                RegenerateXml();
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
                OnPropertyChanged(nameof(IsFault));
            }
        }
    }

    public ObservableCollection<XmlTreeNode> ResponseTree { get; } = [];

    public string ResponseText => _response?.Formatted ?? string.Empty;

    public string ResponseSummary => _response?.Summary ?? string.Empty;

    public bool HasResponse => _response is not null;

    public bool IsFault => _response?.IsFault == true;

    public string Warnings => _document is null ? string.Empty : string.Join("\n", _document.Warnings);

    public bool HasWarnings => _document is { Warnings.Count: > 0 };

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public string? Error
    {
        get => _error;
        set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _error is not null;

    public string? Status
    {
        get => _status;
        private set => SetField(ref _status, value);
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

        return Rows.FirstOrDefault(r => ReferenceEquals(r.Field, field));
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
    }

    private void AddRows(SoapField field, int depth, SoapField? repeatedParent)
    {
        Rows.Add(new SoapFieldRow(field, depth, OnFieldChanged, OnAdd, repeatedParent is null ? null : row => OnRemove(repeatedParent, row)));
        foreach (var child in field.Children)
        {
            AddRows(child, depth + 1, field.Kind == SoapFieldKind.Repeated ? field : null);
        }
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
        row.Field.NewItem();
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

    private async Task DiscoverAsync()
    {
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
            Status = found.Count == 0 ? Localizer.Get("msg.wcf.noneRunning", "No running CoreWCF service publishes metadata. Run the service (Ctrl+F5), with ServiceMetadataBehavior.HttpGetEnabled.") : null;
            if (found.Count > 0)
            {
                SelectedDiscovered = found[0];
            }
        }
        finally
        {
            IsBusy = false;
        }
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
