using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using DaedalusStudio.Projects.Protocol;
using DaedalusStudio.Shell.Docking;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Data;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>A migration in the Entity Framework window.</summary>
public sealed class EfMigrationRow(EfMigrationItem item)
{
    public EfMigrationItem Item { get; } = item;

    public string Id => Item.Id;

    public string Name => Item.Name;

    public bool? Applied => Item.Applied;

    public string State => Applied switch
    {
        true => "Applied",
        false => "Pending",
        null => "Unknown",
    };

    public string Icon => Applied switch
    {
        true => "check",
        false => "circle",
        null => "question",
    };

    /// <summary>The migration's timestamp (its id's <c>yyyyMMddHHmmss</c> prefix), local time.</summary>
    public string Created =>
        Id.Length > 14 && DateTime.TryParseExact(Id[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created)
            ? created.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture)
            : string.Empty;

    public string AccessibleName => $"{Name}, {State}, {Created}";
}

/// <summary>A connection the window applies migrations to: the app's configuration (null) or a connection string.</summary>
public sealed record EfConnectionChoice(string Label, string? ConnectionString)
{
    public override string ToString() => Label;
}

/// <summary>
/// The Entity Framework window (M27a, ADR 0036): the solution's DbContexts, the selected one's migrations (applied or
/// pending on the chosen connection), and the EF commands. The shell runs the commands that ask the user something
/// (<paramref name="act"/>); this view model only lists.
/// </summary>
public sealed class EntityFrameworkViewModel : ObservableObject
{
    public const string ToolWindowId = "ef.explorer";

    private readonly EntityFrameworkService _ef;
    private readonly Func<string, EfMigrationRow?, Task> _act;
    private EfContextEntry? _context;
    private EfConnectionChoice? _connection;
    private EfMigrationRow? _selected;
    private string? _message;
    private string? _status;
    private string? _database;
    private string? _busy;
    private bool _canInstall;
    private int _generation;

    /// <param name="act">Runs a command that may ask the user: <c>add</c>, <c>remove</c>, <c>update</c>, <c>updateTo</c>, <c>script</c>, <c>bundle</c>, <c>model</c>, <c>optimize</c>, <c>scaffold</c>, <c>install</c>, <c>open</c>.</param>
    public EntityFrameworkViewModel(EntityFrameworkService ef, Func<string, EfMigrationRow?, Task> act)
    {
        _ef = ef;
        _act = act;
        RefreshCommand = new RelayCommand(() => _ = ReloadAsync(fromTool: true));
        AddCommand = Action("add");
        RemoveCommand = Action("remove");
        UpdateCommand = Action("update");
        UpdateToCommand = Action("updateTo");
        ScriptCommand = Action("script");
        BundleCommand = Action("bundle");
        ModelCommand = Action("model");
        OptimizeCommand = Action("optimize");
        ScaffoldCommand = Action("scaffold");
        InstallCommand = Action("install");
        OpenCommand = Action("open");
    }

    private RelayCommand Action(string name) => new(() => _ = _act(name, _selected));

    public ObservableCollection<EfContextEntry> Contexts { get; } = [];

    public ObservableCollection<EfConnectionChoice> Connections { get; } = [];

    public ObservableCollection<EfMigrationRow> Migrations { get; } = [];

    public EfContextEntry? Context
    {
        get => _context;
        set
        {
            if (SetField(ref _context, value))
            {
                OnPropertyChanged(nameof(HasContext));
                LoadConnections();
                _ = LoadMigrationsAsync();
            }
        }
    }

    public bool HasContext => _context is not null;

    public EfConnectionChoice? Connection
    {
        get => _connection;
        set
        {
            if (SetField(ref _connection, value) && value is not null)
            {
                _ = LoadMigrationsAsync();
            }
        }
    }

    public EfMigrationRow? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    public bool HasSelection => _selected is not null;

    /// <summary>"dotnet-ef 10.0.12 (local tool) · EF Core 10.0.12".</summary>
    public string? Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    /// <summary>The provider and the database of the selected context, from <c>dbcontext info</c>.</summary>
    public string? Database
    {
        get => _database;
        private set
        {
            if (SetField(ref _database, value))
            {
                OnPropertyChanged(nameof(HasDatabase));
            }
        }
    }

    public bool HasDatabase => _database is not null;

    public string? Message
    {
        get => _message;
        set
        {
            if (SetField(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => _message is not null;

    /// <summary>dotnet-ef is missing (or not restored): the window offers to install it.</summary>
    public bool CanInstall
    {
        get => _canInstall;
        private set => SetField(ref _canInstall, value);
    }

    /// <summary>What runs now ("Building Blogging…", "Applying migrations…"), or null.</summary>
    public string? Busy
    {
        get => _busy;
        set
        {
            if (SetField(ref _busy, value))
            {
                OnPropertyChanged(nameof(IsBusy));
            }
        }
    }

    public bool IsBusy => _busy is not null;

    public ICommand RefreshCommand { get; }

    public ICommand AddCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand UpdateCommand { get; }

    public ICommand UpdateToCommand { get; }

    public ICommand ScriptCommand { get; }

    public ICommand BundleCommand { get; }

    public ICommand ModelCommand { get; }

    public ICommand OptimizeCommand { get; }

    public ICommand ScaffoldCommand { get; }

    public ICommand InstallCommand { get; }

    public ICommand OpenCommand { get; }

    /// <summary>The target of the selected context and connection.</summary>
    public EfTarget? Target => _context is { } context ? _ef.Target(context, _connection?.ConnectionString) : null;

    /// <summary>The migrations as last loaded (for scripts and updates).</summary>
    public IReadOnlyList<EfMigrationItem> MigrationItems => [.. Migrations.Select(m => m.Item)];

    /// <summary>
    /// Reloads the contexts: Roslyn's at once, then (when <paramref name="fromTool"/>) <c>dotnet ef dbcontext list</c>'s,
    /// which builds. Keeps the selected context when it still exists.
    /// </summary>
    public Task ReloadAsync(bool fromTool)
    {
        // A reload in progress serves a second request of the same kind (the window opening while a command shows it).
        if (_reload is { IsCompleted: false } running && (_reloadFromTool || !fromTool))
        {
            return running;
        }

        _reloadFromTool = fromTool;
        return _reload = ReloadCoreAsync(fromTool);
    }

    private Task? _reload;
    private bool _reloadFromTool;

    private async Task ReloadCoreAsync(bool fromTool)
    {
        if (_ef.Projects.Count == 0)
        {
            Contexts.Clear();
            Migrations.Clear();
            Context = null;
            Status = null;
            Database = null;
            Message = "No project of this solution references Entity Framework Core.";
            return;
        }

        Message = null;
        var previous = _context?.FullName;
        try
        {
            Busy = fromTool ? "Building and listing the contexts…" : "Finding the contexts…";
            var contexts = await _ef.ContextsAsync(fromTool, CancellationToken.None).ConfigureAwait(true);
            Contexts.Clear();
            foreach (var context in contexts)
            {
                Contexts.Add(context);
            }

            if (Contexts.Count == 0)
            {
                Message = fromTool ? "No DbContext was found." : "No DbContext was found yet. Refresh asks dotnet ef.";
            }

            Context = Contexts.FirstOrDefault(c => c.FullName == previous) ?? Contexts.FirstOrDefault();
            await LoadStatusAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or EfBuildFailedException or OperationCanceledException)
        {
            Message = ex.Message;
        }
        finally
        {
            Busy = null;
        }
    }

    private async Task LoadStatusAsync()
    {
        var projects = _ef.Projects;
        var project = _context?.Project ?? (projects.Count > 0 ? projects[0].Path : null);
        if (project is null)
        {
            return;
        }

        try
        {
            var status = await _ef.StatusAsync(project, CancellationToken.None).ConfigureAwait(true);
            CanInstall = status.ToolVersion is null;
            Status = status.ToolVersion is { } version
                ? $"dotnet-ef {version} ({(status.ToolSource == "local" ? "local tool" : "global tool")})" + (status.EfCoreVersion is { } ef ? $" · EF Core {ef}" : string.Empty)
                : status.EfCoreVersion is { } efVersion ? $"EF Core {efVersion}" : null;
            if (status.Message is { } message)
            {
                Message = message;
            }
            else if (!status.HasDesignPackage)
            {
                Message = "The startup project doesn't reference Microsoft.EntityFrameworkCore.Design, which dotnet ef needs.";
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or OperationCanceledException)
        {
            Message = ex.Message;
        }
    }

    private void LoadConnections()
    {
        Connections.Clear();
        Connections.Add(new EfConnectionChoice("The app's configuration", null));
        if (_context is { } context)
        {
            foreach (var connection in ConnectionDiscovery.ForProject(_ef.StartupFor(context.Project)).Concat(ConnectionDiscovery.ForProject(context.Project))
                         .DistinctBy(c => (c.Name, c.Source, c.ConnectionString)))
            {
                Connections.Add(new EfConnectionChoice(connection.Label, connection.ConnectionString));
            }
        }

        _connection = Connections[0];
        OnPropertyChanged(nameof(Connection));
    }

    /// <summary>Lists the selected context's migrations on the chosen connection (builds the startup project first if needed).</summary>
    public async Task LoadMigrationsAsync()
    {
        var generation = ++_generation;
        if (Target is not { } target)
        {
            Migrations.Clear();
            Database = null;
            return;
        }

        try
        {
            Busy = "Listing the migrations…";
            var result = await _ef.MigrationsAsync(target, CancellationToken.None).ConfigureAwait(true);
            if (generation != _generation)
            {
                return;
            }

            var selected = _selected?.Id;
            Migrations.Clear();
            foreach (var migration in result.Migrations)
            {
                Migrations.Add(new EfMigrationRow(migration));
            }

            Selected = Migrations.FirstOrDefault(m => m.Id == selected);
            Message = result.ConnectionError is { } error ? "The database couldn't be reached: " + error
                : Migrations.Count == 0 ? "This context has no migrations yet. Add one to create its database." : null;
            if (CanInstall)
            {
                await LoadStatusAsync().ConfigureAwait(true);
            }

            try
            {
                var info = await _ef.ContextInfoAsync(target, CancellationToken.None).ConfigureAwait(true);
                if (generation == _generation)
                {
                    Database = $"{info.ProviderName} · {info.DataSource}" + (info.DatabaseName is { Length: > 0 } name && name != "main" ? $" / {name}" : string.Empty);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Rpc.JsonRpcRemoteException)
            {
                Database = null;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or EfBuildFailedException or OperationCanceledException)
        {
            if (generation == _generation)
            {
                Migrations.Clear();
                Message = ex.Message;
                if (ex is Rpc.JsonRpcRemoteException { Code: ProjectErrorCodes.ToolMissing })
                {
                    CanInstall = true;
                }
            }
        }
        finally
        {
            if (generation == _generation)
            {
                Busy = null;
            }
        }
    }
}

// ------------------------------------------------------------------ the model view

/// <summary>The model view of a context (M27a): its diagram and its debug view.</summary>
public sealed class EfModelViewModel : ObservableObject
{
    private EfModelResult? _model;
    private string? _message = "Reading the model…";

    public EfModelViewModel(string context, Editor.IEditorServices services, EditorFont font, Func<Task> refresh)
    {
        Context = context;
        Services = services;
        Font = font;
        RefreshCommand = new RelayCommand(() => _ = refresh());
    }

    public string Context { get; }

    public Editor.IEditorServices Services { get; }

    public EditorFont Font { get; }

    public ICommand RefreshCommand { get; }

    public EfModelResult? Model
    {
        get => _model;
        set
        {
            if (SetField(ref _model, value))
            {
                OnPropertyChanged(nameof(Summary));
                ModelChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string Summary => _model is { } model
        ? $"{model.Context} · {model.Provider} · {model.Entities.Length} entity types" + (model.ProductVersion is { } version ? $" · EF Core {version}" : string.Empty)
        : Context;

    public string? Message
    {
        get => _message;
        set
        {
            if (SetField(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => _message is not null;

    public event EventHandler? ModelChanged;
}

public sealed class EfModelDocument : ShellDocument
{
    public EfModelDocument(EfModelViewModel viewModel)
    {
        ViewModel = viewModel;
        Id = "ef.model:" + viewModel.Context;
        Title = viewModel.Context[(viewModel.Context.LastIndexOf('.') + 1)..] + " (Model)";
    }

    public override string Kind => "efModel";

    public EfModelViewModel ViewModel { get; }
}

// ------------------------------------------------------------------ reverse engineering

/// <summary>Reverse engineering (<c>dbcontext scaffold</c>): the connection, the provider, where the files go, the tables and the options.</summary>
public sealed class EfScaffoldViewModel : ObservableObject
{
    private readonly Func<EfScaffoldViewModel, Task> _run;
    private string? _project;
    private string _connectionString = string.Empty;
    private EfConnectionChoice? _knownConnection;
    private string? _provider;
    private string _outputDir = "Models";
    private string _contextName = string.Empty;
    private string _tables = string.Empty;
    private string _schemas = string.Empty;
    private bool _dataAnnotations;
    private bool _noOnConfiguring = true;
    private bool _useDatabaseNames;
    private bool _noPluralize;
    private bool _force;
    private bool _isBusy;
    private string? _error;
    private string? _result;

    public EfScaffoldViewModel(IReadOnlyList<string> projects, Func<string, IReadOnlyList<EfConnectionChoice>> connections, Func<EfScaffoldViewModel, Task> run)
    {
        Projects = [.. projects.Select(p => new EfProjectChoice(p))];
        _run = run;
        ConnectionsOf = connections;
        _project = projects.Count > 0 ? projects[0] : null;
        RunCommand = new RelayCommand(() => _ = RunAsync());
        RefreshConnections();
    }

    public IReadOnlyList<EfProjectChoice> Projects { get; }

    /// <summary>The chosen project, for the picker (its name; the path in the tooltip).</summary>
    public EfProjectChoice? SelectedProject
    {
        get => Projects.FirstOrDefault(p => p.Path == _project);
        set => Project = value?.Path;
    }

    public IReadOnlyList<string> Providers { get; } =
    [
        "Microsoft.EntityFrameworkCore.SqlServer",
        "Npgsql.EntityFrameworkCore.PostgreSQL",
        "Microsoft.EntityFrameworkCore.Sqlite",
        "Pomelo.EntityFrameworkCore.MySql",
    ];

    private Func<string, IReadOnlyList<EfConnectionChoice>> ConnectionsOf { get; }

    public ObservableCollection<EfConnectionChoice> KnownConnections { get; } = [];

    public string? Project
    {
        get => _project;
        set
        {
            if (SetField(ref _project, value))
            {
                OnPropertyChanged(nameof(ProjectName));
                OnPropertyChanged(nameof(SelectedProject));
                RefreshConnections();
            }
        }
    }

    public string ProjectName => _project is null ? string.Empty : Path.GetFileNameWithoutExtension(_project);

    /// <summary>A connection string the project already has, copied into <see cref="ConnectionString"/> when picked.</summary>
    public EfConnectionChoice? KnownConnection
    {
        get => _knownConnection;
        set
        {
            if (SetField(ref _knownConnection, value) && value?.ConnectionString is { } connection)
            {
                ConnectionString = connection;
                if (DatabaseEngines.EfProvider(DatabaseEngines.Guess(connection)) is { } provider)
                {
                    Provider = provider;
                }
            }
        }
    }

    public string ConnectionString
    {
        get => _connectionString;
        set => SetField(ref _connectionString, value ?? string.Empty);
    }

    public string? Provider
    {
        get => _provider;
        set => SetField(ref _provider, value);
    }

    public string OutputDir
    {
        get => _outputDir;
        set => SetField(ref _outputDir, value ?? string.Empty);
    }

    public string ContextName
    {
        get => _contextName;
        set => SetField(ref _contextName, value ?? string.Empty);
    }

    /// <summary>Comma-separated table names; empty: every table.</summary>
    public string Tables
    {
        get => _tables;
        set => SetField(ref _tables, value ?? string.Empty);
    }

    public string Schemas
    {
        get => _schemas;
        set => SetField(ref _schemas, value ?? string.Empty);
    }

    public bool DataAnnotations
    {
        get => _dataAnnotations;
        set => SetField(ref _dataAnnotations, value);
    }

    public bool NoOnConfiguring
    {
        get => _noOnConfiguring;
        set => SetField(ref _noOnConfiguring, value);
    }

    public bool UseDatabaseNames
    {
        get => _useDatabaseNames;
        set => SetField(ref _useDatabaseNames, value);
    }

    public bool NoPluralize
    {
        get => _noPluralize;
        set => SetField(ref _noPluralize, value);
    }

    public bool Force
    {
        get => _force;
        set => SetField(ref _force, value);
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

    public ICommand RunCommand { get; }

    public static string[] Split(string list) => list.Split([',', ';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public EfScaffoldParams Parameters() => new(
        _project!,
        null,
        ConnectionString.Trim(),
        _provider!,
        OutputDir: OutputDir.Trim() is { Length: > 0 } output ? output : null,
        ContextName: ContextName.Trim() is { Length: > 0 } name ? name : null,
        Tables: Split(Tables),
        Schemas: Split(Schemas),
        DataAnnotations: DataAnnotations,
        NoOnConfiguring: NoOnConfiguring,
        UseDatabaseNames: UseDatabaseNames,
        NoPluralize: NoPluralize,
        Force: Force);

    public async Task RunAsync()
    {
        if (_project is null || ConnectionString.Trim().Length == 0 || _provider is null)
        {
            Error = "Choose the project, a connection string and the provider.";
            return;
        }

        if (ContextName.Trim() is { Length: > 0 } name && !(char.IsLetter(name[0]) || name[0] == '_'))
        {
            Error = "The context's name must be a C# identifier.";
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

    private void RefreshConnections()
    {
        KnownConnections.Clear();
        if (_project is { } project)
        {
            foreach (var connection in ConnectionsOf(project))
            {
                KnownConnections.Add(connection);
            }
        }
    }
}

/// <summary>A project in a picker: its name, and its path.</summary>
public sealed record EfProjectChoice(string Path)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

public sealed class EfScaffoldDocument : ShellDocument
{
    public const string DocumentId = "ef.scaffold";

    public EfScaffoldDocument(EfScaffoldViewModel viewModel)
    {
        ViewModel = viewModel;
        Id = DocumentId;
        Title = "Reverse Engineer";
    }

    public override string Kind => "efScaffold";

    public EfScaffoldViewModel ViewModel { get; }
}
