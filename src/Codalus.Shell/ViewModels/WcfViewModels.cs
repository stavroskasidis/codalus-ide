using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Wcf;

namespace Codalus.Shell.ViewModels;

/// <summary>An operation of an endpoint in the WCF Test Client's list.</summary>
public sealed record WcfOperationRow(WsdlEndpoint Endpoint, WsdlOperation Operation)
{
    public string Name => Operation.Name;

    public string EndpointLabel => $"{Endpoint.Label} (SOAP {Endpoint.SoapVersion})";

    /// <summary>Why it can't be called (the endpoint's reason first), else null.</summary>
    public string? Unsupported => Endpoint.Unsupported ?? Operation.Unsupported;

    public bool IsSupported => Unsupported is null;

    public string Detail => Unsupported is { } reason ? "Can't be tested: " + reason : Endpoint.Address;
}

/// <summary>
/// The WCF Test Client (M42): a service's operations from its metadata, a request built from the contract to edit,
/// and the response. The work is <see cref="WcfTestClient"/>'s; the shell supplies the actions.
/// </summary>
public sealed class WcfTestClientViewModel : ObservableObject
{
    private readonly Func<WcfTestClientViewModel, string, Task> _action;
    private string _address = string.Empty;
    private WcfOperationRow? _selected;
    private string _request = string.Empty;
    private string _response = string.Empty;
    private string? _status;
    private bool _isFault;
    private bool _isBusy;
    private string? _error;

    public WcfTestClientViewModel(WcfTestClient client, Func<WcfTestClientViewModel, string, Task> action)
    {
        Client = client;
        _action = action;
        LoadCommand = new RelayCommand(() => _ = RunAsync("load"));
        BrowseCommand = new RelayCommand(() => _ = RunAsync("browse"));
        InvokeCommand = new RelayCommand(() => _ = RunAsync("invoke"));
        ResetCommand = new RelayCommand(() => Request = _selected is null ? string.Empty : WcfTestClient.SampleRequest(_selected.Operation));
    }

    public WcfTestClient Client { get; }

    /// <summary>The service's address (its metadata is read from <c>?singleWsdl</c> or <c>?wsdl</c>), a <c>?wsdl</c> address, or a WSDL file.</summary>
    public string Address
    {
        get => _address;
        set => SetField(ref _address, value ?? string.Empty);
    }

    public ObservableCollection<WcfOperationRow> Operations { get; } = [];

    public bool HasOperations => Operations.Count > 0;

    public WcfOperationRow? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                Request = value is null ? string.Empty : WcfTestClient.SampleRequest(value.Operation);
                Response = string.Empty;
                Status = value?.Unsupported is { } reason ? "Can't be tested: " + reason + "." : null;
                IsFault = false;
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(CanInvoke));
            }
        }
    }

    public bool HasSelection => _selected is not null;

    public bool CanInvoke => _selected is { IsSupported: true } && !_isBusy;

    /// <summary>The request body's content, to edit before invoking.</summary>
    public string Request
    {
        get => _request;
        set => SetField(ref _request, value ?? string.Empty);
    }

    public string Response
    {
        get => _response;
        set => SetField(ref _response, value ?? string.Empty);
    }

    /// <summary>The last call's status and time, or why the operation can't be called.</summary>
    public string? Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public bool IsFault
    {
        get => _isFault;
        set => SetField(ref _isFault, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanInvoke));
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
            }
        }
    }

    public bool HasError => _error is not null;

    public ICommand LoadCommand { get; }

    public ICommand BrowseCommand { get; }

    public ICommand InvokeCommand { get; }

    /// <summary>Puts the operation's sample request back.</summary>
    public ICommand ResetCommand { get; }

    /// <summary>Lists the endpoints' operations of loaded metadata, selecting the first one that can be called.</summary>
    public void SetMetadata(WsdlDocument metadata)
    {
        Operations.Clear();
        foreach (var endpoint in metadata.Endpoints)
        {
            foreach (var operation in endpoint.Operations)
            {
                Operations.Add(new WcfOperationRow(endpoint, operation));
            }
        }

        OnPropertyChanged(nameof(HasOperations));
        Selected = Operations.FirstOrDefault(o => o.IsSupported) ?? Operations.FirstOrDefault();
        if (Operations.Count == 0)
        {
            Error = "The metadata has no SOAP endpoints.";
        }
    }

    public void SetResponse(WcfResponse response)
    {
        Response = response.Body;
        IsFault = response.IsFault;
        var time = response.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture);
        Status = response.Fault is { } fault ? $"Fault ({response.Status}, {time} ms): {fault}" : $"{response.Status} in {time} ms";
    }

    public async Task RunAsync(string action)
    {
        Error = null;
        IsBusy = true;
        try
        {
            await _action(this, action).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
