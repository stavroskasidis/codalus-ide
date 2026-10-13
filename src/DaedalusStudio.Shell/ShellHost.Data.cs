using System.Text.RegularExpressions;
using DaedalusStudio.Data.Protocol;
using DaedalusStudio.LanguageServices.Protocol;
using DaedalusStudio.Shell.Editor;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Workbench.Data;
using DaedalusStudio.Workbench.Languages;
using DaedalusStudio.Workbench.StatusBar;
using DaedalusStudio.Workbench.Localization;

namespace DaedalusStudio.Shell;

/// <summary>
/// The database explorer, table data and the query editor (M27b, ADR 0037), and the SQL language service (ADR 0038).
/// The work runs in <c>daedalus-datahost</c>; this is the UI.
/// </summary>
public sealed partial class ShellHost
{
    public const string SqlLanguageId = "sql";
    public const string SqlConnectionStatusItem = "daedalus.sqlConnection";
    public const string MaxRowsSetting = "data.maxRows";

    /// <summary>The connection (and database) of each SQL editor, by file path.</summary>
    private readonly Dictionary<string, (string Id, string? Database)> _queryConnections = new(SolutionPaths.Comparer);
    private CancellationTokenSource? _queryCancellation;
    private string? _lastSqlEditor;
    private int _queryNumber;
    private HashSet<string> _treeConnections = [];

    public DataService Data { get; private set; } = null!;

    public DatabaseExplorerViewModel DbExplorer { get; private set; } = null!;

    public QueryResultsViewModel QueryResults { get; private set; } = null!;

    private void InitializeData()
    {
        Data = new DataService(() => DataHostLaunch.Resolve(), Paths.State, Secrets);
        Workbench.LanguageServers.Register(new LanguageServiceDescriptor(SqlLanguageId, "SQL", [], null, DataHostLaunch.ResolveLanguageServer) { Languages = [SqlLanguageId] });
        DbExplorer = new DatabaseExplorerViewModel(id => CommandFor(id));
        QueryResults = new QueryResultsViewModel(id => CommandFor(id), row => _ = OpenQueryAsync(null, row.Entry.Text)) { Font = EditorFont };
        QueryResults.LoadHistory(Data.History);
        Data.Log += (_, line) => DataLog.AppendLine(line);
        Data.Changed += (_, _) => Ui.Post(SyncDataTree);
        Data.HostCrashed += (_, restarting) => Ui.Post(() => Workbench.Notifications.Warning(restarting
            ? "The data host stopped unexpectedly and is restarting; running queries were canceled."
            : "The data host keeps crashing and was stopped. Reopen the Database Explorer to try again."));
        DbExplorer.SelectionChanged += (_, node) => Workbench.Context.Set("dataNodeKind", node?.Kind);
        Solutions.SolutionChanged += (_, _) => Ui.Post(DiscoverConnections);
        Solutions.ProjectChanged += (_, _) => Ui.Post(DiscoverConnections);
        SyncDataTree();
    }

    public Workbench.Output.OutputChannel DataLog => Workbench.Output.GetOrCreate("daedalus.data", "Data");

    /// <summary>The solution's connection strings: appsettings, user secrets, Aspire AppHost resources.</summary>
    private void DiscoverConnections()
    {
        if (Solutions.Solution is null || Solutions.Solution.Trusted != true)
        {
            Data.SetDiscovered([]);
            return;
        }

        var found = new List<DataConnection>();
        foreach (var project in Solutions.Projects.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var connection in ConnectionDiscovery.ForProject(project.Path))
            {
                if (connection.Engine is not null)
                {
                    found.Add(new DataConnection($"{project.Name}:{connection.Source}:{connection.Name}", $"{project.Name}: {connection.Name}", connection.Engine, AbsoluteSqlite(connection, project.Path), Source: connection.Source, Project: project.Path));
                }
            }

            found.AddRange(AspireResources.Discover(project.Path, project.Name));
        }

        if (found.Select(c => c.Id).Order(StringComparer.Ordinal).SequenceEqual(Data.DiscoveredConnections.Select(c => c.Id).Order(StringComparer.Ordinal)))
        {
            return;
        }

        Data.SetDiscovered(found);
    }

    /// <summary>A SQLite file relative to the project (what the app sees when it runs there) made absolute.</summary>
    private static string AbsoluteSqlite(DiscoveredConnection connection, string project)
    {
        if (connection.Engine != DataEngines.Sqlite)
        {
            return connection.ConnectionString;
        }

        var keys = DatabaseEngines.Parse(connection.ConnectionString);
        var source = keys.GetValueOrDefault("data source") ?? keys.GetValueOrDefault("datasource") ?? keys.GetValueOrDefault("filename");
        if (source is null || source == ":memory:" || Path.IsPathRooted(source))
        {
            return connection.ConnectionString;
        }

        return "Data Source=" + Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, source));
    }

    // ------------------------------------------------------------------ the explorer tree

    /// <summary>Rebuilds the roots when the set of connections changed (keeps expanded nodes otherwise).</summary>
    private void SyncDataTree()
    {
        var ids = Data.Connections.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.SetEquals(_treeConnections) && DbExplorer.Roots.Count > 0 || ids.Count == 0 && _treeConnections.Count == 0 && DbExplorer.HasMessage)
        {
            return;
        }

        _treeConnections = ids;
        DbExplorer.Roots.Clear();
        foreach (var connection in Data.UserConnections)
        {
            DbExplorer.Roots.Add(ConnectionNode(connection));
        }

        if (Data.DiscoveredConnections.Count > 0)
        {
            var group = new DbNodeViewModel(DbNodeKinds.Group, "From the solution", "solution") { Tooltip = "Connection strings in the solution's appsettings files, user secrets and Aspire AppHost" };
            foreach (var connection in Data.DiscoveredConnections)
            {
                group.Children.Add(ConnectionNode(connection));
            }

            group.IsExpanded = true;
            DbExplorer.Roots.Add(group);
        }

        DbExplorer.Message = DbExplorer.Roots.Count == 0 ? "No connections yet. Add one, or open a solution whose appsettings files or user secrets have connection strings." : null;
    }

    private DbNodeViewModel ConnectionNode(DataConnection connection) =>
        new(DbNodeKinds.Connection, connection.Name, "database", LoadConnectionAsync)
        {
            ConnectionId = connection.Id,
            Description = DatabaseEngines.DisplayName(connection.Engine) + (connection.Source is { } source ? " · " + source : string.Empty),
            Tooltip = ConnectionDiscovery.Mask(connection.ConnectionString),
        };

    private async Task LoadConnectionAsync(DbNodeViewModel node)
    {
        var connection = Data.Find(node.ConnectionId!) ?? throw new InvalidOperationException("The connection no longer exists.");
        var info = await EnsureConnectedAsync(connection).ConfigureAwait(true);
        node.Description = DatabaseEngines.DisplayName(connection.Engine) + " " + info.ServerVersion + (connection.Source is { } source ? " · " + source : string.Empty);
        node.Children.Clear();
        if (connection.Engine == DataEngines.Sqlite)
        {
            await FillDatabaseAsync(node, connection.Id, null).ConfigureAwait(true);
            return;
        }

        var databases = await Data.DatabasesAsync(connection.Id, CancellationToken.None).ConfigureAwait(true);
        foreach (var database in databases.Databases.OrderBy(d => d == databases.Current ? 0 : 1).ThenBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var isCurrent = database == databases.Current;
            var child = new DbNodeViewModel(DbNodeKinds.Database, database, "database", n => FillDatabaseAsync(n, connection.Id, isCurrent ? null : database))
            {
                ConnectionId = connection.Id,
                Database = isCurrent ? null : database,
                Description = isCurrent ? "current" : null,
            };
            node.Children.Add(child);
            if (isCurrent)
            {
                child.IsExpanded = true;
            }
        }
    }

    /// <summary>Tables, Views, Procedures and Functions of a database.</summary>
    private async Task FillDatabaseAsync(DbNodeViewModel node, string id, string? database)
    {
        var schema = await Data.SchemaAsync(id, database, refresh: false, CancellationToken.None).ConfigureAwait(true);
        node.Children.Clear();
        foreach (var (kind, title, icon) in new[] { (DbObjectKinds.Table, "Tables", "table"), (DbObjectKinds.View, "Views", "view"), (DbObjectKinds.Procedure, "Procedures", "procedure"), (DbObjectKinds.Function, "Functions", "function") })
        {
            var objects = schema.Objects.Where(o => o.Kind == kind).ToList();
            if (objects.Count == 0 && kind is DbObjectKinds.Procedure or DbObjectKinds.Function)
            {
                continue;
            }

            var folder = new DbNodeViewModel(DbNodeKinds.Folder, title, "folder") { ConnectionId = id, Database = database, Description = objects.Count.ToString(System.Globalization.CultureInfo.CurrentCulture) };
            foreach (var o in objects)
            {
                folder.Children.Add(ObjectNode(id, database, o, schema.DefaultSchema, icon));
            }

            if (kind == DbObjectKinds.Table)
            {
                folder.IsExpanded = true;
            }

            node.Children.Add(folder);
        }

        _ = SendSqlContextsAsync();
    }

    private static DbNodeViewModel ObjectNode(string id, string? database, DbObjectInfo o, string? defaultSchema, string icon)
    {
        var label = o.Schema is { Length: > 0 } schema && !schema.Equals(defaultSchema, StringComparison.OrdinalIgnoreCase) ? schema + "." + o.Name : o.Name;
        var node = new DbNodeViewModel(o.Kind, label, icon) { ConnectionId = id, Database = database, Object = o, Tooltip = o.QualifiedName };
        var foreign = o.Keys.Where(k => k.Kind == "foreign").SelectMany(k => k.Columns).ToHashSet(StringComparer.Ordinal);
        foreach (var column in o.Columns)
        {
            node.Children.Add(new DbNodeViewModel(DbNodeKinds.Column, column.Name, column.IsKey ? "key" : "column")
            {
                ConnectionId = id,
                Database = database,
                Description = column.Type + (column.Nullable ? ", null" : ", not null") + (column.IsIdentity ? ", identity" : string.Empty) + (foreign.Contains(column.Name) ? ", FK" : string.Empty),
            });
        }

        if (o.Keys.Length > 0)
        {
            var keys = new DbNodeViewModel(DbNodeKinds.Folder, "Keys", "folder");
            foreach (var key in o.Keys)
            {
                keys.Children.Add(new DbNodeViewModel(DbNodeKinds.Key, key.Name, "key")
                {
                    Description = key.Kind + " (" + string.Join(", ", key.Columns) + ")" + (key.ReferencedTable is { } table ? " → " + table + (key.ReferencedColumns is { Length: > 0 } columns ? "(" + string.Join(", ", columns) + ")" : string.Empty) : string.Empty),
                });
            }

            node.Children.Add(keys);
        }

        if (o.Indexes.Length > 0)
        {
            var indexes = new DbNodeViewModel(DbNodeKinds.Folder, "Indexes", "folder");
            foreach (var index in o.Indexes)
            {
                indexes.Children.Add(new DbNodeViewModel(DbNodeKinds.Index, index.Name, "list") { Description = (index.IsUnique ? "unique (" : "(") + string.Join(", ", index.Columns) + ")" });
            }

            node.Children.Add(indexes);
        }

        return node;
    }

    /// <summary>Connects, asking for the password when it isn't saved (kept for the session).</summary>
    public async Task<ConnectResult> EnsureConnectedAsync(DataConnection connection)
    {
        while (true)
        {
            try
            {
                return await Data.ConnectAsync(connection, CancellationToken.None).ConfigureAwait(true);
            }
            catch (DataPasswordRequiredException)
            {
                var answer = await Dialogs.ShowAsync(new DialogRequest("Connect", $"The password for {connection.Label}:", ["Connect", "Cancel"]) { Input = string.Empty, IsPassword = true }).ConfigureAwait(true);
                if (answer.Button != "Connect" || answer.Input is null)
                {
                    throw new OperationCanceledException();
                }

                Data.RememberPassword(connection.Id, answer.Input);
            }
        }
    }

    /// <summary>Double-click or Enter in the explorer: a table's or view's data; a routine's call in a new query.</summary>
    public void ActivateDataNode(DbNodeViewModel node)
    {
        switch (node.Kind)
        {
            case DbNodeKinds.Table or DbNodeKinds.View:
                _ = ViewTableDataAsync(node);
                break;
            case DbNodeKinds.Procedure or DbNodeKinds.Function when node.Object is { } routine:
                _ = OpenQueryAsync(node, Data.Find(node.ConnectionId!) is { Engine: DataEngines.SqlServer }
                    ? $"EXEC {routine.QualifiedName};\n"
                    : $"SELECT * FROM {routine.QualifiedName}();\n");
                break;
        }
    }

    // ------------------------------------------------------------------ commands

    private DatabaseExplorerViewModel OpenDatabaseExplorer()
    {
        DiscoverConnections();
        SyncDataTree();
        return DbExplorer;
    }

    /// <summary>
    /// Reverse Engineer with EF Core from the explorer: the EF scaffold form with the connection string, the provider
    /// and the selected tables filled in (ADR 0036).
    /// </summary>
    public async Task ReverseEngineerFromDatabaseAsync()
    {
        var node = DbExplorer.Selected;
        if (node?.ConnectionId is not { } id || Data.Find(id) is not { } connection)
        {
            ShowMessage("Select a connection, a database or tables in the Database Explorer.");
            return;
        }

        if (OpenReverseEngineer() is not { } document)
        {
            return;
        }

        var connectionString = await Data.ConnectionStringAsync(connection, CancellationToken.None).ConfigureAwait(true);
        var form = document.ViewModel;
        form.ConnectionString = node.Database is { } database && connection.Engine != DataEngines.Sqlite ? WithDatabase(connectionString, connection.Engine, database) : connectionString;
        form.Provider = DatabaseEngines.EfProvider(connection.Engine);
        form.Tables = node.Kind switch
        {
            DbNodeKinds.Table or DbNodeKinds.View when node.Object is { } o => o.QualifiedName,
            DbNodeKinds.Folder => string.Join(", ", node.Children.Where(c => c.Object is not null).Select(c => c.Object!.QualifiedName)),
            _ => string.Empty,
        };
    }

    private static string WithDatabase(string connectionString, string engine, string database)
    {
        var builder = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };
        foreach (var key in new[] { "Database", "Initial Catalog" })
        {
            builder.Remove(key);
        }

        builder[engine == DataEngines.SqlServer ? "Initial Catalog" : "Database"] = database;
        return builder.ConnectionString;
    }

    public void ShowDatabaseExplorer()
    {
        DiscoverConnections();
        Layout.ShowToolWindow(DatabaseExplorerViewModel.ToolWindowId, focus: true);
    }

    public ConnectionFormDocument OpenConnectionForm(DataConnection? existing = null)
    {
        var form = new ConnectionFormViewModel(existing?.Id, TestConnectionAsync, SaveConnectionAsync);
        if (existing is not null)
        {
            form.Engine = existing.Engine;
            form.Name = existing.Name;
            form.UseConnectionString = true;
            form.ConnectionString = existing.ConnectionString;
            form.SavePassword = existing.SavePassword;
        }

        var document = Layout.FindDocument<ConnectionFormDocument>("data.connection:" + form.Id) ?? new ConnectionFormDocument(form);
        Layout.OpenDocument(document, focus: true);
        return document;
    }

    private async Task<string> TestConnectionAsync(ConnectionFormViewModel form)
    {
        var connectionString = form.UsesPassword && form.Password.Length > 0 ? DataService.WithPassword(form.BuildConnectionString(), form.Password) : form.BuildConnectionString();
        try
        {
            var result = await Data.TestAsync(form.Engine, connectionString, CancellationToken.None).ConfigureAwait(true);
            return result.Succeeded ? $"Connected: {DatabaseEngines.DisplayName(form.Engine)} {result.ServerVersion}." : "Couldn't connect: " + result.Error;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException)
        {
            return "Couldn't connect: " + ex.Message;
        }
    }

    private async Task SaveConnectionAsync(ConnectionFormViewModel form)
    {
        var connection = new DataConnection(form.Id, form.DefaultName(), form.Engine, form.BuildConnectionString(), form.SavePassword);
        await Data.SaveAsync(connection, form.Password.Length > 0 ? form.Password : null, CancellationToken.None).ConfigureAwait(true);
        _treeConnections.Clear();
        SyncDataTree();
        if (Layout.FindDocument<ConnectionFormDocument>("data.connection:" + form.Id) is { } document)
        {
            Layout.CloseDocument(document);
        }

        Layout.ShowToolWindow(DatabaseExplorerViewModel.ToolWindowId, focus: true);
        StatusBar.ShowMessage(Localizer.Format("msg.data.savedTheConnection", "Saved the connection {0}.", connection.Name), TimeSpan.FromSeconds(4));
    }

    private DataConnection? SelectedConnection() =>
        DbExplorer.Selected?.ConnectionId is { } id ? Data.Find(id) : null;

    public async Task RemoveConnectionAsync()
    {
        if (SelectedConnection() is not { IsDiscovered: false } connection)
        {
            ShowMessage("Select one of your connections to remove (the solution's come from its files).");
            return;
        }

        if (await Dialogs.ConfirmAsync(Localizer.Get("msg.data.removeConnection", "Remove Connection"), Localizer.Format("msg.data.removeTheConnectionItsSavedPassword", "Remove the connection {0}? Its saved password is deleted too.", connection.Name), Localizer.Get("msg.data.remove", "Remove"), danger: true).ConfigureAwait(true))
        {
            await Data.RemoveAsync(connection.Id, CancellationToken.None).ConfigureAwait(true);
        }
    }

    public void EditConnection()
    {
        if (SelectedConnection() is not { IsDiscovered: false } connection)
        {
            ShowMessage("Select one of your connections to edit.");
            return;
        }

        OpenConnectionForm(connection);
    }

    /// <summary>Refresh: the selected connection (schemas again), or every connection.</summary>
    public async Task RefreshDataAsync()
    {
        DiscoverConnections();
        if (DbExplorer.ConnectionOf(DbExplorer.Selected) is { } node)
        {
            await Data.DisconnectAsync(node.ConnectionId!).ConfigureAwait(true);
            node.Reset();
            return;
        }

        _treeConnections.Clear();
        SyncDataTree();
    }

    public async Task DisconnectDataAsync()
    {
        if (DbExplorer.ConnectionOf(DbExplorer.Selected) is { } node)
        {
            await Data.DisconnectAsync(node.ConnectionId!).ConfigureAwait(true);
            node.IsExpanded = false;
            node.Reset();
        }
    }

    // ------------------------------------------------------------------ table data

    public async Task<TableDataDocument?> ViewTableDataAsync(DbNodeViewModel? node = null)
    {
        node ??= DbExplorer.Selected;
        if (node?.Table is not { } table || node.ConnectionId is not { } id || Data.Find(id) is not { } connection)
        {
            ShowMessage("Select a table or a view in the Database Explorer.");
            return null;
        }

        var vm = new TableDataViewModel(Data, id, node.Database, table, connection.Name, TableActionAsync)
        {
            PageSize = Math.Clamp(Workbench.Settings.GetInt32("data.pageSize", TableDataViewModel.DefaultPageSize), 10, 10_000),
        };
        var document = Layout.FindDocument<TableDataDocument>($"data.table:{id}:{node.Database}:{vm.Title}");
        if (document is null)
        {
            document = new TableDataDocument(vm);
            var created = document;
            document.CloseRequested += async (_, _) =>
            {
                if (await Dialogs.ConfirmAsync(Localizer.Get("msg.data.discardChanges", "Discard Changes"), Localizer.Format("msg.data.hasChangeSThatArentCommitted", "{0} has {1} change(s) that aren't committed. Discard them?", created.Title, created.ViewModel.PendingChanges), Localizer.Get("msg.data.discard", "Discard"), danger: true).ConfigureAwait(true))
                {
                    created.CloseConfirmed();
                }
            };
            Layout.OpenDocument(document, focus: true);
            await vm.LoadAsync().ConfigureAwait(true);
        }
        else
        {
            Layout.OpenDocument(document, focus: true);
        }

        return document;
    }

    private async Task TableActionAsync(TableDataViewModel table, string action)
    {
        if (Window is null)
        {
            return;
        }

        try
        {
            if (action == "export")
            {
                if (await Window.SaveFileAsync("Export Table", table.Table.Name + ".csv", "CSV or JSON", ["*.csv", "*.json"]).ConfigureAwait(true) is not { } path)
                {
                    return;
                }

                var result = await Data.ExportAsync(new ExportParams(table.ConnectionId, table.Database, Format(path), path, table.Table), CancellationToken.None).ConfigureAwait(true);
                StatusBar.ShowMessage(Localizer.Format("msg.data.exportedRowSTo", "Exported {0:N0} row(s) to {1}.", result.Rows, Path.GetFileName(path)), TimeSpan.FromSeconds(6));
            }
            else if (action == "import")
            {
                if (await Window.PickFileAsync("Import Rows", "CSV or JSON", ["*.csv", "*.json"]).ConfigureAwait(true) is not { } path)
                {
                    return;
                }

                var result = await Data.ImportAsync(new ImportParams(table.ConnectionId, table.Database, table.Table, Format(path), path), CancellationToken.None).ConfigureAwait(true);
                table.Message = $"Imported {result.Rows:N0} row(s) from {Path.GetFileName(path)}.";
                await table.LoadAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is Rpc.JsonRpcRemoteException or InvalidOperationException or Rpc.JsonRpcConnectionLostException)
        {
            table.Message = ex.Message;
        }

        static string Format(string path) => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "json" : "csv";
    }

    // ------------------------------------------------------------------ the query editor

    /// <summary>
    /// A new query editor on the selected connection (or <paramref name="node"/>'s), with <paramref name="text"/>: an
    /// unsaved <c>.sql</c> file in the state folder's queries.
    /// </summary>
    public async Task<CodeEditorDocument?> OpenQueryAsync(DbNodeViewModel? node = null, string? text = null)
    {
        node ??= DbExplorer.Selected;
        var connection = node?.ConnectionId is { } id ? Data.Find(id) : null;
        var folder = Path.Combine(Paths.State, "queries");
        string path;
        do
        {
            path = Path.Combine(folder, $"Query{++_queryNumber}.sql");
        }
        while (File.Exists(path) || Editors.Any(e => SolutionPaths.Comparer.Equals(e.Model.Path, path)));

        if (text is null && node?.Table is { } table && connection is not null)
        {
            text = connection.Engine == DataEngines.SqlServer
                ? $"SELECT TOP (100) * FROM {QuoteFor(connection.Engine, table)};\n"
                : $"SELECT * FROM {QuoteFor(connection.Engine, table)} LIMIT 100;\n";
        }

        if (connection is not null)
        {
            _queryConnections[path] = (connection.Id, node!.Database);
        }

        var editor = await OpenFileAsync(path, preview: false, at: null, initialContent: text ?? string.Empty).ConfigureAwait(true);
        UpdateSqlConnectionStatus();
        _ = SendSqlContextAsync(path);
        return editor;
    }

    private static string QuoteFor(string engine, DbTableRef table)
    {
        string Q(string name) => engine switch
        {
            DataEngines.SqlServer => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]",
            DataEngines.MySql => "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`",
            _ => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
        };
        return table.Schema is { Length: > 0 } schema ? Q(schema) + "." + Q(table.Name) : Q(table.Name);
    }

    /// <summary>The SQL editor commands act on: the active one, else the last active one.</summary>
    private CodeEditorDocument? SqlEditor =>
        ActiveEditor is { Model.LanguageId: SqlLanguageId } active ? active
        : Editors.FirstOrDefault(e => e.Model.LanguageId == SqlLanguageId && SolutionPaths.Comparer.Equals(e.Model.Path, _lastSqlEditor));

    /// <summary>Chooses the connection (and for server engines, the database) of the SQL editor.</summary>
    public async Task<bool> ChooseQueryConnectionAsync()
    {
        if (SqlEditor is not { } editor)
        {
            ShowMessage("Open a .sql file (or New Query) first.");
            return false;
        }

        var items = Data.Connections.Select(c => new QuickPickItem(c.Label) { Tag = c.Id, Detail = c.Description }).ToList();
        if (items.Count == 0)
        {
            ShowMessage("There are no connections: add one in the Database Explorer.");
            return false;
        }

        if (await Palette.ShowAsync(items, "Connection for " + Path.GetFileName(editor.Model.Path)).ConfigureAwait(true) is not { Tag: string id })
        {
            return false;
        }

        _queryConnections[editor.Model.Path] = (id, null);
        UpdateSqlConnectionStatus();
        await SendSqlContextAsync(editor.Model.Path).ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// Runs the SQL editor's selection, its statement at the caret (<paramref name="statementOnly"/>), or all of it,
    /// with a plan (<see cref="PlanKinds"/>), and shows the outcome in the Query Results window.
    /// </summary>
    public async Task<ExecuteResult?> ExecuteQueryAsync(bool statementOnly = false, string plan = PlanKinds.None)
    {
        if (SqlEditor is not { Controller: { } controller } editor)
        {
            ShowMessage("Open a .sql file (or New Query) to run SQL.");
            return null;
        }

        if (!_queryConnections.ContainsKey(editor.Model.Path) && !await ChooseQueryConnectionAsync().ConfigureAwait(true))
        {
            return null;
        }

        var (id, database) = _queryConnections[editor.Model.Path];
        var text = editor.Model.Text.Text;
        var selection = controller.Editor.SelectionLength > 0 ? controller.Editor.SelectedText : null;
        var query = selection ?? (statementOnly || plan != PlanKinds.None ? StatementAt(text, controller.Editor.CaretOffset) : text);
        if (query.Trim().Length == 0)
        {
            ShowMessage("There is nothing to run.");
            return null;
        }

        Layout.ShowToolWindow(QueryResultsViewModel.ToolWindowId, focus: false);
        _queryCancellation?.Cancel();
        var cancellation = _queryCancellation = new CancellationTokenSource();
        QueryResults.IsRunning = true;
        QueryResults.Status = "Running…";
        Workbench.Context.Set("queryRunning", true);
        try
        {
            if (Data.Find(id) is { } connection && !Data.IsConnected(id))
            {
                await EnsureConnectedAsync(connection).ConfigureAwait(true);
            }

            var maxRows = Math.Clamp(Workbench.Settings.GetInt32(MaxRowsSetting, 10_000), 1, 1_000_000);
            var result = await Data.ExecuteAsync(new ExecuteParams(id, query, database, editor.Model.Path, plan, maxRows), cancellation.Token).ConfigureAwait(true);
            QueryResults.Show(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            QueryResults.ShowError("The query was canceled.");
            return null;
        }
        catch (Exception ex) when (ex is Rpc.JsonRpcRemoteException or InvalidOperationException or Rpc.JsonRpcConnectionLostException or DataPasswordRequiredException)
        {
            QueryResults.ShowError(ex.Message);
            return null;
        }
        finally
        {
            QueryResults.IsRunning = false;
            QueryResults.LoadHistory(Data.History);
            Workbench.Context.Set("queryRunning", false);
            if (ReferenceEquals(_queryCancellation, cancellation))
            {
                _queryCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    public void CancelQuery()
    {
        try
        {
            _queryCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>The statement around an offset: between semicolons and <c>GO</c> lines (outside strings and comments).</summary>
    public static string StatementAt(string text, int offset)
    {
        var bounds = new List<int> { 0 };
        var inString = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString != '\0')
            {
                if (c == inString)
                {
                    inString = '\0';
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                inString = c;
            }
            else if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == ';')
            {
                bounds.Add(i + 1);
            }
            else if (c == '\n' && GoLine().IsMatch(LineAfter(text, i + 1)))
            {
                bounds.Add(i + 1);
                var end = text.IndexOf('\n', i + 1);
                i = end < 0 ? text.Length : end - 1;
                bounds.Add(i + 1);
            }
        }

        bounds.Add(text.Length);
        offset = Math.Clamp(offset, 0, text.Length);
        for (var b = 0; b + 1 < bounds.Count; b++)
        {
            var (start, end) = (bounds[b], bounds[b + 1]);
            var statement = text[start..end].Trim();
            if (offset >= start && offset <= end && statement.Length > 0 && !GoLine().IsMatch(statement))
            {
                return statement;
            }
        }

        // The caret after the last statement (trailing blank lines): the last statement.
        return bounds.Zip(bounds.Skip(1)).Select(p => text[p.First..p.Second].Trim()).LastOrDefault(s => s.Length > 0 && !GoLine().IsMatch(s)) ?? string.Empty;
    }

    private static string LineAfter(string text, int start)
    {
        var end = text.IndexOf('\n', start);
        return text[start..(end < 0 ? text.Length : end)];
    }

    [GeneratedRegex(@"^\s*GO(\s+\d+)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();

    /// <summary>Opens the last plan's raw form (XML, JSON or text) in an editor.</summary>
    public async Task OpenPlanAsync()
    {
        if (QueryResults.PlanRaw is not { Length: > 0 } raw)
        {
            ShowMessage("Run Estimated Plan or Actual Plan first.");
            return;
        }

        var extension = raw.TrimStart().StartsWith('<') ? ".sqlplan.xml" : raw.TrimStart().StartsWith('[') || raw.TrimStart().StartsWith('{') ? ".plan.json" : ".plan.txt";
        var path = Path.Combine(Paths.State, "queries", $"Plan{++_queryNumber}{extension}");
        await OpenFileAsync(path, preview: false, at: null, initialContent: raw).ConfigureAwait(true);
    }

    public void ClearQueryHistory()
    {
        Data.ClearHistory();
        QueryResults.LoadHistory(Data.History);
    }

    // ------------------------------------------------------------------ the SQL editor's connection and language service

    /// <summary>The status bar item of the active SQL editor's connection, and the Query Results window's label.</summary>
    private void UpdateSqlConnectionStatus()
    {
        if (ActiveEditor is { Model.LanguageId: SqlLanguageId } active)
        {
            _lastSqlEditor = active.Model.Path;
        }

        var editor = SqlEditor;
        var label = editor is not null && _queryConnections.TryGetValue(editor.Model.Path, out var association) && Data.Find(association.Id) is { } connection
            ? connection.Label + (association.Database is { } database ? " / " + database : string.Empty)
            : null;
        QueryResults.ConnectionLabel = editor is null ? null : label ?? "No connection: choose one";
        if (ActiveEditor is { Model.LanguageId: SqlLanguageId })
        {
            Workbench.StatusBar.Set(new StatusBarItem(SqlConnectionStatusItem, "$(database) " + (label ?? "No connection"), StatusBarAlignment.Right, 50, DaedalusStudio.Workbench.Commands.ContributionSource.Shell.Id)
            {
                Tooltip = "The connection this SQL editor runs on: click to change it",
                AccessibleName = "SQL connection: " + (label ?? "none"),
                Command = "data.chooseConnection",
            });
        }
        else
        {
            Workbench.StatusBar.Remove(SqlConnectionStatusItem);
        }
    }

    private Task SendSqlContextsAsync() => Task.WhenAll(Editors.Where(e => e.Model.LanguageId == SqlLanguageId).Select(e => SendSqlContextAsync(e.Model.Path)).ToList());

    /// <summary>Gives the SQL language server a document's dialect and its connection's schema (loading it if needed).</summary>
    private async Task SendSqlContextAsync(string path)
    {
        try
        {
            if (await Languages.ClientAsync(SqlLanguageId).ConfigureAwait(true) is not { } client)
            {
                return;
            }

            var uri = DocumentUris.FromPath(path);
            if (!_queryConnections.TryGetValue(path, out var association) || Data.Find(association.Id) is not { } connection)
            {
                await client.SqlContextAsync(new SqlContextParams(uri, "sqlserver", null, []), CancellationToken.None).ConfigureAwait(true);
                return;
            }

            var schema = Data.CachedSchema(association.Id, association.Database)
                         ?? await Data.SchemaAsync(association.Id, association.Database, refresh: false, CancellationToken.None).ConfigureAwait(true);
            var objects = schema.Objects.Select(o => new SqlSchemaObject(o.Schema, o.Name, o.Kind, [.. o.Columns.Select(c => new SqlSchemaColumn(c.Name, c.Type, c.IsKey, c.Nullable))])).ToArray();
            await client.SqlContextAsync(new SqlContextParams(uri, connection.Engine, schema.DefaultSchema, objects), CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is Rpc.JsonRpcRemoteException or InvalidOperationException or Rpc.JsonRpcConnectionLostException or DataPasswordRequiredException or OperationCanceledException)
        {
            DataLog.AppendLine($"The SQL context of {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>The active document changed: the SQL connection item and context follow it.</summary>
    private void OnSqlEditorActivated()
    {
        UpdateSqlConnectionStatus();
        if (ActiveEditor is { Model.LanguageId: SqlLanguageId } editor)
        {
            _ = SendSqlContextAsync(editor.Model.Path);
        }
    }
}

/// <summary>
/// Database resources of a .NET Aspire AppHost (M27b): <c>builder.AddSqlServer("sql")…AddDatabase("db")</c> and the
/// PostgreSQL and MySQL equivalents with a fixed host port (<c>port:</c> or <c>WithHostPort</c>); their generated
/// passwords come from the AppHost's user secrets (<c>Parameters:&lt;name&gt;-password</c>). Resources on dynamic
/// ports exist only while the AppHost runs (M17).
/// </summary>
public static partial class AspireResources
{
    [GeneratedRegex(@"Add(?<kind>SqlServer|Postgres|MySql)\(\s*""(?<name>[^""]+)""(?<args>[^;]*?)\)(?<chain>(?:\s*\.\s*\w+\([^;]*?\))*)\s*;", RegexOptions.Singleline)]
    private static partial Regex Resource();

    [GeneratedRegex(@"(?:port:\s*|WithHostPort\(\s*)(?<port>\d+)")]
    private static partial Regex Port();

    [GeneratedRegex(@"AddDatabase\(\s*""(?<db>[^""]+)""(?:\s*,\s*(?:databaseName:\s*)?""(?<real>[^""]+)"")?")]
    private static partial Regex Database();

    public static IReadOnlyList<DataConnection> Discover(string projectPath, string projectName)
    {
        string text;
        try
        {
            if (!File.ReadAllText(projectPath).Contains("Aspire.AppHost", StringComparison.OrdinalIgnoreCase)
                && !File.ReadAllText(projectPath).Contains("IsAspireHost", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            text = string.Join('\n', Directory.EnumerateFiles(Path.GetDirectoryName(projectPath)!, "*.cs").Select(File.ReadAllText));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var secrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Workbench.Web.WebDevelopmentService.UserSecretsId(projectPath) is { } id && File.Exists(Workbench.Web.WebDevelopmentService.SecretsPath(id)))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Workbench.Web.WebDevelopmentService.SecretsPath(id)));
                foreach (var property in document.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == System.Text.Json.JsonValueKind.String))
                {
                    secrets[property.Name] = property.Value.GetString()!;
                }
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
            }
        }

        var found = new List<DataConnection>();
        foreach (Match match in Resource().Matches(text))
        {
            var kind = match.Groups["kind"].Value;
            var name = match.Groups["name"].Value;
            var portMatch = Port().Match(match.Groups["args"].Value + match.Groups["chain"].Value);
            if (!portMatch.Success)
            {
                continue; // a dynamic port: known only while the AppHost runs (M17)
            }

            var port = portMatch.Groups["port"].Value;
            var password = secrets.GetValueOrDefault($"Parameters:{name}-password") ?? string.Empty;
            var databases = Database().Matches(text[match.Index..]).Take(1).Select(m => m.Groups["real"].Success ? m.Groups["real"].Value : m.Groups["db"].Value).DefaultIfEmpty(null);
            foreach (var database in databases)
            {
                var (engine, connection) = kind switch
                {
                    "SqlServer" => (DataEngines.SqlServer, $"Server=localhost,{port};User ID=sa;Password={password};TrustServerCertificate=true" + (database is null ? string.Empty : $";Database={database}")),
                    "Postgres" => (DataEngines.PostgreSql, $"Host=localhost;Port={port};Username=postgres;Password={password}" + (database is null ? string.Empty : $";Database={database}")),
                    _ => (DataEngines.MySql, $"Server=localhost;Port={port};User ID=root;Password={password}" + (database is null ? string.Empty : $";Database={database}")),
                };
                found.Add(new DataConnection($"{projectName}:aspire:{name}:{database}", $"{projectName}: {database ?? name}", engine, connection, Source: "Aspire AppHost", Project: projectPath));
            }
        }

        return found;
    }
}
