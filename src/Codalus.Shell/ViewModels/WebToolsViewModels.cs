using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Projects.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Wcf;
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

/// <summary>
/// A reference in the list: what it is, its document, and how its code is generated. An OpenAPI or gRPC reference has
/// <see cref="Info"/>; a WCF reference (M42) has <see cref="Wcf"/>.
/// </summary>
public sealed record ServiceReferenceRow(ServiceReferenceInfo? Info, WcfReferenceInfo? Wcf = null)
{
    public bool IsWcf => Wcf is not null;

    public string Kind => Wcf is not null ? "WCF" : Info!.Kind == ServiceReferenceKinds.OpenApi ? "OpenAPI" : "gRPC";

    public string Name => Wcf?.Name ?? Path.GetFileName(Info!.Path);

    public string Icon => Wcf is not null ? "globe" : Info!.Kind == ServiceReferenceKinds.OpenApi ? "cloud" : "link";

    public string Detail => Wcf is { } wcf
        ? Localizer.Format("msg.serviceReferences.wcfDetail", "dotnet-svcutil, namespace {0}", wcf.Namespace ?? wcf.Name)
          + (wcf.CollectionType is { } collection ? ", " + collection : string.Empty)
          + (wcf.Sync ? Localizer.Get("msg.serviceReferences.wcfSync", ", synchronous too") : string.Empty)
          + (wcf.Internal ? Localizer.Get("msg.serviceReferences.wcfInternal", ", internal") : string.Empty)
        : Info!.Kind == ServiceReferenceKinds.OpenApi
            ? $"{Info.CodeGenerator ?? "NSwagCSharp"}{(Info.ClassName is { } name ? ", class " + name : string.Empty)}{(Info.Namespace is { } ns ? ", namespace " + ns : string.Empty)}"
            : $"Services: {Info.GrpcServices ?? GrpcServiceKinds.Both}{(Info.Access is { } access ? ", " + access.ToLowerInvariant() : string.Empty)}";

    public string Source => Wcf is { } reference ? string.Join(", ", reference.Inputs) : Info!.SourceUrl ?? Info.Include;

    /// <summary>The path of the reference: its document, or a WCF reference's folder.</summary>
    public string Location => Wcf?.Folder ?? Info!.Path;
}

/// <summary>
/// Service references of a project (ADR 0041; WCF, ADR 0069): the OpenAPI, gRPC and WCF references with Refresh, Edit
/// and Remove, and a form to add one from a file or a URL (a WCF one from a service's metadata, a WSDL file, or a
/// running CoreWCF service).
/// </summary>
public sealed class ServiceReferencesViewModel : ObservableObject
{
    /// <summary>The add form's kind for a WCF reference (the others are <see cref="ServiceReferenceKinds"/>).</summary>
    public const string WcfKind = "wcf";

    private string _wcfName = "ServiceReference1";
    private string _collectionType = WcfReferenceService.CollectionTypes[0];
    private string _dictionaryType = WcfReferenceService.DictionaryTypes[0];
    private bool _sync;
    private bool _reuseTypes = true;
    private bool _internal;
    private string _editCollectionType = WcfReferenceService.CollectionTypes[0];
    private string _editDictionaryType = WcfReferenceService.DictionaryTypes[0];
    private bool _editSync;
    private bool _editReuseTypes = true;
    private bool _editInternal;
    private WcfToolStatusResult? _wcfStatus;
    private DiscoveredService? _discovered;
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
        InstallWcfCommand = new RelayCommand(() => _ = RunAsync("installWcf"));
        DiscoverCommand = new RelayCommand(() => _ = RunAsync("discover"));
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
                EditNamespace = value?.Info?.Namespace ?? value?.Wcf?.Namespace ?? string.Empty;
                EditClassName = value?.Info?.ClassName ?? string.Empty;
                EditOptions = value?.Info?.Options ?? string.Empty;
                EditServices = value?.Info?.GrpcServices ?? GrpcServiceKinds.Both;
                EditCollectionType = value?.Wcf?.CollectionType ?? WcfReferenceService.CollectionTypes[0];
                EditDictionaryType = value?.Wcf?.DictionaryCollectionType ?? WcfReferenceService.DictionaryTypes[0];
                EditSync = value?.Wcf?.Sync ?? false;
                EditReuseTypes = value?.Wcf?.ReuseTypes ?? true;
                EditInternal = value?.Wcf?.Internal ?? false;
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedIsOpenApi));
                OnPropertyChanged(nameof(SelectedIsGrpc));
                OnPropertyChanged(nameof(SelectedIsWcf));
                OnPropertyChanged(nameof(SelectedHasNamespace));
                OnPropertyChanged(nameof(CanRefresh));
            }
        }
    }

    public bool HasSelection => _selected is not null;

    public bool SelectedIsOpenApi => _selected?.Info?.Kind == ServiceReferenceKinds.OpenApi;

    public bool SelectedIsGrpc => _selected?.Info?.Kind == ServiceReferenceKinds.Grpc;

    public bool SelectedIsWcf => _selected?.Wcf is not null;

    /// <summary>OpenAPI and WCF references have a namespace to edit.</summary>
    public bool SelectedHasNamespace => SelectedIsOpenApi || SelectedIsWcf;

    /// <summary>A reference downloaded from a URL can be downloaded again (a WCF one is generated again from its address).</summary>
    public bool CanRefresh => _selected?.Info?.SourceUrl is not null || _selected?.Wcf?.Inputs.Any(ServiceReferenceService.IsUrl) == true;

    public IReadOnlyList<string> Kinds { get; } = ["OpenAPI", "gRPC", "WCF"];

    public string KindLabel
    {
        get => _kind switch { ServiceReferenceKinds.Grpc => "gRPC", WcfKind => "WCF", _ => "OpenAPI" };
        set
        {
            var kind = value switch { "gRPC" => ServiceReferenceKinds.Grpc, "WCF" => WcfKind, _ => ServiceReferenceKinds.OpenApi };
            if (SetField(ref _kind, kind, nameof(Kind)))
            {
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsOpenApi));
                OnPropertyChanged(nameof(IsGrpc));
                OnPropertyChanged(nameof(IsWcf));
                OnPropertyChanged(nameof(HasNamespace));
                OnPropertyChanged(nameof(NamespacePlaceholder));
                OnPropertyChanged(nameof(SourcePlaceholder));
                if (kind == WcfKind && _wcfStatus is null)
                {
                    _ = RunAsync("wcfStatus");
                }
            }
        }
    }

    public string Kind => _kind;

    public bool IsOpenApi => _kind == ServiceReferenceKinds.OpenApi;

    public bool IsGrpc => _kind == ServiceReferenceKinds.Grpc;

    public bool IsWcf => _kind == WcfKind;

    public bool HasNamespace => IsOpenApi || IsWcf;

    public string NamespacePlaceholder => IsWcf
        ? Localizer.Get("msg.webTools.theReferenceSName", "The reference's name")
        : Localizer.Get("serviceReferences.theProjectSRootNamespace", "The project's root namespace");

    public string SourcePlaceholder => IsWcf
        ? Localizer.Get("msg.webTools.wcfMetadataAddressOrAWsdlFile", "http://localhost:5000/Service.svc?wsdl, or a WSDL file")
        : Localizer.Get("serviceReferences.aFileOrHttpsSwaggerV1", "A file, or https://…/swagger/v1/swagger.json");

    // ------------------------------------------------------------------ WCF (M42)

    /// <summary>The folder under Connected Services, and the default namespace.</summary>
    public string WcfName
    {
        get => _wcfName;
        set => SetField(ref _wcfName, value ?? string.Empty);
    }

    public IReadOnlyList<string> CollectionTypes => WcfReferenceService.CollectionTypes;

    public IReadOnlyList<string> DictionaryTypes => WcfReferenceService.DictionaryTypes;

    public string CollectionType
    {
        get => _collectionType;
        set => SetField(ref _collectionType, value ?? WcfReferenceService.CollectionTypes[0]);
    }

    public string DictionaryType
    {
        get => _dictionaryType;
        set => SetField(ref _dictionaryType, value ?? WcfReferenceService.DictionaryTypes[0]);
    }

    public bool Sync
    {
        get => _sync;
        set => SetField(ref _sync, value);
    }

    public bool ReuseTypes
    {
        get => _reuseTypes;
        set => SetField(ref _reuseTypes, value);
    }

    public bool Internal
    {
        get => _internal;
        set => SetField(ref _internal, value);
    }

    public string EditCollectionType
    {
        get => _editCollectionType;
        set => SetField(ref _editCollectionType, value ?? WcfReferenceService.CollectionTypes[0]);
    }

    public string EditDictionaryType
    {
        get => _editDictionaryType;
        set => SetField(ref _editDictionaryType, value ?? WcfReferenceService.DictionaryTypes[0]);
    }

    public bool EditSync
    {
        get => _editSync;
        set => SetField(ref _editSync, value);
    }

    public bool EditReuseTypes
    {
        get => _editReuseTypes;
        set => SetField(ref _editReuseTypes, value);
    }

    public bool EditInternal
    {
        get => _editInternal;
        set => SetField(ref _editInternal, value);
    }

    /// <summary>dotnet-svcutil's state: whether it runs, local or global, its version.</summary>
    public WcfToolStatusResult? WcfStatus
    {
        get => _wcfStatus;
        set
        {
            if (SetField(ref _wcfStatus, value))
            {
                OnPropertyChanged(nameof(WcfToolMessage));
                OnPropertyChanged(nameof(NeedsWcfTool));
            }
        }
    }

    public bool NeedsWcfTool => _wcfStatus is { ToolAvailable: false };

    public string WcfToolMessage => _wcfStatus is null ? Localizer.Get("msg.serviceReferences.checkingSvcutil", "Checking dotnet-svcutil…")
        : _wcfStatus.ToolAvailable ? $"dotnet-svcutil {_wcfStatus.ToolVersion} ({_wcfStatus.ToolSource})"
        : (_wcfStatus.Message ?? Localizer.Get("msg.serviceReferences.noSvcutil", "dotnet-svcutil isn't available.")) + " " + Localizer.Get("msg.serviceReferences.installSvcutil", "Install adds it as a local tool of this solution.");

    /// <summary>Running CoreWCF services whose metadata answers, found by Discover.</summary>
    public ObservableCollection<DiscoveredService> Discovered { get; } = [];

    public bool HasDiscovered => Discovered.Count > 0;

    public DiscoveredService? SelectedDiscovered
    {
        get => _discovered;
        set
        {
            if (SetField(ref _discovered, value) && value is not null)
            {
                Source = value.MetadataUrl;
            }
        }
    }

    public void SetDiscovered(IReadOnlyList<DiscoveredService> services)
    {
        Discovered.Clear();
        foreach (var service in services)
        {
            Discovered.Add(service);
        }

        OnPropertyChanged(nameof(HasDiscovered));
        SelectedDiscovered = services.Count > 0 ? services[0] : null;
    }

    /// <summary>The add form as svcutil's options.</summary>
    public WcfReferenceParams WcfRequest() =>
        new(Project, WcfName.Trim(), Source.Trim(), Empty(Namespace), CollectionType, DictionaryType, Sync, ReuseTypes, Internal);

    /// <summary>The selected WCF reference's edited options (its address unchanged).</summary>
    public WcfReferenceParams? WcfUpdate() => _selected?.Wcf is { } wcf
        ? new(Project, wcf.Name, null, Empty(EditNamespace), EditCollectionType, EditDictionaryType, EditSync, EditReuseTypes, EditInternal)
        : null;

    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

    public ICommand ReloadCommand { get; }

    public ICommand InstallWcfCommand { get; }

    public ICommand DiscoverCommand { get; }

    public ICommand TestCommand { get; }

    public void SetReferences(ServiceReferencesResult result, IReadOnlyList<WcfReferenceInfo>? wcf = null)
    {
        var selected = _selected?.Location;
        References.Clear();
        foreach (var reference in result.References)
        {
            References.Add(new ServiceReferenceRow(reference));
        }

        foreach (var reference in wcf ?? [])
        {
            References.Add(new ServiceReferenceRow(null, reference));
        }

        Packages = result.Packages.Length == 0 ? "No code generation packages yet." : string.Join(", ", result.Packages.Select(p => $"{p.Id} {p.Version}".Trim()));
        Selected = References.FirstOrDefault(r => r.Location == selected) ?? References.FirstOrDefault();
        if (wcf is not null)
        {
            WcfName = WcfReferenceService.DefaultName(wcf.Select(r => r.Name));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    public ServiceReferenceRequest Request() =>
        new(Project, _kind, Source.Trim(), Namespace, ClassName, IsOpenApi ? CodeGenerator : null, null, IsGrpc ? GrpcServices : null);

    public async Task RunAsync(string action)
    {
        if (action == "add" && Source.Trim().Length == 0)
        {
            Error = IsWcf
                ? Localizer.Get("msg.serviceReferences.wcfNoAddress", "Enter the service's metadata address (…/Service.svc?wsdl), choose a WSDL file, or discover a running service.")
                : Localizer.Get("msg.serviceReferences.noDocument", "Choose the document: a file, or the URL of an OpenAPI document or a .proto file.");
            return;
        }

        if (action == "add" && IsWcf && WcfName.Trim().Length == 0)
        {
            Error = Localizer.Get("msg.serviceReferences.wcfNoName", "Name the reference: its folder under Connected Services.");
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
