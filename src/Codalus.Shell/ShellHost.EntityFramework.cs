using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor;
using Codalus.Projects.Protocol;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Data;
using Codalus.Workbench.Localization;

namespace Codalus.Shell;

/// <summary>
/// EF Core (M27a, ADR 0036): the Entity Framework window, migrations with their SQL shown before they are applied,
/// scripts, bundles, compiled models, reverse engineering and the model view. The work runs in the build host (the
/// user's dotnet-ef and the model agent); this is the UI.
/// </summary>
public sealed partial class ShellHost
{
    public EntityFrameworkService EntityFramework { get; private set; } = null!;

    public EntityFrameworkViewModel EfExplorer { get; private set; } = null!;

    private void InitializeEntityFramework()
    {
        EntityFramework = new EntityFrameworkService(
            Solutions,
            Builds,
            Workbench.Output,
            () => Runs.StartupProject,
            ct => Languages.RunningClient(CSharpLanguageId) is { } client ? client.EfContextsAsync(null, ct) : Task.FromResult<EfDiscoveredContextsResult?>(null));
        EfExplorer = new EntityFrameworkViewModel(EntityFramework, EfActionAsync);
        EntityFramework.Changed += (_, _) => Ui.Post(() =>
        {
            if (Layout.IsVisible(EntityFrameworkViewModel.ToolWindowId))
            {
                _ = EfExplorer.LoadMigrationsAsync();
            }
        });
        Solutions.SolutionChanged += (_, _) => Ui.Post(() =>
        {
            Workbench.Context.Set("efProject", Solutions.Projects.Values.Any(EntityFrameworkService.ReferencesEfCore));
            if (!Solutions.IsOpen)
            {
                EfExplorer.Contexts.Clear();
                EfExplorer.Migrations.Clear();
            }
        });
        Solutions.ProjectChanged += (_, project) => Ui.Post(() =>
        {
            if (EntityFrameworkService.ReferencesEfCore(project))
            {
                Workbench.Context.Set("efProject", true);
            }
        });
        Languages.StatusChanged += (_, e) => Ui.Post(() =>
        {
            if (e.LanguageId == CSharpLanguageId && e.Status.State == WorkspaceStates.Ready && Layout.IsVisible(EntityFrameworkViewModel.ToolWindowId))
            {
                _ = EfExplorer.ReloadIfNoContextsAsync();
            }
        });
    }

    private EntityFrameworkViewModel OpenEntityFramework()
    {
        _ = EfExplorer.ReloadAsync(fromTool: false);
        return EfExplorer;
    }

    /// <summary>Shows the Entity Framework window and lists the contexts (from Roslyn; Refresh asks dotnet ef too).</summary>
    public async Task ShowEntityFrameworkAsync()
    {
        if (!RequireTrustedSolution("Entity Framework"))
        {
            return;
        }

        Layout.ShowToolWindow(EntityFrameworkViewModel.ToolWindowId, focus: true);
        await EfExplorer.ReloadAsync(fromTool: false).ConfigureAwait(true);
    }

    /// <summary>The window's commands; each reports its failure in the window and as a notification.</summary>
    private async Task EfActionAsync(string action, EfMigrationRow? migration)
    {
        if (!RequireTrustedSolution("Entity Framework"))
        {
            return;
        }

        try
        {
            switch (action)
            {
                case "add":
                    await AddMigrationAsync().ConfigureAwait(true);
                    break;
                case "remove":
                    await RemoveMigrationAsync().ConfigureAwait(true);
                    break;
                case "update":
                    await UpdateDatabaseAsync(null).ConfigureAwait(true);
                    break;
                case "updateTo":
                    if (migration is not null)
                    {
                        await UpdateDatabaseAsync(migration.Id).ConfigureAwait(true);
                    }

                    break;
                case "script":
                    await ScriptMigrationsAsync().ConfigureAwait(true);
                    break;
                case "bundle":
                    await BundleMigrationsAsync().ConfigureAwait(true);
                    break;
                case "model":
                    await ShowEfModelAsync().ConfigureAwait(true);
                    break;
                case "optimize":
                    await OptimizeEfModelAsync().ConfigureAwait(true);
                    break;
                case "scaffold":
                    OpenReverseEngineer();
                    break;
                case "install":
                    await InstallEfToolAsync().ConfigureAwait(true);
                    break;
                case "open":
                    if (EfExplorer.Context is { Path: { } path } context)
                    {
                        await OpenFileAsync(path, preview: false, new Position(context.Line, 0)).ConfigureAwait(true);
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or EfBuildFailedException or IOException)
        {
            EfExplorer.Message = ex.Message;
            Workbench.Notifications.Error("Entity Framework: " + ex.Message);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            EfExplorer.Busy = null;
        }
    }

    /// <summary>The window's context, or the only one; null (with a message) when there is none.</summary>
    private EfTarget? EfTarget()
    {
        if (EfExplorer.Target is { } target)
        {
            return target;
        }

        ShowMessage("Choose a DbContext in the Entity Framework window first.");
        return null;
    }

    public async Task AddMigrationAsync(string? name = null)
    {
        if (EfTarget() is not { } target)
        {
            return;
        }

        name ??= await Dialogs.InputAsync(Localizer.Get("msg.entityFramework.addMigration", "Add Migration"), Localizer.Get("msg.entityFramework.theMigrationsNameACIdentifier", "The migration's name (a C# identifier, such as AddBlogRating):"), string.Empty, text =>
            text.Trim().Length == 0 ? "Enter a name."
            : !(char.IsLetter(text.Trim()[0]) || text.Trim()[0] == '_') || !text.Trim().All(c => char.IsLetterOrDigit(c) || c == '_') ? "Use letters, digits and underscores, starting with a letter."
            : EfExplorer.Migrations.Any(m => m.Name.Equals(text.Trim(), StringComparison.Ordinal)) ? "A migration with this name exists." : null).ConfigureAwait(true);
        if (name is null)
        {
            return;
        }

        EfExplorer.Busy = $"Adding the migration {name.Trim()}…";
        var files = await EntityFramework.AddMigrationAsync(target, name.Trim(), null, CancellationToken.None).ConfigureAwait(true);
        if (files.FirstOrDefault(f => !f.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase) && !f.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase)) is { } migration)
        {
            await OpenFileAsync(migration, preview: false).ConfigureAwait(true);
        }

        StatusBar.ShowMessage(Localizer.Format("msg.entityFramework.addedTheMigration", "Added the migration {0}.", name.Trim()), TimeSpan.FromSeconds(5));
        await EfExplorer.LoadMigrationsAsync().ConfigureAwait(true);
    }

    public async Task RemoveMigrationAsync()
    {
        if (EfTarget() is not { } target || EfExplorer.Migrations.LastOrDefault() is not { } last)
        {
            ShowMessage("There is no migration to remove.");
            return;
        }

        var force = false;
        if (last.Applied == true)
        {
            if (!await Dialogs.ConfirmAsync(Localizer.Get("msg.entityFramework.removeMigration", "Remove Migration"), Localizer.Format("msg.entityFramework.isAppliedToTheDatabaseRevert", "{0} is applied to the database. Revert it in the database, then delete its files?", last.Name), Localizer.Get("msg.entityFramework.revertAndRemove", "Revert and Remove"), danger: true).ConfigureAwait(true))
            {
                return;
            }

            force = true;
        }
        else if (!await Dialogs.ConfirmAsync(Localizer.Get("msg.entityFramework.removeMigration", "Remove Migration"), Localizer.Format("msg.entityFramework.deleteTheMigrationAndUpdateThe", "Delete the migration {0} and update the model snapshot?", last.Name), Localizer.Get("msg.entityFramework.remove", "Remove"), danger: true).ConfigureAwait(true))
        {
            return;
        }

        EfExplorer.Busy = $"Removing {last.Name}…";
        await EntityFramework.RemoveMigrationAsync(target, force, CancellationToken.None).ConfigureAwait(true);
        StatusBar.ShowMessage(Localizer.Format("msg.entityFramework.removedTheMigration", "Removed the migration {0}.", last.Name), TimeSpan.FromSeconds(5));
        await EfExplorer.LoadMigrationsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Updates the database to <paramref name="migration"/> (null: the last one): shows the SQL the update will run
    /// (the pending migrations' Up scripts, or the Down scripts of a revert) and applies it only when the user says so.
    /// Returns whether it was applied.
    /// </summary>
    public async Task<bool> UpdateDatabaseAsync(string? migration)
    {
        if (EfTarget() is not { } target)
        {
            return false;
        }

        var items = EfExplorer.MigrationItems;
        if (items.Count == 0)
        {
            ShowMessage("This context has no migrations to apply.");
            return false;
        }

        EfExplorer.Busy = "Generating the SQL…";
        var sql = await EntityFramework.UpdateScriptAsync(target, items, migration, CancellationToken.None).ConfigureAwait(true);
        EfExplorer.Busy = null;
        if (sql.Trim().Length == 0)
        {
            ShowMessage("The database is already at that migration.");
            return false;
        }

        var (from, to) = EntityFrameworkService.UpdateRange(items, migration);
        var revert = string.CompareOrdinal(to, from) < 0;
        var where = EfExplorer.Connection?.ConnectionString is null ? "the app's database" : EfExplorer.Connection.Label;
        var message = revert
            ? $"Revert {where} to {(to == "0" ? "no migrations" : Name(to))}? This SQL will run:"
            : $"Apply {Plural(items.Count(m => string.CompareOrdinal(m.Id, from) > 0 && string.CompareOrdinal(m.Id, to) <= 0), "migration")} to {where}? This SQL will run:";
        var choice = await Dialogs.ShowAsync(new DialogRequest(revert ? "Revert Migrations" : "Update Database", message, [revert ? "Revert" : "Apply", "Open in Editor", "Cancel"])
        {
            Details = [.. sql.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => new DetailLine(line, line.StartsWith("--", StringComparison.Ordinal) ? "header" : null))],
            IsDanger = revert,
        }).ConfigureAwait(true);
        if (choice.Button == "Open in Editor")
        {
            await OpenSqlScriptAsync(target, sql, $"{Name(from)}-{Name(to)}").ConfigureAwait(true);
            return false;
        }

        if (choice.Button is not ("Apply" or "Revert"))
        {
            return false;
        }

        EfExplorer.Busy = revert ? "Reverting migrations…" : "Applying migrations…";
        await EntityFramework.UpdateDatabaseAsync(target, migration ?? to, CancellationToken.None).ConfigureAwait(true);
        StatusBar.ShowMessage(revert ? $"Reverted the database to {Name(to)}." : $"The database is at {Name(to)}.", TimeSpan.FromSeconds(6));
        await EfExplorer.LoadMigrationsAsync().ConfigureAwait(true);
        return true;

        string Name(string id) => id == "0" ? "empty" : items.FirstOrDefault(m => m.Id == id)?.Name ?? id;
        static string Plural(int count, string noun) => count == 1 ? "1 " + noun : $"{count} {noun}s";
    }

    /// <summary>Generate Script: from and to migrations, idempotent or not, opened as an unsaved SQL file.</summary>
    public async Task ScriptMigrationsAsync()
    {
        if (EfTarget() is not { } target)
        {
            return;
        }

        var items = EfExplorer.MigrationItems;
        var fromItems = new List<QuickPickItem> { new("(the empty database)") { Tag = "0" } };
        fromItems.AddRange(items.Select(m => new QuickPickItem(m.Name) { Tag = m.Id, Detail = m.Applied == true ? "applied" : null }));
        if (await Palette.ShowAsync(fromItems, "Script from (the starting point)").ConfigureAwait(true) is not { Tag: string from })
        {
            return;
        }

        var toItems = items.Where(m => from == "0" || string.CompareOrdinal(m.Id, from) > 0).Reverse().Select(m => new QuickPickItem(m.Name) { Tag = m.Id }).ToList();
        if (toItems.Count == 0)
        {
            ShowMessage("There are no migrations after that one.");
            return;
        }

        if (await Palette.ShowAsync(toItems, "Script to (the last migration included)").ConfigureAwait(true) is not { Tag: string to })
        {
            return;
        }

        var sqlite = EfExplorer.Context?.Provider == "Microsoft.EntityFrameworkCore.Sqlite" || EfExplorer.Database?.StartsWith("Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal) == true;
        var options = await Dialogs.ShowAsync(new DialogRequest("Generate Script", "Options of the script:", ["Generate", "Cancel"])
        {
            Choices =
            [
                new DialogChoice("idempotent", "Idempotent (each migration checks the history table first)", false, sqlite ? "SQLite doesn't support idempotent scripts." : null),
                new DialogChoice("noTransactions", "No transactions", false),
            ],
        }).ConfigureAwait(true);
        if (options.Button != "Generate")
        {
            return;
        }

        EfExplorer.Busy = "Generating the script…";
        var sql = await EntityFramework.ScriptAsync(target, from, to, options.Checked.Contains("idempotent"), options.Checked.Contains("noTransactions"), CancellationToken.None).ConfigureAwait(true);
        await OpenSqlScriptAsync(target, sql, $"{(from == "0" ? "initial" : items.First(m => m.Id == from).Name)}-{items.First(m => m.Id == to).Name}").ConfigureAwait(true);
    }

    /// <summary>Opens SQL as an unsaved file next to the project (it is written only when saved).</summary>
    private async Task OpenSqlScriptAsync(EfTarget target, string sql, string suffix)
    {
        var folder = Path.GetDirectoryName(target.Project)!;
        var name = (target.Context ?? "Migrations")[((target.Context ?? "Migrations").LastIndexOf('.') + 1)..];
        var path = Path.Combine(folder, $"{name}.{suffix}.sql");
        for (var i = 2; File.Exists(path) || Editors.Any(e => SolutionPaths.Comparer.Equals(e.Model.Path, path)); i++)
        {
            path = Path.Combine(folder, $"{name}.{suffix}.{i}.sql");
        }

        await OpenFileAsync(path, preview: false, at: null, initialContent: sql).ConfigureAwait(true);
    }

    public async Task BundleMigrationsAsync()
    {
        if (EfTarget() is not { } target || Window is null)
        {
            return;
        }

        var exe = OperatingSystem.IsWindows() ? "efbundle.exe" : "efbundle";
        if (await Window.SaveFileAsync("Create Migrations Bundle", exe, "Executable", OperatingSystem.IsWindows() ? ["*.exe"] : ["*"]).ConfigureAwait(true) is not { } output)
        {
            return;
        }

        var selfContained = await Dialogs.ShowAsync(new DialogRequest("Create Migrations Bundle", "A bundle applies the migrations without the SDK or the source.", ["Create", "Cancel"])
        {
            Choices = [new DialogChoice("selfContained", "Self-contained (runs without the .NET runtime installed)", false)],
        }).ConfigureAwait(true);
        if (selfContained.Button != "Create")
        {
            return;
        }

        EfExplorer.Busy = "Creating the migrations bundle…";
        var bundle = await EntityFramework.BundleAsync(target, output, selfContained.Checked.Contains("selfContained"), null, CancellationToken.None).ConfigureAwait(true);
        var notification = Workbench.Notifications.Info(Localizer.Format("msg.entityFramework.createdTheMigrationsBundleRunIt", "Created the migrations bundle {0}. Run it with --connection to apply the migrations elsewhere.", bundle ?? output), "Open Folder");
        _ = notification.Result.ContinueWith(
            t =>
            {
                if (t.Result == "Open Folder")
                {
                    RevealPath(bundle ?? output);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    public async Task<EfModelDocument?> ShowEfModelAsync()
    {
        if (EfTarget() is not { } target)
        {
            return null;
        }

        var id = "ef.model:" + target.Context;
        var document = Layout.FindDocument<EfModelDocument>(id);
        if (document is null)
        {
            EfModelViewModel? model = null;
            model = new EfModelViewModel(target.Context ?? "DbContext", this, EditorFont, () => LoadEfModelAsync(model!, target));
            document = new EfModelDocument(model);
        }

        Layout.OpenDocument(document, focus: true);
        await LoadEfModelAsync(document.ViewModel, target).ConfigureAwait(true);
        return document;
    }

    private async Task LoadEfModelAsync(EfModelViewModel model, EfTarget target)
    {
        model.Message = "Building and reading the model…";
        try
        {
            EntityFramework.Invalidate();
            model.Model = await EntityFramework.ModelAsync(target, CancellationToken.None).ConfigureAwait(true);
            model.Message = null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or EfBuildFailedException)
        {
            model.Message = ex.Message;
        }
    }

    public async Task OptimizeEfModelAsync()
    {
        if (EfTarget() is not { } target)
        {
            return;
        }

        var folder = await Dialogs.InputAsync(Localizer.Get("msg.entityFramework.compiledModel", "Compiled Model"), Localizer.Get("msg.entityFramework.theFolderForTheCompiledModel", "The folder for the compiled model (relative to the project):"), "CompiledModels", text => text.Trim().Length == 0 ? "Enter a folder." : null).ConfigureAwait(true);
        if (folder is null)
        {
            return;
        }

        EfExplorer.Busy = "Generating the compiled model…";
        var files = await EntityFramework.OptimizeAsync(target, folder.Trim(), null, CancellationToken.None).ConfigureAwait(true);
        Workbench.Notifications.Info($"Generated the compiled model: {files.Count} file(s) in {folder.Trim()}. Use it with options.UseModel({(target.Context ?? "Context")[((target.Context ?? "Context").LastIndexOf('.') + 1)..]}Model.Instance).");
        if (files.FirstOrDefault(f => f.EndsWith("Model.cs", StringComparison.Ordinal) && !f.EndsWith("ModelBuilder.cs", StringComparison.Ordinal)) is { } main)
        {
            await OpenFileAsync(main, preview: true).ConfigureAwait(true);
        }
    }

    public EfScaffoldDocument? OpenReverseEngineer()
    {
        if (!RequireTrustedSolution("Entity Framework"))
        {
            return null;
        }

        var projects = EntityFramework.Projects.Select(p => p.Path).ToList();
        if (projects.Count == 0)
        {
            projects = [.. Solutions.Projects.Values.Where(p => p.State == ProjectStates.Loaded).Select(p => p.Path)];
        }

        var document = Layout.FindDocument<EfScaffoldDocument>(EfScaffoldDocument.DocumentId)
            ?? new EfScaffoldDocument(new EfScaffoldViewModel(
                projects,
                project => [.. ConnectionDiscovery.ForProject(project).Select(c => new EfConnectionChoice(c.Label, c.ConnectionString))],
                ScaffoldAsync));
        Layout.OpenDocument(document, focus: true);
        return document;
    }

    private async Task ScaffoldAsync(EfScaffoldViewModel form)
    {
        try
        {
            var files = await EntityFramework.ScaffoldAsync(form.Parameters(), CancellationToken.None).ConfigureAwait(true);
            form.Result = files.Count == 1 ? "Generated 1 file." : $"Generated {files.Count} files.";
            if ((files.FirstOrDefault(f => f.EndsWith("Context.cs", StringComparison.Ordinal)) ?? (files.Count > 0 ? files[0] : null)) is { } first)
            {
                await OpenFileAsync(first, preview: false).ConfigureAwait(true);
            }

            await EfExplorer.ReloadAsync(fromTool: false).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Rpc.JsonRpcRemoteException or Rpc.JsonRpcConnectionLostException or EfBuildFailedException or ArgumentException)
        {
            form.Error = ex.Message;
        }
    }

    /// <summary>Restores the .NET tools when the manifest lists dotnet-ef, otherwise installs it as a local tool at the project's EF Core version.</summary>
    public async Task InstallEfToolAsync()
    {
        var projects = EntityFramework.Projects;
        var project = EfExplorer.Context?.Project ?? (projects.Count > 0 ? projects[0].Path : null);
        if (project is null)
        {
            return;
        }

        var status = await EntityFramework.StatusAsync(project, CancellationToken.None).ConfigureAwait(true);
        ToolCommandResult result;
        if (status.ManifestListsTool)
        {
            EfExplorer.Busy = "Restoring the .NET tools…";
            result = await Packages.RunToolCommandAsync(new ToolCommandParams(ToolActions.Restore), CancellationToken.None).ConfigureAwait(true);
        }
        else
        {
            if (!await Dialogs.ConfirmAsync("Install dotnet-ef", $"Install dotnet-ef {status.EfCoreVersion ?? "(latest)"} as a local tool of this solution (a tool manifest in .config/dotnet-tools.json)? It is downloaded from your NuGet sources.", "Install").ConfigureAwait(true))
            {
                return;
            }

            EfExplorer.Busy = "Installing dotnet-ef…";
            if (status.Manifest is null)
            {
                await Packages.RunToolCommandAsync(new ToolCommandParams(ToolActions.CreateManifest), CancellationToken.None).ConfigureAwait(true);
            }

            result = await Packages.RunToolCommandAsync(new ToolCommandParams(ToolActions.Install, "dotnet-ef", status.EfCoreVersion), CancellationToken.None).ConfigureAwait(true);
        }

        EntityFramework.Channel.AppendLine(result.Output);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("dotnet-ef could not be installed; see the Entity Framework output.");
        }

        EfExplorer.Busy = null;
        await EfExplorer.ReloadAsync(fromTool: false).ConfigureAwait(true);
        await EfExplorer.LoadMigrationsAsync().ConfigureAwait(true);
    }
}
