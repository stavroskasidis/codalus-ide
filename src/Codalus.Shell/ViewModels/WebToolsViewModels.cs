using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Projects.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Web;

namespace Codalus.Shell.ViewModels;

// ------------------------------------------------------------------ Add New Scaffolded Item

/// <summary>One field of a scaffolder's form, generated from its <see cref="ScaffoldParameter"/>.</summary>
public abstract class ScaffoldFieldViewModel(ScaffoldParameter parameter) : ObservableObject
{
    public ScaffoldParameter Parameter { get; } = parameter;

    public string Id => Parameter.Id;

    public string Label => Parameter.Required ? Parameter.Label + " *" : Parameter.Label;

    public string? Description => Parameter.Description;

    public bool HasDescription => Parameter.Description is not null;

    public abstract string? Value { get; }
}

/// <summary>A name, a folder or free text; model and context fields offer what the project's DbContexts have.</summary>
public sealed class ScaffoldTextField : ScaffoldFieldViewModel
{
    private string _text;
    private string? _suggestion;

    public ScaffoldTextField(ScaffoldParameter parameter, string? initial = null)
        : base(parameter) => _text = initial ?? parameter.Default ?? string.Empty;

    public string Text
    {
        get => _text;
        set => SetField(ref _text, value ?? string.Empty);
    }

    public ObservableCollection<string> Suggestions { get; } = [];

    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>Picking a suggestion fills the text.</summary>
    public string? Suggestion
    {
        get => _suggestion;
        set
        {
            if (SetField(ref _suggestion, value) && value is not null)
            {
                Text = value;
            }
        }
    }

    public string Placeholder => Parameter.Kind switch
    {
        ScaffoldParameterKind.Model => "Shop.Models.Product, or Product",
        ScaffoldParameterKind.DbContext => "An existing context, or a new name",
        ScaffoldParameterKind.Folder => "A folder in the project",
        _ => string.Empty,
    };

    public override string? Value => Text;

    public void SetSuggestions(IEnumerable<string> values)
    {
        Suggestions.Clear();
        foreach (var value in values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            Suggestions.Add(value);
        }

        if (Text.Length == 0 && Suggestions.Count == 1 && Parameter.Kind == ScaffoldParameterKind.DbContext)
        {
            Text = Suggestions[0];
        }

        OnPropertyChanged(nameof(HasSuggestions));
    }
}

public sealed class ScaffoldBoolField(ScaffoldParameter parameter) : ScaffoldFieldViewModel(parameter)
{
    private bool _isChecked = bool.TryParse(parameter.Default, out var on) && on;

    public bool IsChecked
    {
        get => _isChecked;
        set => SetField(ref _isChecked, value);
    }

    public override string? Value => IsChecked ? "true" : "false";
}

/// <summary>A template or a database provider.</summary>
public sealed class ScaffoldChoiceField(ScaffoldParameter parameter, string? initial = null) : ScaffoldFieldViewModel(parameter)
{
    private string? _selected = initial ?? parameter.Default;

    public IReadOnlyList<string> Choices { get; } = parameter.Choices ?? [];

    public string? Selected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }

    public override string? Value => Selected;
}

/// <summary>A scaffolder in the list: its name, what it does, and what runs it.</summary>
public sealed record ScaffolderRow(Scaffolder Scaffolder)
{
    public string Name => Scaffolder.Name;

    public string Description => Scaffolder.Description;

    public string Engine => Scaffolder.Engine switch
    {
        ScaffoldEngine.DotnetNew => $"SDK item template (dotnet new {Scaffolder.Command})",
        ScaffoldEngine.CodeGenerator => $"dotnet aspnet-codegenerator {Scaffolder.Command}",
        _ => "Written by the IDE",
    };

    public string Icon => Scaffolder.Engine == ScaffoldEngine.CodeGenerator ? "database" : "file";
}

/// <summary>
/// Add New Scaffolded Item (ADR 0041): categories, the scaffolders, and a form generated from the chosen one's
/// parameters. The code generator's state (tool, design package) is shown with an Install button.
/// </summary>
public sealed class ScaffoldViewModel : ObservableObject
{
    private readonly Func<ScaffoldViewModel, Task> _run;
    private readonly Func<ScaffoldViewModel, Task> _install;
    private readonly string? _initialFolder;
    private string _selectedCategory;
    private ScaffolderRow? _selected;
    private IReadOnlyList<string> _models = [];
    private IReadOnlyList<string> _contexts = [];
    private string? _defaultProvider;
    private ScaffoldStatusResult? _status;
    private bool _isBusy;
    private string? _error;
    private string? _result;

    public ScaffoldViewModel(string project, string? folder, Func<ScaffoldViewModel, Task> run, Func<ScaffoldViewModel, Task> install)
    {
        Project = project;
        _run = run;
        _install = install;
        var directory = Path.GetDirectoryName(project)!;
        if (folder is not null && Path.GetRelativePath(directory, folder) is { } relative && relative != "." && !relative.StartsWith("..", StringComparison.Ordinal))
        {
            _initialFolder = relative.Replace('\\', '/');
        }

        _selectedCategory = Scaffolders.Categories[0];
        AddCommand = new RelayCommand(() => _ = AddAsync());
        InstallCommand = new RelayCommand(() => _ = InstallAsync());
        SelectedRow = Rows.Count > 0 ? Rows[0] : null;
    }

    public string Project { get; }

    public string Title => $"Add New Scaffolded Item — {Path.GetFileNameWithoutExtension(Project)}";

    public IReadOnlyList<string> Categories => Scaffolders.Categories;

    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (value is not null && SetField(ref _selectedCategory, value))
            {
                OnPropertyChanged(nameof(Rows));
                SelectedRow = Rows.Count > 0 ? Rows[0] : null;
            }
        }
    }

    public IReadOnlyList<ScaffolderRow> Rows => [.. Scaffolders.All.Where(s => s.Category == _selectedCategory).Select(s => new ScaffolderRow(s))];

    public ScaffolderRow? SelectedRow
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                BuildFields();
                Error = null;
                OnPropertyChanged(nameof(ToolMessage));
                OnPropertyChanged(nameof(NeedsTool));
            }
        }
    }

    public Scaffolder? Scaffolder => _selected?.Scaffolder;

    public ObservableCollection<ScaffoldFieldViewModel> Fields { get; } = [];

    /// <summary>The code generator's state, for the scaffolders that need it.</summary>
    public ScaffoldStatusResult? Status
    {
        get => _status;
        set
        {
            if (SetField(ref _status, value))
            {
                _defaultProvider = value is null ? null : Scaffolders.DbProviderFor(value.EfProviders);
                if (_defaultProvider is not null)
                {
                    foreach (var choice in Fields.OfType<ScaffoldChoiceField>().Where(f => f.Parameter.Kind == ScaffoldParameterKind.DbProvider && f.Selected is null))
                    {
                        choice.Selected = _defaultProvider;
                    }
                }

                OnPropertyChanged(nameof(ToolMessage));
                OnPropertyChanged(nameof(NeedsTool));
            }
        }
    }

    /// <summary>The code generator can't run these yet: the Install button shows.</summary>
    public bool NeedsTool => Scaffolder?.Engine == ScaffoldEngine.CodeGenerator && _status is { } status
        && (!status.ToolAvailable || status.DesignPackageVersion is null || (Scaffolder.UsesEntityFramework && status.EfToolsVersion is null));

    public string? ToolMessage => Scaffolder?.Engine != ScaffoldEngine.CodeGenerator ? null
        : _status is null ? "Checking dotnet-aspnet-codegenerator…"
        : NeedsTool ? (_status.Message ?? "The Entity Framework scaffolders need Microsoft.EntityFrameworkCore.Tools.") + " Install adds what is missing (a local tool and packages)."
        : $"dotnet-aspnet-codegenerator {_status.ToolVersion} ({_status.ToolSource}), {SolutionDesignPackage}";

    private string SolutionDesignPackage => $"Microsoft.VisualStudio.Web.CodeGeneration.Design {_status?.DesignPackageVersion}";

    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
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

    public string? Result
    {
        get => _result;
        set
        {
            if (SetField(ref _result, value))
            {
                OnPropertyChanged(nameof(HasResult));
            }
        }
    }

    public bool HasResult => _result is not null;

    public ICommand AddCommand { get; }

    public ICommand InstallCommand { get; }

    /// <summary>Selects a scaffolder by id (the palette and the tests).</summary>
    public void Select(string id)
    {
        if (Scaffolders.Find(id) is { } scaffolder)
        {
            SelectedCategory = scaffolder.Category;
            SelectedRow = Rows.FirstOrDefault(r => r.Scaffolder.Id == id);
        }
    }

    public ScaffoldFieldViewModel? Field(string id) => Fields.FirstOrDefault(f => f.Id == id);

    /// <summary>The entity types and contexts Roslyn found, offered by the model and context fields.</summary>
    public void SetSuggestions(IReadOnlyList<string> models, IReadOnlyList<string> contexts)
    {
        _models = models;
        _contexts = contexts;
        ApplySuggestions();
    }

    public IReadOnlyDictionary<string, string?> Values() => Fields.ToDictionary(f => f.Id, f => f.Value);

    private void BuildFields()
    {
        Fields.Clear();
        if (Scaffolder is not { } scaffolder)
        {
            return;
        }

        foreach (var parameter in scaffolder.Parameters)
        {
            Fields.Add(parameter.Kind switch
            {
                ScaffoldParameterKind.Bool => new ScaffoldBoolField(parameter),
                ScaffoldParameterKind.Choice => new ScaffoldChoiceField(parameter),
                ScaffoldParameterKind.DbProvider => new ScaffoldChoiceField(parameter, _defaultProvider),
                ScaffoldParameterKind.Folder => new ScaffoldTextField(parameter, _initialFolder),
                _ => new ScaffoldTextField(parameter),
            });
        }

        ApplySuggestions();
    }

    private void ApplySuggestions()
    {
        foreach (var field in Fields.OfType<ScaffoldTextField>())
        {
            if (field.Parameter.Kind == ScaffoldParameterKind.Model)
            {
                field.SetSuggestions(_models);
            }
            else if (field.Parameter.Kind == ScaffoldParameterKind.DbContext)
            {
                field.SetSuggestions(_contexts);
            }
        }
    }

    public async Task AddAsync()
    {
        if (Scaffolder is not { } scaffolder)
        {
            return;
        }

        if (Scaffolders.Validate(scaffolder, Values()) is { } problem)
        {
            Error = problem;
            return;
        }

        Error = null;
        Result = null;
        IsBusy = true;
        try
        {
            await _run(this).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task InstallAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            await _install(this).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

// ------------------------------------------------------------------ service references

/// <summary>A reference in the list: what it is, its document, and how its code is generated.</summary>
public sealed record ServiceReferenceRow(ServiceReferenceInfo Info)
{
    public bool IsWcf => Info.Kind == ServiceReferenceKinds.Wcf;

    public string Kind => Info.Kind switch
    {
        ServiceReferenceKinds.OpenApi => "OpenAPI",
        ServiceReferenceKinds.Wcf => "WCF",
        _ => "gRPC",
    };

    /// <summary>A WCF reference is its folder's name; the others are their documents'.</summary>
    public string Name => IsWcf ? Info.ClassName ?? Info.Include : Path.GetFileName(Info.Path);

    public string Icon => Info.Kind == ServiceReferenceKinds.Grpc ? "link" : "cloud";

    public string Detail => Info.Kind switch
    {
        ServiceReferenceKinds.OpenApi => $"{Info.CodeGenerator ?? "NSwagCSharp"}{(Info.ClassName is { } name ? ", class " + name : string.Empty)}{(Info.Namespace is { } ns ? ", namespace " + ns : string.Empty)}",
        ServiceReferenceKinds.Wcf => $"dotnet-svcutil{(Info.Namespace is { } wcfNs ? ", namespace " + wcfNs : string.Empty)}, {ServiceReferencesViewModel.OperationsLabel(Info.Wcf?.Operations).ToLowerInvariant()}{(Info.Wcf?.Internal == true ? ", internal" : string.Empty)}",
        _ => $"Services: {Info.GrpcServices ?? GrpcServiceKinds.Both}{(Info.Access is { } access ? ", " + access.ToLowerInvariant() : string.Empty)}",
    };

    public string Source => IsWcf ? Info.Input ?? Info.Include : Info.SourceUrl ?? Info.Include;
}

/// <summary>
/// Service references of a project (ADR 0041): the OpenAPI and gRPC references with Refresh, Edit and Remove, and a
/// form to add one from a file or a URL.
/// </summary>
public sealed class ServiceReferencesViewModel : ObservableObject
{
    private readonly Func<ServiceReferencesViewModel, string, Task> _action;
    private ServiceReferenceRow? _selected;
    private string _kind = ServiceReferenceKinds.OpenApi;
    private string _source = string.Empty;
    private string _namespace = string.Empty;
    private string _className = string.Empty;
    private string _codeGenerator = "NSwagCSharp";
    private string _grpcServices = GrpcServiceKinds.Client;
    private string _editNamespace = string.Empty;
    private string _editClassName = string.Empty;
    private string _editOptions = string.Empty;
    private string _editServices = GrpcServiceKinds.Client;
    private string _wcfName = "ServiceReference";
    private string _wcfOperations = OperationsLabel(WcfOperationKinds.Async);
    private string _wcfCollectionType = ArraysLabel;
    private bool _wcfInternal;
    private bool _wcfReuseTypes = true;
    private string _editOperations = OperationsLabel(WcfOperationKinds.Async);
    private bool _editInternal;
    private string _packages = string.Empty;
    private bool _isBusy;
    private string? _error;
    private string? _result;

    public ServiceReferencesViewModel(string project, Func<ServiceReferencesViewModel, string, Task> action)
    {
        Project = project;
        _action = action;
        AddCommand = new RelayCommand(() => _ = RunAsync("add"));
        BrowseCommand = new RelayCommand(() => _ = RunAsync("browse"));
        RefreshCommand = new RelayCommand(() => _ = RunAsync("refresh"));
        SaveCommand = new RelayCommand(() => _ = RunAsync("save"));
        RemoveCommand = new RelayCommand(() => _ = RunAsync("remove"));
        OpenCommand = new RelayCommand(() => _ = RunAsync("open"));
        ReloadCommand = new RelayCommand(() => _ = RunAsync("reload"));
        TestCommand = new RelayCommand(() => _ = RunAsync("test"));
    }

    public string Project { get; }

    public string Title => $"Service References — {Path.GetFileNameWithoutExtension(Project)}";

    public ObservableCollection<ServiceReferenceRow> References { get; } = [];

    public bool IsEmpty => References.Count == 0;

    public ServiceReferenceRow? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                EditNamespace = value?.Info.Namespace ?? string.Empty;
                EditClassName = value?.Info.ClassName ?? string.Empty;
                EditOptions = value?.Info.Options ?? string.Empty;
                EditServices = value?.Info.GrpcServices ?? GrpcServiceKinds.Both;
                EditOperations = OperationsLabel(value?.Info.Wcf?.Operations);
                EditInternal = value?.Info.Wcf?.Internal == true;
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedIsWcf));
                OnPropertyChanged(nameof(SelectedHasNamespace));
                OnPropertyChanged(nameof(SelectedIsOpenApi));
                OnPropertyChanged(nameof(SelectedIsGrpc));
                OnPropertyChanged(nameof(CanRefresh));
            }
        }
    }

    public bool HasSelection => _selected is not null;

    public bool SelectedIsOpenApi => _selected?.Info.Kind == ServiceReferenceKinds.OpenApi;

    public bool SelectedIsGrpc => _selected?.Info.Kind == ServiceReferenceKinds.Grpc;

    /// <summary>A WCF reference is generated again from its metadata; the others download their documents again.</summary>
    public bool CanRefresh => _selected?.Info.SourceUrl is not null || SelectedIsWcf;

    public bool SelectedIsWcf => _selected?.Info.Kind == ServiceReferenceKinds.Wcf;

    public bool SelectedHasNamespace => SelectedIsOpenApi || SelectedIsWcf;

    public IReadOnlyList<string> Kinds { get; } = ["OpenAPI", "gRPC", "WCF"];

    public string KindLabel
    {
        get => _kind switch { ServiceReferenceKinds.Grpc => "gRPC", ServiceReferenceKinds.Wcf => "WCF", _ => "OpenAPI" };
        set
        {
            var kind = value switch { "gRPC" => ServiceReferenceKinds.Grpc, "WCF" => ServiceReferenceKinds.Wcf, _ => ServiceReferenceKinds.OpenApi };
            if (SetField(ref _kind, kind, nameof(Kind)))
            {
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsOpenApi));
                OnPropertyChanged(nameof(IsGrpc));
                OnPropertyChanged(nameof(IsWcf));
                OnPropertyChanged(nameof(HasNamespace));
            }
        }
    }

    public string Kind => _kind;

    public bool IsOpenApi => _kind == ServiceReferenceKinds.OpenApi;

    public bool IsGrpc => _kind == ServiceReferenceKinds.Grpc;

    /// <summary>A WCF client generated by dotnet-svcutil (M42).</summary>
    public bool IsWcf => _kind == ServiceReferenceKinds.Wcf;

    public bool HasNamespace => IsOpenApi || IsWcf;

    // ------------------------------------------------------------------ WCF (M42)

    private const string ArraysLabel = "Arrays";

    /// <summary>The choices of the generated operations, as labels.</summary>
    public IReadOnlyList<string> OperationChoices { get; } =
        [OperationsLabel(WcfOperationKinds.Async), OperationsLabel(WcfOperationKinds.Both), OperationsLabel(WcfOperationKinds.Sync)];

    public static string OperationsLabel(string? kind) => kind switch
    {
        WcfOperationKinds.Both => "Synchronous and asynchronous operations",
        WcfOperationKinds.Sync => "Synchronous operations",
        _ => "Asynchronous operations",
    };

    private static string OperationsKind(string label) =>
        label == OperationsLabel(WcfOperationKinds.Both) ? WcfOperationKinds.Both : label == OperationsLabel(WcfOperationKinds.Sync) ? WcfOperationKinds.Sync : WcfOperationKinds.Async;

    /// <summary>The collection types Visual Studio offers for the generated data contracts.</summary>
    public IReadOnlyList<string> CollectionTypes { get; } =
    [
        ArraysLabel, "System.Collections.Generic.List`1", "System.Collections.ObjectModel.ObservableCollection`1",
        "System.Collections.ObjectModel.Collection`1", "System.Collections.Generic.LinkedList`1",
    ];

    /// <summary>The new reference's name: its folder under Connected Services and its namespace's last part.</summary>
    public string WcfName
    {
        get => _wcfName;
        set => SetField(ref _wcfName, value ?? string.Empty);
    }

    public string WcfOperations
    {
        get => _wcfOperations;
        set => SetField(ref _wcfOperations, value ?? OperationsLabel(WcfOperationKinds.Async));
    }

    public string WcfCollectionType
    {
        get => _wcfCollectionType;
        set => SetField(ref _wcfCollectionType, value ?? ArraysLabel);
    }

    public bool WcfInternal
    {
        get => _wcfInternal;
        set => SetField(ref _wcfInternal, value);
    }

    /// <summary>Reuse the types of the project's references instead of generating them.</summary>
    public bool WcfReuseTypes
    {
        get => _wcfReuseTypes;
        set => SetField(ref _wcfReuseTypes, value);
    }

    public string EditOperations
    {
        get => _editOperations;
        set => SetField(ref _editOperations, value ?? OperationsLabel(WcfOperationKinds.Async));
    }

    public bool EditInternal
    {
        get => _editInternal;
        set => SetField(ref _editInternal, value);
    }

    /// <summary>The new WCF reference, from the form.</summary>
    public WcfReferenceParams WcfRequest() => new(
        Project,
        WcfName.Trim(),
        Source.Trim(),
        string.IsNullOrWhiteSpace(Namespace) ? null : Namespace.Trim(),
        new WcfOptions(OperationsKind(WcfOperations), WcfCollectionType == ArraysLabel ? null : WcfCollectionType, WcfInternal, ReuseTypes: WcfReuseTypes));

    /// <summary>The selected WCF reference with the edited namespace and options, to generate again.</summary>
    public WcfReferenceParams? WcfEdit() => _selected?.Info is { Kind: ServiceReferenceKinds.Wcf, ClassName: { } name } info
        ? new WcfReferenceParams(
            Project,
            name,
            null,
            string.IsNullOrWhiteSpace(EditNamespace) ? null : EditNamespace.Trim(),
            (info.Wcf ?? new WcfOptions()) with { Operations = OperationsKind(EditOperations), Internal = EditInternal })
        : null;

    /// <summary>A file path, or an http(s) URL to download the document from.</summary>
    public string Source
    {
        get => _source;
        set => SetField(ref _source, value ?? string.Empty);
    }

    public string Namespace
    {
        get => _namespace;
        set => SetField(ref _namespace, value ?? string.Empty);
    }

    public string ClassName
    {
        get => _className;
        set => SetField(ref _className, value ?? string.Empty);
    }

    public IReadOnlyList<string> CodeGenerators { get; } = ["NSwagCSharp", "NSwagTypeScript"];

    public string CodeGenerator
    {
        get => _codeGenerator;
        set => SetField(ref _codeGenerator, value ?? "NSwagCSharp");
    }

    public IReadOnlyList<string> ServiceKinds { get; } = [GrpcServiceKinds.Client, GrpcServiceKinds.Server, GrpcServiceKinds.Both, GrpcServiceKinds.None];

    public string GrpcServices
    {
        get => _grpcServices;
        set => SetField(ref _grpcServices, value ?? GrpcServiceKinds.Client);
    }

    public string EditNamespace
    {
        get => _editNamespace;
        set => SetField(ref _editNamespace, value ?? string.Empty);
    }

    public string EditClassName
    {
        get => _editClassName;
        set => SetField(ref _editClassName, value ?? string.Empty);
    }

    public string EditOptions
    {
        get => _editOptions;
        set => SetField(ref _editOptions, value ?? string.Empty);
    }

    public string EditServices
    {
        get => _editServices;
        set => SetField(ref _editServices, value ?? GrpcServiceKinds.Both);
    }

    /// <summary>The generating packages the project references.</summary>
    public string Packages
    {
        get => _packages;
        set => SetField(ref _packages, value ?? string.Empty);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
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

    public string? Result
    {
        get => _result;
        set
        {
            if (SetField(ref _result, value))
            {
                OnPropertyChanged(nameof(HasResult));
            }
        }
    }

    public bool HasResult => _result is not null;

    public ICommand AddCommand { get; }

    public ICommand BrowseCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand OpenCommand { get; }

    /// <summary>Opens the WCF Test Client on the selected WCF reference's metadata.</summary>
    public ICommand TestCommand { get; }

    public ICommand ReloadCommand { get; }

    public void SetReferences(ServiceReferencesResult result)
    {
        var selected = _selected?.Info.Path;
        References.Clear();
        foreach (var reference in result.References)
        {
            References.Add(new ServiceReferenceRow(reference));
        }

        Packages = result.Packages.Length == 0 ? "No code generation packages yet." : string.Join(", ", result.Packages.Select(p => $"{p.Id} {p.Version}".Trim()));
        Selected = References.FirstOrDefault(r => r.Info.Path == selected) ?? References.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    public ServiceReferenceRequest Request() =>
        new(Project, _kind, Source.Trim(), Namespace, ClassName, IsOpenApi ? CodeGenerator : null, null, IsGrpc ? GrpcServices : null);

    public async Task RunAsync(string action)
    {
        if (action == "add" && Source.Trim().Length == 0)
        {
            Error = IsWcf
                ? "Choose the service's metadata: its address (or a ?wsdl address), or a WSDL file."
                : "Choose the document: a file, or the URL of an OpenAPI document or a .proto file.";
            return;
        }

        Error = null;
        Result = null;
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
