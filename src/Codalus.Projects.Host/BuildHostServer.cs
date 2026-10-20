using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Codalus.Projects.Protocol;
using Codalus.Rpc;

namespace Codalus.Projects.Host;

/// <summary>
/// The build host's side of the protocol (docs/project-protocol.md). One solution session at a time; the shell
/// starts one host per solution. Notifications to the shell go through one ordered queue.
/// </summary>
internal sealed class BuildHostServer : IAsyncDisposable, IHostEvents
{
    private readonly JsonRpcConnection _connection;
    private readonly string _msbuildPath;
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<Func<Task>> _outgoing = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _outgoingLoop;
    private SolutionSession? _session;

    public BuildHostServer(JsonRpcConnection connection, string msbuildPath)
    {
        _connection = connection;
        _msbuildPath = msbuildPath;
        _outgoingLoop = Task.Run(PumpOutgoingAsync);
    }

    public Task ExitRequested => _exit.Task;

    /// <summary>Why the SDK the solution's folder resolves to isn't the one in use (ADR 0025), or null.</summary>
    public string? SdkWarning { get; init; }

    /// <summary><c>&lt;dotnet root&gt;/sdk/&lt;version&gt;/</c> → <c>&lt;dotnet root&gt;/dotnet</c>.</summary>
    private string DotnetExecutable
    {
        get
        {
            var root = Directory.GetParent(Path.TrimEndingDirectorySeparator(_msbuildPath))?.Parent?.FullName;
            var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
            return root is not null && File.Exists(Path.Combine(root, exe)) ? Path.Combine(root, exe) : exe;
        }
    }

    private SolutionSession Session => _session ?? throw new JsonRpcHandlerException(ProjectErrorCodes.NoSolution, "No solution is open.");

    public void RegisterHandlers()
    {
        var json = ProjectJsonContext.Default;
        OnRequest(ProjectMethods.Initialize, json.InitializeParams, json.InitializeResult, (_, _) =>
        {
            var version = typeof(BuildHostServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            var msbuild = typeof(Microsoft.Build.Evaluation.Project).Assembly.GetName().Version?.ToString() ?? "?";
            return Task.FromResult(new InitializeResult("codalus-buildhost", version, ProjectMethods.ProtocolVersion, msbuild, _msbuildPath, SdkWarning));
        });
        OnRequest(ProjectMethods.OpenSolution, json.OpenSolutionParams, json.SolutionInfo, async (p, ct) =>
        {
            if (_session is { } previous)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            _session = new SolutionSession(this, DotnetExecutable) { SdkPath = _msbuildPath, SdkWarning = SdkWarning };
            return await Guard(() => _session.OpenAsync(p, ct)).ConfigureAwait(false);
        });
        OnRequest(ProjectMethods.CloseSolution, json.EmptyParams, json.EmptyResult, async (_, _) =>
        {
            if (_session is { } session)
            {
                _session = null;
                await session.DisposeAsync().ConfigureAwait(false);
            }

            return new EmptyResult();
        });
        OnRequest(ProjectMethods.Trust, json.TrustParams, json.EmptyResult, (p, _) => Sync(() => Session.Trust(p.Restore)));
        OnRequest(ProjectMethods.SetConfiguration, json.SetConfigurationParams, json.EmptyResult, (p, _) => Sync(() => Session.SetConfiguration(p)));
        OnRequest(ProjectMethods.Reevaluate, json.ProjectPathParams, json.EmptyResult, async (p, _) =>
        {
            await Session.ReevaluateAsync([Path.GetFullPath(p.Path)]).ConfigureAwait(false);
            return new EmptyResult();
        });
        OnRequest(ProjectMethods.Restore, json.RestoreParams, json.EmptyResult, (p, _) => Sync(() => Session.StartRestore(p.Path)));
        OnRequest(ProjectMethods.ListDirectory, json.ListDirectoryParams, json.ListDirectoryResult, (p, _) => Guard(() => Task.FromResult(Session.ListDirectory(p))));
        OnRequest(ProjectMethods.CreateFile, json.CreateFileParams, json.FileOperationResult, (p, _) => Guard(() => Task.FromResult(Session.CreateFile(p))));
        OnRequest(ProjectMethods.CreateFolder, json.CreateFolderParams, json.FileOperationResult, (p, _) => Guard(() => Task.FromResult(Session.CreateFolder(p))));
        OnRequest(ProjectMethods.Rename, json.RenameParams, json.FileOperationResult, (p, ct) => Guard(() => Session.RenameAsync(p, ct)));
        OnRequest(ProjectMethods.Move, json.MoveParams, json.FileOperationResult, (p, ct) => Guard(() => Session.MoveAsync(p, ct)));
        OnRequest(ProjectMethods.Delete, json.DeleteParams, json.FileOperationResult, (p, ct) => Guard(() => Session.DeleteAsync(p, ct)));
        OnRequest(ProjectMethods.AddProject, json.AddProjectParams, json.EmptyResult, (p, ct) => GuardEmpty(() => Session.AddProjectAsync(p, ct)));
        OnRequest(ProjectMethods.RemoveProject, json.ProjectPathParams, json.EmptyResult, (p, ct) => GuardEmpty(() => Session.RemoveProjectAsync(p, ct)));
        OnRequest(ProjectMethods.AddSolutionFolder, json.SolutionFolderParams, json.EmptyResult, (p, ct) => GuardEmpty(() => Session.AddFolderAsync(p, ct)));
        OnRequest(ProjectMethods.RenameSolutionFolder, json.RenameSolutionFolderParams, json.EmptyResult, (p, ct) => GuardEmpty(() => Session.RenameFolderAsync(p, ct)));
        OnRequest(ProjectMethods.RemoveSolutionFolder, json.RemoveSolutionFolderParams, json.EmptyResult, (p, ct) => GuardEmpty(() => Session.RemoveFolderAsync(p, ct)));
        OnRequest(ProjectMethods.MoveInSolution, json.MoveInSolutionParams, json.EmptyResult, (p, ct) => GuardEmpty(() => Session.MoveInSolutionAsync(p, ct)));
        OnRequest(ProjectMethods.AddProjectReference, json.ProjectReferenceParams, json.EmptyResult, (p, _) => Sync(() => Session.AddProjectReference(p)));
        OnRequest(ProjectMethods.RemoveProjectReference, json.ProjectReferenceParams, json.EmptyResult, (p, _) => Sync(() => Session.RemoveProjectReference(p)));
        OnRequest(ProjectMethods.SetActiveFramework, json.SetActiveFrameworkParams, json.EmptyResult, (p, _) => Sync(() => Session.SetActiveFramework(p)));
        OnRequest(ProjectMethods.DesignTimeBuild, json.DesignTimeBuildParams, json.EmptyResult, (p, _) => Sync(() => Session.RequestDesignTimeBuild(p.Path)));
        OnRequest(ProjectMethods.Build, json.BuildParams, json.BuildResultInfo, async (p, ct) =>
        {
            return await Guard(() => Session.BuildAsync(p, this, ct)).ConfigureAwait(false);
        });
        OnRequest(ProjectMethods.CancelBuild, json.EmptyParams, json.EmptyResult, (_, _) => Sync(() => _session?.CancelBuild()));
        OnRequest(ProjectMethods.RunInfo, json.RunInfoParams, json.RunInfo, (p, _) => Guard(() => Task.Run(() => Session.GetRunInfo(p.Path, p.TargetFramework))));
        RegisterPackageHandlers();
        RegisterProjectManagementHandlers();
        RegisterItemHandlers();
        RegisterBuildDepthHandlers();
        RegisterEntityFrameworkHandlers();
        RegisterWebToolHandlers();
        OnRequest(ProjectMethods.Shutdown, json.EmptyParams, json.EmptyResult, async (_, _) =>
        {
            if (_session is { } session)
            {
                _session = null;
                await session.DisposeAsync().ConfigureAwait(false);
            }

            return new EmptyResult();
        });
        _connection.OnNotification(ProjectMethods.Exit, json.EmptyParams, _ => _exit.TrySetResult());
    }

    /// <summary>Project properties, references and solution management (ADR 0013).</summary>
    private void RegisterProjectManagementHandlers()
    {
        var json = ProjectManagementJsonContext.Default;
        OnRequest(ProjectManagementMethods.GetProperties, json.GetPropertiesParams, json.ProjectPropertiesResult, (p, _) => Guard(() => Task.Run(() => Session.GetProperties(p))));
        OnRequest(ProjectManagementMethods.SetProperty, json.SetPropertyParams, json.SetPropertyResult, (p, _) => Guard(() => Session.SetPropertyAsync(p)));
        OnRequest(ProjectManagementMethods.AddReference, json.ReferenceEditParams, json.EmptyResult, (p, _) => Sync(() => Session.AddReference(p)));
        OnRequest(ProjectManagementMethods.RemoveReference, json.ReferenceEditParams, json.EmptyResult, (p, _) => Sync(() => Session.RemoveReference(p)));
        OnRequest(ProjectManagementMethods.Configurations, json.EmptyParams, json.SolutionConfigurationsResult, (_, _) => Guard(() => Task.Run(Session.GetConfigurations)));
        OnRequest(ProjectManagementMethods.SetProjectConfiguration, json.SetProjectConfigurationParams, json.EmptyResult, (p, ct) => GuardEmpty(() => Session.SetProjectConfigurationAsync(p, ct)));
        OnRequest(ProjectManagementMethods.AddConfiguration, json.SolutionConfigurationEditParams, json.EmptyResult, (p, ct) => GuardEmpty(() => Session.AddConfigurationAsync(p, ct)));
        OnRequest(ProjectManagementMethods.RemoveConfiguration, json.SolutionConfigurationEditParams, json.EmptyResult, (p, ct) => GuardEmpty(() => Session.RemoveConfigurationAsync(p, ct)));
        OnRequest(ProjectManagementMethods.Dependencies, json.EmptyParams, json.SolutionDependenciesResult, (_, _) => Guard(() => Task.FromResult(Session.GetDependencies())));
        OnRequest(ProjectManagementMethods.SetDependencies, json.SetDependenciesParams, json.EmptyResult, (p, ct) => GuardEmpty(() => Session.SetDependenciesAsync(p, ct)));
        OnRequest(ProjectManagementMethods.Unload, json.ProjectPathParams, json.EmptyResult, (p, _) => GuardEmpty(() => Session.UnloadAsync(p.Path)));
        OnRequest(ProjectManagementMethods.Reload, json.ProjectPathParams, json.EmptyResult, (p, _) => GuardEmpty(() => Session.ReloadAsync(p.Path)));
        OnRequest(ProjectManagementMethods.SaveFilter, json.SaveFilterParams, json.EmptyResult, (p, _) => Sync(() => Session.SaveFilter(p)));
        OnRequest(ProjectManagementMethods.SdkInfo, json.EmptyParams, json.SdkInfoResult, (_, ct) => Guard(() => Session.GetSdkInfoAsync(ct)));
        OnRequest(ProjectManagementMethods.SetGlobalJson, json.SetGlobalJsonParams, json.EmptyResult, (p, _) => Sync(() => Session.SetGlobalJson(p)));
    }

    /// <summary>Project items, Show All Files, folder mode and item templates (ADR 0033).</summary>
    private void RegisterBuildDepthHandlers()
    {
        var json = BuildDepthJsonContext.Default;
        OnRequest(BuildDepthMethods.Batch, json.BatchBuildParams, json.BatchBuildResult, async (p, ct) =>
        {
            return await Guard(() => Session.BatchBuildAsync(p, this, ct)).ConfigureAwait(false);
        });
        OnRequest(BuildDepthMethods.UpToDate, json.UpToDateParams, json.UpToDateResult, (p, ct) => Guard(() => Session.CheckUpToDateAsync(p, ct)));
        OnRequest(BuildDepthMethods.Timings, json.EmptyParams, json.BuildTimingsResult, (_, _) => Guard(() => Task.FromResult(Session.GetTimings())));
        OnRequest(BuildDepthMethods.GetBuildEvents, json.BuildEventsParams, json.BuildEventsResult, (p, _) => Guard(() => Task.Run(() => Session.GetBuildEvents(p))));
        OnRequest(BuildDepthMethods.SetBuildEvents, json.SetBuildEventsParams, json.BuildEventsResult, (p, _) => Guard(() => Session.SetBuildEventsAsync(p)));
        OnRequest(BuildDepthMethods.OpenBinaryLog, json.OpenBinaryLogParams, json.BinaryLogInfo, (p, ct) => Guard(() => Session.OpenBinaryLogAsync(p, ct)));
        OnRequest(BuildDepthMethods.BinaryLogChildren, json.BinaryLogChildrenParams, json.BinaryLogChildrenResult, (p, _) => Guard(() => Task.FromResult(Session.BinaryLogChildren(p))));
        OnRequest(BuildDepthMethods.SearchBinaryLog, json.SearchBinaryLogParams, json.SearchBinaryLogResult, (p, ct) => Guard(() => Session.SearchBinaryLogAsync(p, ct)));
        OnRequest(BuildDepthMethods.CloseBinaryLog, json.CloseBinaryLogParams, json.EmptyResult, (p, _) => Sync(() => Session.CloseBinaryLog(p)));
    }

    private void RegisterItemHandlers()
    {
        var json = ItemJsonContext.Default;
        OnRequest(ItemMethods.Get, json.ItemParams, json.ItemInfoResult, (p, _) => Guard(() => Task.Run(() => Session.GetItem(p))));
        OnRequest(ItemMethods.Set, json.ItemSetParams, json.ItemEditResult, (p, _) => Guard(() => Session.SetItemAsync(p)));
        OnRequest(ItemMethods.Exclude, json.ItemPathsParams, json.ItemEditResult, (p, _) => Guard(() => Session.ExcludeAsync(p)));
        OnRequest(ItemMethods.Include, json.ItemPathsParams, json.ItemEditResult, (p, _) => Guard(() => Session.IncludeAsync(p)));
        OnRequest(ItemMethods.Move, json.MoveItemParams, json.ItemEditResult, (p, _) => Guard(() => Session.MoveItemAsync(p)));
        OnRequest(ItemMethods.AddExisting, json.AddExistingItemsParams, json.FileOperationResult, (p, _) => Guard(() => Session.AddExistingAsync(p)));
        OnRequest(ItemMethods.ListTree, json.ListTreeParams, json.ListTreeResult, (p, _) => Guard(() => Task.Run(() => Session.ListTree(p))));
        OnRequest(ItemMethods.Discover, json.EmptyParams, json.DiscoverProjectsResult, (_, _) => Guard(() => Task.Run(Session.DiscoverProjects)));
        OnRequest(ItemMethods.SetFolderProjects, json.SetFolderProjectsParams, json.EmptyResult, (p, ct) => GuardEmpty(() => Session.SetFolderProjectsAsync(p, ct)));
        OnRequest(ItemMethods.Instantiate, json.InstantiateTemplateParams, json.InstantiateTemplateResult, (p, ct) => Guard(() => Session.InstantiateTemplateAsync(p, ct)));
    }

    /// <summary>EF Core through the user's dotnet-ef and the model agent (ADR 0036).</summary>
    private void RegisterEntityFrameworkHandlers()
    {
        var json = EntityFrameworkJsonContext.Default;
        OnRequest(EfMethods.Status, json.EfStatusParams, json.EfStatusResult, (p, ct) => Guard(() => Session.EfStatusAsync(p, ct)));
        OnRequest(EfMethods.Contexts, json.EfTarget, json.EfContextsResult, (p, ct) => Guard(() => Session.EfContextsAsync(p, ct)));
        OnRequest(EfMethods.ContextInfo, json.EfTarget, json.EfContextInfoResult, (p, ct) => Guard(() => Session.EfContextInfoAsync(p, ct)));
        OnRequest(EfMethods.Migrations, json.EfTarget, json.EfMigrationsResult, (p, ct) => Guard(() => Session.EfMigrationsAsync(p, ct)));
        OnRequest(EfMethods.AddMigration, json.EfAddMigrationParams, json.EfFilesResult, (p, ct) => Guard(() => Session.EfAddMigrationAsync(p, ct)));
        OnRequest(EfMethods.RemoveMigration, json.EfRemoveMigrationParams, json.EfFilesResult, (p, ct) => Guard(() => Session.EfRemoveMigrationAsync(p, ct)));
        OnRequest(EfMethods.Script, json.EfScriptParams, json.EfScriptResult, (p, ct) => Guard(() => Session.EfScriptAsync(p, ct)));
        OnRequest(EfMethods.UpdateDatabase, json.EfUpdateDatabaseParams, json.EmptyResult, (p, ct) => Guard(() => Session.EfUpdateDatabaseAsync(p, ct)));
        OnRequest(EfMethods.Bundle, json.EfBundleParams, json.EfFilesResult, (p, ct) => Guard(() => Session.EfBundleAsync(p, ct)));
        OnRequest(EfMethods.Scaffold, json.EfScaffoldParams, json.EfFilesResult, (p, ct) => Guard(() => Session.EfScaffoldAsync(p, ct)));
        OnRequest(EfMethods.Optimize, json.EfOptimizeParams, json.EfFilesResult, (p, ct) => Guard(() => Session.EfOptimizeAsync(p, ct)));
        OnRequest(EfMethods.Model, json.EfTarget, json.EfModelResult, (p, ct) => Guard(() => Session.EfModelAsync(p, ct)));
    }

    /// <summary>Scaffolding through the user's dotnet-aspnet-codegenerator, and service references (ADR 0041).</summary>
    private void RegisterWebToolHandlers()
    {
        var json = WebToolsJsonContext.Default;
        OnRequest(ScaffoldMethods.Status, json.ScaffoldStatusParams, json.ScaffoldStatusResult, (p, ct) => Guard(() => Session.ScaffoldStatusAsync(p, ct)));
        OnRequest(ScaffoldMethods.Install, json.ScaffoldInstallParams, json.ScaffoldStatusResult, (p, ct) => Guard(() => Session.ScaffoldInstallAsync(p, ct)));
        OnRequest(ScaffoldMethods.Run, json.ScaffoldRunParams, json.ScaffoldRunResult, (p, ct) => Guard(() => Session.ScaffoldRunAsync(p, ct)));
        OnRequest(ServiceReferenceMethods.List, json.ServiceReferencesParams, json.ServiceReferencesResult, (p, _) => Guard(() => Task.Run(() => Session.ServiceReferencesOf(p))));
        OnRequest(ServiceReferenceMethods.Add, json.ServiceReferenceParams, json.ServiceReferenceEditResult, (p, ct) => Guard(() => Session.AddServiceReferenceAsync(p, ct)));
        OnRequest(ServiceReferenceMethods.Update, json.ServiceReferenceParams, json.ServiceReferenceEditResult, (p, _) => Guard(() => Session.UpdateServiceReferenceAsync(p)));
        OnRequest(ServiceReferenceMethods.Remove, json.ServiceReferenceRemoveParams, json.ServiceReferenceEditResult, (p, _) => Guard(() => Session.RemoveServiceReferenceAsync(p)));

        // WCF references through the user's dotnet-svcutil (ADR 0069).
        var wcf = WcfJsonContext.Default;
        OnRequest(WcfReferenceMethods.Status, wcf.WcfToolStatusParams, wcf.WcfToolStatusResult, (p, ct) => Guard(() => Session.WcfStatusAsync(p, ct)));
        OnRequest(WcfReferenceMethods.Install, wcf.WcfToolInstallParams, wcf.WcfToolStatusResult, (p, ct) => Guard(() => Session.WcfInstallAsync(p, ct)));
        OnRequest(WcfReferenceMethods.List, wcf.WcfReferencesParams, wcf.WcfReferencesResult, (p, _) => Guard(() => Task.Run(() => Session.WcfReferencesOf(p))));
        OnRequest(WcfReferenceMethods.Add, wcf.WcfReferenceParams, wcf.WcfReferenceRunResult, (p, ct) => Guard(() => Session.AddWcfReferenceAsync(p, ct)));
        OnRequest(WcfReferenceMethods.Update, wcf.WcfReferenceParams, wcf.WcfReferenceRunResult, (p, ct) => Guard(() => Session.UpdateWcfReferenceAsync(p, ct)));
        OnRequest(WcfReferenceMethods.Remove, wcf.WcfReferenceRemoveParams, wcf.WcfReferencesResult, (p, _) => Guard(() => Session.RemoveWcfReferenceAsync(p)));
    }

    /// <summary>NuGet packages and .NET tools (ADR 0012).</summary>
    private void RegisterPackageHandlers()
    {
        var json = PackageJsonContext.Default;
        OnRequest(PackageMethods.Search, json.PackageSearchParams, json.PackageSearchResult, (p, ct) => GuardNuGet(() => Session.NuGet.SearchAsync(p, ct)));
        OnRequest(PackageMethods.Details, json.PackageDetailsParams, json.PackageDetails, (p, ct) => GuardNuGet(() => Session.NuGet.DetailsAsync(p, ct)));
        OnRequest(PackageMethods.Status, json.PackageStatusParams, json.PackageStatusResult, (p, ct) => GuardNuGet(() => Session.NuGet.StatusAsync(p, ct)));
        OnRequest(PackageMethods.Installed, json.InstalledPackagesParams, json.InstalledPackagesResult, (p, ct) => Guard(() => Session.InstalledPackagesAsync(p, ct)));
        OnRequest(PackageMethods.Why, json.PackageWhyParams, json.PackageWhyResult, (p, _) => Guard(() => Task.Run(() => Session.WhyPackage(p))));
        OnRequest(PackageMethods.Install, json.PackageInstallParams, json.PackageEditResult, (p, ct) => Guard(() => Session.InstallPackageAsync(p, ct)));
        OnRequest(PackageMethods.Uninstall, json.PackageUninstallParams, json.PackageEditResult, (p, ct) => Guard(() => Session.UninstallPackageAsync(p, ct)));
        OnRequest(PackageMethods.Sources, json.EmptyParams, json.PackageSourcesResult, (_, _) => GuardNuGet(() => Task.FromResult(Session.NuGet.GetSources())));
        OnRequest(PackageMethods.SetSources, json.SetPackageSourcesParams, json.EmptyResult, (p, _) => GuardNuGet(() =>
        {
            Session.NuGet.SetSources(p);
            return Task.FromResult(new EmptyResult());
        }));
        OnRequest(PackageMethods.SignIn, json.PackageSignInParams, json.PackageSignInResult, (p, ct) => GuardNuGet(() =>
            Session.NuGet.SignInAsync(p, message => Post(PackageMethods.SignInMessage, new PackageSignInMessage(p.Source, message), json.PackageSignInMessage), ct)));
        OnRequest(PackageMethods.Tools, json.EmptyParams, json.ToolsResult, (_, ct) => Guard(() => Session.Tools.ListAsync(ct)));
        OnRequest(PackageMethods.ToolCommand, json.ToolCommandParams, json.ToolCommandResult, async (p, ct) =>
        {
            var (succeeded, output) = await Guard(() => Session.Tools.RunActionAsync(p, ct)).ConfigureAwait(false);
            return new ToolCommandResult(succeeded, output);
        });
    }

    /// <summary>
    /// Like <see cref="Guard{T}"/>, and a NuGet API that this SDK's NuGet lacks (the host compiles against an older
    /// one, ADR 0012) becomes a readable error instead of a crash.
    /// </summary>
    private static Task<T> GuardNuGet<T>(Func<Task<T>> action) => Guard(async () =>
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MissingMethodException or MissingFieldException or TypeLoadException or FileNotFoundException { FileName: not null } && ex.Message.Contains("NuGet", StringComparison.Ordinal))
        {
            throw new JsonRpcHandlerException(ProjectErrorCodes.InvalidOperation, $"The NuGet client in this .NET SDK is not supported: {ex.Message}");
        }
    });

    /// <summary>Maps expected failures (missing files, conflicts, invalid edits) to protocol errors with readable messages.</summary>
    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new JsonRpcHandlerException(ProjectErrorCodes.FileSystem, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Microsoft.Build.Exceptions.InvalidProjectFileException)
        {
            if (ex is ArgumentNullException || (ex is not Microsoft.Build.Exceptions.InvalidProjectFileException && !ThrownByHost(ex)))
            {
                // A bug, not a user's mistake (the host's own refusals are thrown here; this came from MSBuild, NuGet or
                // .NET): the stack goes to the host's log (and CI output) and to the error's data, where the shell's
                // JsonRpcRemoteException shows it.
                await Console.Error.WriteLineAsync("[host] [error] " + ex).ConfigureAwait(false);
                throw new JsonRpcHandlerException(ProjectErrorCodes.InvalidOperation, ex.Message) { Details = ex.ToString() };
            }

            throw new JsonRpcHandlerException(ProjectErrorCodes.InvalidOperation, ex.Message);
        }
    }

    /// <summary>Whether the build host's own code threw <paramref name="exception"/> (a refusal with a readable message).</summary>
    internal static bool ThrownByHost(Exception exception) =>
        exception.TargetSite?.DeclaringType?.Assembly == typeof(BuildHostServer).Assembly;

    private static Task<EmptyResult> GuardEmpty(Func<Task> action) => Guard(async () =>
    {
        await action().ConfigureAwait(false);
        return new EmptyResult();
    });

    private static Task<EmptyResult> Sync(Action action) => GuardEmpty(() =>
    {
        action();
        return Task.CompletedTask;
    });

    private static Task<EmptyResult> Sync(Func<bool> action) => Sync(() => { _ = action(); });

    // ------------------------------------------------------------------ events to the shell

    public Task SolutionChangedAsync(SolutionInfo solution) => Post(ProjectMethods.SolutionChanged, solution, ProjectJsonContext.Default.SolutionInfo);

    public Task ProjectEvaluatedAsync(ProjectInfo project) => Post(ProjectMethods.ProjectEvaluated, project, ProjectJsonContext.Default.ProjectInfo);

    public Task ProgressAsync(EvaluationProgressParams progress) => Post(ProjectMethods.EvaluationProgress, progress, ProjectJsonContext.Default.EvaluationProgressParams);

    public Task RestoreOutputAsync(string line) => Post(ProjectMethods.RestoreOutput, new RestoreOutputParams(line), ProjectJsonContext.Default.RestoreOutputParams);

    public Task RestoreStateAsync(string state, string? message) => Post(ProjectMethods.RestoreState, new RestoreStateParams(state, message), ProjectJsonContext.Default.RestoreStateParams);

    public Task CompilerInputsAsync(CompilerInputsInfo inputs) => Post(ProjectMethods.CompilerInputs, inputs, ProjectJsonContext.Default.CompilerInputsInfo);

    public Task DesignTimeProgressAsync(DesignTimeProgressParams progress) => Post(ProjectMethods.DesignTimeProgress, progress, ProjectJsonContext.Default.DesignTimeProgressParams);

    public Task FilesChangedAsync(FilesChangedParams changes) => Post(ItemMethods.FilesChanged, changes, ItemJsonContext.Default.FilesChangedParams);

    public Task BuildOutputAsync(string line) => Post(ProjectMethods.BuildOutput, new BuildOutputParams(line), ProjectJsonContext.Default.BuildOutputParams);

    public Task BuildDiagnosticAsync(BuildDiagnosticInfo diagnostic) => Post(ProjectMethods.BuildDiagnostic, diagnostic, ProjectJsonContext.Default.BuildDiagnosticInfo);

    public Task EfOutputAsync(EfOutputParams output) => Post(EfMethods.Output, output, EntityFrameworkJsonContext.Default.EfOutputParams);

    public Task ScaffoldOutputAsync(ScaffoldOutputParams output) => Post(ScaffoldMethods.Output, output, WebToolsJsonContext.Default.ScaffoldOutputParams);

    public Task WcfOutputAsync(ScaffoldOutputParams output) => Post(WcfReferenceMethods.Output, output, WcfJsonContext.Default.ScaffoldOutputParams);

    /// <summary>
    /// Registers a request whose response is sent after the notifications queued before it, error or not. Notifications
    /// go through the queue and a response doesn't, so it overtook them: an item edit's result reached the shell before
    /// the project it had evaluated, and Solution Explorer showed the old items until then (macOS CI, PR #75). Builds
    /// flushed their output this way; every request does now, as the shell's <c>SolutionService.RequestAsync</c> expects.
    /// </summary>
    private void OnRequest<TParams, TResult>(string method, JsonTypeInfo<TParams> paramsInfo, JsonTypeInfo<TResult> resultInfo, Func<TParams, CancellationToken, Task<TResult>> handler) =>
        _connection.OnRequest(method, paramsInfo, resultInfo, async (parameters, cancellationToken) =>
        {
            try
            {
                return await handler(parameters, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await FlushAsync().ConfigureAwait(false);
            }
        });

    /// <summary>
    /// Completes once every notification queued so far has been sent (a response must not overtake them), or once the
    /// queue has stopped (the connection was lost: nothing more is sent).
    /// </summary>
    private async Task FlushAsync()
    {
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_outgoing.Writer.TryWrite(() =>
            {
                sent.TrySetResult();
                return Task.CompletedTask;
            }))
        {
            sent.TrySetResult();
        }

        await Task.WhenAny(sent.Task, _outgoingLoop).ConfigureAwait(false);
    }

    public Task LogAsync(string level, string message) => Post(ProjectMethods.Log, new LogParams(level, message), ProjectJsonContext.Default.LogParams);

    private Task Post<T>(string method, T parameters, JsonTypeInfo<T> info)
    {
        _outgoing.Writer.TryWrite(() => _connection.NotifyAsync(method, parameters, info, CancellationToken.None));
        return Task.CompletedTask;
    }

    private async Task PumpOutgoingAsync()
    {
        await foreach (var send in _outgoing.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await send().ConfigureAwait(false);
            }
            catch (JsonRpcConnectionLostException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is { } session)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _outgoing.Writer.TryComplete();
        await _outgoingLoop.ConfigureAwait(false);
    }
}
