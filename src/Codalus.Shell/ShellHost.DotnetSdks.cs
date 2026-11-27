using System.Globalization;
using System.Text.Json;
using Codalus.Platform;
using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Menus;
using Codalus.Workbench.Sdks;
using Codalus.Workbench.Setup;
using Codalus.Workbench.ToolWindows;

namespace Codalus.Shell;

/// <summary>
/// Tools → .NET SDKs (ADR 0113): the .NET SDKs and runtimes of this machine, by folder, with their updates from
/// Microsoft's release metadata; install another channel, update within a feature band, remove an SDK and the runtimes
/// only it brought. The selection's details show the exact command or folders before anything runs (layout B of
/// <c>docs/design/controls/DotnetSdks.dc.html</c>): Install and Update run from their button, Remove confirms.
/// </summary>
public sealed partial class ShellHost
{
    public const string DotnetSdksWindow = "dotnetSdks";

    private DotnetSdksContent? _dotnetSdksContent;
    private DotnetSdksViewModel? _dotnetSdksModel;
    private DotnetReleaseSource? _dotnetReleases;
    private DotnetSdkFacts? _sdkFacts;
    private bool _sdksBusy;
    private string? _sdksReleaseTime;
    private string? _sdksReleaseProblem;
    private SdkRequest? _sdkRequest;
    private bool _selectRequest;

    /// <summary>
    /// What a prompt asked the manager for (ADR 0113): the exact SDK a solution's <c>global.json</c> pins, or the
    /// newest SDK of a channel (no SDK at all, or the IDE's own runtime missing), with the reason the row shows.
    /// </summary>
    internal sealed record SdkRequest(ToolStatus Tool, string? Version, string? Channel, string Reason, bool ReloadSolution);

    public sealed class DotnetSdksContent
    {
        internal DotnetSdksContent(ShellHost shell) => Shell = shell;

        internal ShellHost Shell { get; }

        public DotnetSdksViewModel Model => Shell.DotnetSdksModel;
    }

    /// <summary>The window's lists, selection and details.</summary>
    internal DotnetSdksViewModel DotnetSdksModel => _dotnetSdksModel ??= new DotnetSdksViewModel(new DotnetSdksActions(this));

    /// <summary>What the last change did, shown above the lists until it's dismissed or replaced.</summary>
    private string? SdksMessage
    {
        get => DotnetSdksModel.Message;
        set => DotnetSdksModel.Message = value;
    }

    /// <summary>A view restored with the layout: OpenDotnetSdks didn't read the folders, so it reads them now.</summary>
    internal void EnsureDotnetSdksRead()
    {
        if (!DotnetSdksLoaded)
        {
            _ = RunSdkActionAsync(() => RefreshDotnetSdksAsync());
        }
    }

    /// <summary>Whether the window has read the .NET folders once (the smoke step waits for it).</summary>
    internal bool DotnetSdksLoaded => _sdkFacts is not null;

    /// <summary>Whether a change or a refresh is running (the buttons are off meanwhile).</summary>
    internal bool DotnetSdksBusy => _sdksBusy;

    private DotnetSdksContent DotnetSdksPage() => _dotnetSdksContent ??= new DotnetSdksContent(this);

    /// <summary>Tests: the machine the manager reads in place of this one.</summary>
    internal IDotnetMachine? SdkMachineStandIn { get; set; }

    /// <summary>Tests: the .NET folders to read, in place of <see cref="EnvironmentCheck.KnownRoots"/>, and the one in use.</summary>
    internal Func<(IReadOnlyList<string> Roots, string? InUse)>? SdkRootsStandIn { get; set; }

    /// <summary>Tests: runs an install, update or removal command in place of the real process (exit code and output).</summary>
    internal ProcessRunner? SdkCommandStandIn { get; set; }

    /// <summary>Microsoft's release metadata, cached in the cache folder (<c>CODALUS_DOTNET_RELEASES</c> points it elsewhere).</summary>
    internal DotnetReleaseSource DotnetReleases
    {
        get => _dotnetReleases ??= new DotnetReleaseSource(Path.Combine(Paths.Cache, "dotnet-releases"), Environment.GetEnvironmentVariable(DotnetReleaseSource.LocationVariable));
        set => _dotnetReleases = value;
    }

    /// <summary>
    /// A prompt's Install… for .NET (a solution's <c>global.json</c> SDK, no SDK, the IDE's runtime): the SDK manager,
    /// opened with what's needed selected at the top of Available, where the user installs it (ADR 0113, the
    /// maintainer's choice of 2026-11-26). Without the release metadata to name a version (offline, no cache), the
    /// direct install of Check Environment runs, as before. Other tools install directly.
    /// </summary>
    internal async Task InstallFromPromptAsync(ToolStatus tool)
    {
        if (tool.Id is not ("dotnet" or "dotnet-runtime"))
        {
            await InstallToolAsync(tool).ConfigureAwait(true);
            return;
        }

        var request = RequestFor(tool);
        _sdkRequest = request;
        _selectRequest = true;
        Layout.ShowToolWindow(DotnetSdksWindow, focus: true);
        await RunSdkActionAsync(() => RefreshDotnetSdksAsync()).ConfigureAwait(true);
        if (_sdkRequest == request && RequestedRow(request) is null)
        {
            _sdkRequest = null;
            Workbench.Log.AppendLine($"[sdks] No release information names the SDK to install for {tool.Title}: installing it as Check Environment does.");
            Fill();
            await InstallToolAsync(tool).ConfigureAwait(true);
        }
    }

    /// <summary>What a prompt's tool asks the manager for.</summary>
    internal static SdkRequest RequestFor(ToolStatus tool) => tool switch
    {
        { Id: "dotnet", State: ToolState.Mismatch, WantedVersion: { } pinned } =>
            new(tool, pinned, null, Localizer.Get("msg.sdks.askedByGlobalJson", "The open solution's global.json asks for it"), ReloadSolution: true),
        { Id: "dotnet-runtime" } =>
            new(tool, null, tool.WantedChannel ?? InstallPlans.HostChannel, Localizer.Get("msg.sdks.neededByIde", "Codalus IDE's helper processes need its runtime"), ReloadSolution: false),
        _ => new(tool, null, InstallPlans.DotnetChannel(tool), Localizer.Get("msg.sdks.neededSdk", "Codalus IDE needs a .NET SDK"), ReloadSolution: false),
    };

    /// <summary>The row of a request, when the metadata names its version (a pinned one needs none) and it isn't installed yet.</summary>
    private AvailableDotnet? RequestedRow(SdkRequest request)
    {
        if (_sdkFacts is not { } facts || (request.Version ?? (request.Channel is { } channel ? facts.ChannelInfo(channel)?.LatestSdk : null)) is not { } version
            || facts.Installations.Any(i => i.Sdks.Contains(version, StringComparer.OrdinalIgnoreCase)))
        {
            return null;
        }

        return new AvailableDotnet(DotnetSdkPlanner.ChannelOf(version), AvailableDotnet.Sdk, version)
        {
            Support = request.Reason,
            Preview = version.Contains('-', StringComparison.Ordinal),
        };
    }

    private IDotnetMachine SdkMachine => SdkMachineStandIn ?? new LocalDotnetMachine(Workbench.Platform);

    private void InitializeDotnetSdks()
    {
        Workbench.ToolWindows.Register(new ToolWindowDescriptor(DotnetSdksWindow, ".NET SDKs", ToolWindowLocation.Document, ToolWindowKind.BuiltIn, ContributionSource.Shell) { Icon = "package" });
        Workbench.Commands.Register(
            new CommandDescriptor("dotnetSdks.show", ".NET SDKs…", ContributionSource.Shell) { Category = "Tools" },
            (_, _) =>
            {
                Ui.Post(OpenDotnetSdks);
                return Task.FromResult<JsonElement?>(null);
            });
        Workbench.Menus.Add([new MenuItemRule(MenuLocations.MenuBar("tools"), "dotnetSdks.show", ContributionSource.Shell) { Group = "2-templates", Order = 5 }]);
    }

    /// <summary>Opens the SDK manager (Tools → .NET SDKs…, Check Environment's Manage…, Project → .NET SDKs…' link).</summary>
    /// <remarks>Opening it reads the folders and the release metadata again (ADR 0113: the metadata is fetched when the window opens).</remarks>
    public void OpenDotnetSdks()
    {
        Layout.ShowToolWindow(DotnetSdksWindow, focus: true);
        _ = RunSdkActionAsync(() => RefreshDotnetSdksAsync());
    }

    /// <summary>
    /// Reads the machine's .NET folders and Microsoft's release metadata (<paramref name="refreshMetadata"/>: asked again
    /// even when the cache is fresh), off the UI thread, then redraws.
    /// </summary>
    internal async Task RefreshDotnetSdksAsync(bool refreshMetadata = false)
    {
        var machine = SdkMachine;
        var (roots, inUse) = SdkRootsStandIn?.Invoke() ?? (EnvironmentCheck.KnownRoots(), EnvironmentCheck.FindSdkRoot());
        var installations = await Task.Run(() => DotnetInstallations.Read(machine, roots, inUse)).ConfigureAwait(true);

        IReadOnlyList<DotnetChannel> channels = [];
        var releases = new Dictionary<string, DotnetChannelReleases>(StringComparer.Ordinal);
        string? when = null;
        string? problem = null;
        try
        {
            var index = await Task.Run(() => DotnetReleases.GetIndexAsync(refreshMetadata, _lifetime.Token)).ConfigureAwait(true);
            channels = index.Value;
            when = index.FetchedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            problem = index.FetchError is { } error
                ? Localizer.Format("msg.sdks.metadataStale", "Microsoft's release information couldn't be refreshed ({0}): this is the copy of {1}.", error, when)
                : null;
            var wanted = installations.SelectMany(i => i.Sdks.Concat(i.Runtimes.Select(r => r.Version))).Select(DotnetSdkPlanner.ChannelOf)
                .Distinct(StringComparer.Ordinal).Where(c => channels.Any(x => x.Version == c)).ToList();
            var read = await Task.WhenAll(wanted.Select(async channel =>
            {
                try
                {
                    return (channel, (await Task.Run(() => DotnetReleases.GetChannelAsync(channel, refreshMetadata, _lifetime.Token)).ConfigureAwait(false)).Value);
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    Workbench.Log.AppendLine($"[sdks] .NET {channel}'s releases couldn't be read: {ex.Message}");
                    return (channel, (DotnetChannelReleases?)null);
                }
            })).ConfigureAwait(true);
            foreach (var (channel, value) in read)
            {
                if (value is not null)
                {
                    releases[channel] = value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            problem = Localizer.Format("msg.sdks.noMetadata", "Microsoft's release information couldn't be read, so updates and what's available aren't shown: {0}", ex.Message);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }

        string? solutionSdk = null;
        if (Solutions.IsOpen)
        {
            try
            {
                solutionSdk = (await ProjectManagement.GetSdkInfoAsync(_lifetime.Token).ConfigureAwait(true)).ResolvedSdk;
            }
            catch (Exception ex) when (ViewModels.PackageManagerViewModel.IsExpected(ex) || ex is OperationCanceledException)
            {
                Workbench.Log.AppendLine($"[sdks] The open solution's SDK couldn't be read: {ex.Message}");
            }
        }

        var recent = Recent.Items;
        var pinned = await Task.Run(() => recent.Select(p => Path.GetDirectoryName(p) is { } folder && Directory.Exists(folder) ? EnvironmentCheck.GlobalJson(folder)?.Version : null)
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)).ConfigureAwait(true);
        var check = EnvironmentCheck;
        _sdkFacts = new DotnetSdkFacts(machine, installations, DateOnly.FromDateTime(DateTime.Today))
        {
            Channels = channels,
            Releases = releases,
            Install = DotnetFacts(),
            SolutionSdk = solutionSdk,
            Pinned = pinned,
            HostsStillRun = SdkMachineStandIn is null ? (root, removed) => check.HostsStillRunWithout(root, [.. removed.Select(r => (r.Framework, r.Version))]) : null,
        };
        _sdksReleaseTime = when;
        _sdksReleaseProblem = problem;
        Fill();
    }

    private void Fill()
    {
        if (_sdkFacts is not { } facts)
        {
            return;
        }

        var model = DotnetSdksModel;
        var requested = _sdkRequest is { } request ? RequestedRow(request) : null;
        var select = requested is not null && _selectRequest;
        if (select)
        {
            _selectRequest = false;
        }

        model.SetData(new DotnetSdksData(facts, DotnetSdkPlanner.InstalledSdks(facts), DotnetSdkPlanner.InstalledRuntimes(facts), DotnetSdkPlanner.Available(facts, model.ShowPreviews, model.ShowOutOfSupport))
        {
            Requested = requested,
            SelectRequested = select,
            ReleaseInfoTime = _sdksReleaseTime,
            ReleaseInfoProblem = _sdksReleaseProblem,
        });
    }

    /// <summary>One change at a time: the buttons are off while it runs.</summary>
    private async Task RunSdkActionAsync(Func<Task> action)
    {
        if (_sdksBusy)
        {
            return;
        }

        _sdksBusy = true;
        DotnetSdksModel.IsBusy = true;
        try
        {
            await action().ConfigureAwait(true);
        }
        finally
        {
            _sdksBusy = false;
            DotnetSdksModel.IsBusy = false;
        }
    }

    private static ToolStatus SdkTool(string title) => new("dotnet", title, true, ToolState.Found, "The .NET SDK manager");

    /// <summary>The window's actions, for its view model.</summary>
    private sealed class DotnetSdksActions(ShellHost shell) : IDotnetSdksActions
    {
        public Task<DotnetSdkPreview> PreviewAsync(object item, CancellationToken cancellationToken) => shell.PreviewSdkAsync(item, cancellationToken);

        public Task InstallAsync(AvailableDotnet item, InstallPlan plan) => shell.RunSdkActionAsync(() => shell.InstallDotnetAsync(item, plan));

        public Task UpdateAsync(object item, InstallPlan plan) => item switch
        {
            InstalledSdk sdk => shell.RunSdkActionAsync(() => shell.UpdateSdkAsync(sdk, plan)),
            InstalledRuntime runtime => shell.RunSdkActionAsync(() => shell.UpdateRuntimeAsync(runtime, plan)),
            _ => Task.CompletedTask,
        };

        public Task RemoveAsync(object item) => item switch
        {
            InstalledSdk sdk => shell.RunSdkActionAsync(() => shell.RemoveSdkAsync(sdk)),
            InstalledRuntime runtime => shell.RunSdkActionAsync(() => shell.RemoveRuntimeAsync(runtime)),
            _ => Task.CompletedTask,
        };

        public Task RemoveOldPatchesAsync(InstalledSdk sdk) => shell.RunSdkActionAsync(() => shell.RemoveOldPatchesAsync(sdk));

        public Task RefreshAsync() => shell.RunSdkActionAsync(() => shell.RefreshDotnetSdksAsync(refreshMetadata: true));

        public void FiltersChanged() => shell.Fill();
    }

    /// <summary>
    /// What a selection's buttons would do, for its details: each way its install or update runs, and what Remove deletes
    /// or runs (or why it's refused). A package's folder asks which package holds it (<c>dpkg -S</c>…), off the UI thread.
    /// </summary>
    internal async Task<DotnetSdkPreview> PreviewSdkAsync(object item, CancellationToken cancellationToken)
    {
        if (_sdkFacts is not { } facts)
        {
            return new DotnetSdkPreview([], null);
        }

        switch (item)
        {
            case AvailableDotnet available:
                return new DotnetSdkPreview(DotnetSdkPlanner.InstallOptions(facts, available.Framework, available.Version), null);
            case InstalledSdk sdk:
            {
                var owner = await OwnerPackageAsync(sdk.Installation, Path.Combine(sdk.Installation.Root, "sdk", sdk.Version)).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                var plans = sdk.Update is { } to ? DotnetSdkPlanner.UpdateOptions(facts, sdk.Installation, AvailableDotnet.Sdk, sdk.Version, to, owner) : [];
                return new DotnetSdkPreview(plans, DotnetSdkPlanner.RemoveSdk(facts, sdk.Installation, sdk.Version, owner));
            }

            case InstalledRuntime runtime:
            {
                var owner = await OwnerPackageAsync(runtime.Installation, Path.Combine(runtime.Installation.Root, "shared", runtime.Runtime.Framework, runtime.Runtime.Version)).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                var plans = runtime.Update is { } to ? DotnetSdkPlanner.UpdateOptions(facts, runtime.Installation, runtime.Runtime.Framework, runtime.Runtime.Version, to, owner) : [];
                return new DotnetSdkPreview(plans, DotnetSdkPlanner.RemoveRuntime(facts, runtime.Installation, runtime.Runtime, owner));
            }

            default:
                return new DotnetSdkPreview([], null);
        }
    }

    /// <summary>Install, the way the details' chosen option says: it runs without another question, since the details showed its command.</summary>
    private async Task InstallDotnetAsync(AvailableDotnet available, InstallPlan chosen)
    {
        if (await RunSdkPlanAsync(SdkTool(available.Title), [chosen], confirmed: true).ConfigureAwait(true) is not { } plan)
        {
            return;
        }

        var request = _sdkRequest is { } asked && RequestedRow(asked) is { } row && row.Version == available.Version ? asked : null;
        if (request is not null)
        {
            _sdkRequest = null;
        }

        SdksMessage = Localizer.Format("msg.sdks.installed", "{0} is installed.", available.Title);
        await AfterSdkChangeAsync(plan).ConfigureAwait(true);
        if (request is { ReloadSolution: true } && Solutions.IsOpen)
        {
            // The build host chose another SDK when the solution opened: a reload builds it with this one.
            var reload = Localizer.Get("msg.sdks.reloadSolution", "Reload Solution");
            var offer = Workbench.Notifications.Info(Localizer.Format("msg.sdks.reloadOffer", "{0} is installed. Reload the solution so that it builds with it.", available.Title), reload);
            _ = offer.Result.ContinueWith(t =>
            {
                if (t.Result == reload)
                {
                    Ui.Post(() => _ = ReopenSolutionAsync());
                }
            }, TaskScheduler.Default);
        }

        if (available.IsSdk)
        {
            OfferWorkloads(available.Version);
        }
    }

    private async Task UpdateSdkAsync(InstalledSdk row, InstallPlan chosen)
    {
        if (row.Update is not { } newVersion || await RunSdkPlanAsync(SdkTool($".NET SDK {newVersion}"), [chosen], confirmed: true).ConfigureAwait(true) is not { } plan)
        {
            return;
        }

        SdksMessage = Localizer.Format("msg.sdks.updated", ".NET SDK {0} is installed.", newVersion);
        await AfterSdkChangeAsync(plan).ConfigureAwait(true);

        // Side by side (the user's folders, the .pkg, winget): the old patch is still there, so offer it (ADR 0113).
        if (_sdkFacts?.Installations.FirstOrDefault(i => i.Root == row.Installation.Root) is { } after && after.Sdks.Contains(row.Version, StringComparer.OrdinalIgnoreCase))
        {
            if (_sdkFacts.Pinned.Contains(row.Version))
            {
                SdksMessage += " " + Localizer.Format("msg.sdks.keptPinned", ".NET SDK {0} stays: a recent solution's global.json pins it.", row.Version);
                return;
            }

            var owner = await OwnerPackageAsync(after, Path.Combine(after.Root, "sdk", row.Version)).ConfigureAwait(true);
            await RemoveAsync(after, DotnetSdkPlanner.RemoveSdk(_sdkFacts, after, row.Version, owner), afterUpdate: newVersion).ConfigureAwait(true);
        }
    }

    private async Task UpdateRuntimeAsync(InstalledRuntime row, InstallPlan chosen)
    {
        if (row.Update is not { } newVersion)
        {
            return;
        }

        var title = $"{DotnetFrameworks.Title(row.Runtime.Framework)} {newVersion}";
        if (await RunSdkPlanAsync(SdkTool(title), [chosen], confirmed: true).ConfigureAwait(true) is { } plan)
        {
            SdksMessage = Localizer.Format("msg.sdks.installed", "{0} is installed.", title);
            await AfterSdkChangeAsync(plan).ConfigureAwait(true);
        }
    }

    private async Task RemoveSdkAsync(InstalledSdk row)
    {
        if (_sdkFacts is not { } facts)
        {
            return;
        }

        var owner = await OwnerPackageAsync(row.Installation, Path.Combine(row.Installation.Root, "sdk", row.Version)).ConfigureAwait(true);
        await RemoveAsync(row.Installation, DotnetSdkPlanner.RemoveSdk(facts, row.Installation, row.Version, owner)).ConfigureAwait(true);
    }

    private async Task RemoveOldPatchesAsync(InstalledSdk row)
    {
        if (_sdkFacts is not { } facts)
        {
            return;
        }

        var old = DotnetSdkPlanner.OldPatches(row.Installation, row.Channel).Where(s => !facts.Pinned.Contains(s)).ToList();
        if (old.Count == 0)
        {
            SdksMessage = Localizer.Format("msg.sdks.noOldPatches", "Every .NET {0} SDK here is the newest of its band, or pinned by a recent solution's global.json.", row.Channel);
            return;
        }

        await RemoveAsync(row.Installation, DotnetSdkPlanner.RemoveSdks(facts, row.Installation, old)).ConfigureAwait(true);
    }

    private async Task RemoveRuntimeAsync(InstalledRuntime row)
    {
        if (_sdkFacts is not { } facts)
        {
            return;
        }

        var owner = await OwnerPackageAsync(row.Installation, Path.Combine(row.Installation.Root, "shared", row.Runtime.Framework, row.Runtime.Version)).ConfigureAwait(true);
        await RemoveAsync(row.Installation, DotnetSdkPlanner.RemoveRuntime(facts, row.Installation, row.Runtime, owner)).ConfigureAwait(true);
    }

    /// <summary>
    /// A removal, once the user agrees to what it lists: the folders deleted here (the user's folders, each checked to be
    /// inside the .NET folder), or its command run. After an update the dialog says what replaces it.
    /// </summary>
    private async Task RemoveAsync(DotnetInstallation installation, DotnetRemoval removal, string? afterUpdate = null)
    {
        if (removal.Refused is { } refused)
        {
            SdksMessage = refused;
            if (afterUpdate is null)
            {
                Workbench.Notifications.Warning(refused);
            }

            return;
        }

        var lines = new List<string>();
        if (afterUpdate is not null)
        {
            lines.Add(Localizer.Format("msg.sdks.replacedBy", "{0} replaces it.", afterUpdate));
        }

        lines.Add(removal.Command is { } command
            ? Localizer.Format("msg.sdks.thisRuns", "This runs:\n{0}", command.Command)
            : Localizer.Format("msg.sdks.thisDeletes", "This deletes:\n{0}", string.Join("\n", removal.Folders)));
        lines.AddRange(removal.Kept);
        var title = Localizer.Format("msg.sdks.removeTitle", "Remove {0}?", removal.What);
        var action = removal.Command?.Kind == InstallKind.Terminal ? Localizer.Get("msg.setup.runInTerminal", "Run in Terminal") : Localizer.Get("msg.sdks.removeAction", "Remove");
        if (!await Dialogs.ConfirmAsync(title, string.Join("\n\n", lines), action, danger: true).ConfigureAwait(true))
        {
            return;
        }

        SetupLog.AppendLine($"[{DateTime.Now:T}] Removing {removal.What}: {removal.Command?.Command ?? string.Join(", ", removal.Folders)}");
        if (removal.Command is { } plan)
        {
            if (await RunSdkPlanAsync(SdkTool(removal.What), [plan], confirmed: true).ConfigureAwait(true) is null)
            {
                return;
            }
        }
        else
        {
            var platform = SdkMachine.Platform;
            var machine = SdkMachine;
            var failed = await Task.Run(() =>
            {
                var errors = new List<string>();
                foreach (var folder in removal.Folders)
                {
                    if (!DotnetInstallations.IsUnder(folder, installation.Root, platform))
                    {
                        errors.Add($"{folder} isn't inside {installation.Root}, so it was left alone.");
                        continue;
                    }

                    try
                    {
                        machine.DeleteDirectory(folder);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        errors.Add($"{folder}: {ex.Message}");
                    }
                }

                return errors;
            }).ConfigureAwait(true);
            foreach (var error in failed)
            {
                SetupLog.AppendLine(error);
            }

            if (failed.Count > 0)
            {
                SdksMessage = Localizer.Format("msg.sdks.removeFailed", "{0} was removed in part: {1}", removal.What, string.Join(" ", failed));
                await AfterSdkChangeAsync(null).ConfigureAwait(true);
                return;
            }
        }

        SdksMessage = (afterUpdate is null ? string.Empty : SdksMessage + " ") + Localizer.Format("msg.sdks.removed", "{0} is removed.", removal.What);
        await AfterSdkChangeAsync(null).ConfigureAwait(true);
    }

    /// <summary>
    /// Runs one of <paramref name="plans"/>: the user picks one when there are several, or agrees to the only one (unless
    /// <paramref name="confirmed"/> already); null when nothing ran or it failed (the message says so).
    /// </summary>
    private async Task<InstallPlan?> RunSdkPlanAsync(ToolStatus tool, IReadOnlyList<InstallPlan> plans, bool confirmed = false)
    {
        if (plans.Count == 0)
        {
            SdksMessage = Localizer.Format("msg.setup.noPlan", "There's no way to install {0} from here on this system.", tool.Title);
            return null;
        }

        var ran = await PickAndRunInstallAsync(
            tool,
            plans,
            confirmed ? _ => Task.FromResult(true) : null,
            SdkCommandStandIn,
            null).ConfigureAwait(true);
        if (ran is not var (plan, code))
        {
            return null;
        }

        if (code != 0)
        {
            SdksMessage = Localizer.Format("msg.sdks.failed", "{0} failed (exit code {1}). The Environment Setup output has the details.", plan.Summary, code);
            return null;
        }

        return plan;
    }

    /// <summary>After a change: the IDE moves to the .NET it should use now (an install into a new folder), checks again, and lists again.</summary>
    private async Task AfterSdkChangeAsync(InstallPlan? plan)
    {
        if (SdkMachineStandIn is null)
        {
            if (plan?.AddsToPath is { } folder && EnvironmentCheck.FindSdkRoot() is var current && (current is null || !DotnetInstallations.SameFolder(current, folder, Workbench.Platform)) && Directory.Exists(Path.Combine(folder, "sdk")))
            {
                AddDotnetToEnvironment(folder);
            }

            // The environment read again and the .NET folder chosen again, then every tool checked; the IDE's hosts that
            // couldn't start for want of their runtime start again when it's there now (as after Check Environment's installs).
            await RecheckEnvironmentAsync().ConfigureAwait(true);
        }

        await RefreshDotnetSdksAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// After a new SDK: the workloads of an older band of its channel, offered for its band (ADR 0113), never installed
    /// without the user's yes (a MAUI install is gigabytes).
    /// </summary>
    private void OfferWorkloads(string sdk)
    {
        if (_sdkFacts?.Installations.FirstOrDefault(i => i.Sdks.Contains(sdk, StringComparer.OrdinalIgnoreCase)) is not { } installation
            || DotnetSdkPlanner.WorkloadsToCarry(_sdkFacts.Machine, installation, sdk) is not { Count: > 0 } workloads)
        {
            return;
        }

        var names = string.Join(' ', workloads);
        var dotnet = Path.Combine(installation.Root, Workbench.Platform == PlatformKind.Windows ? "dotnet.exe" : "dotnet");
        // Pinned to the new SDK through a global.json in a folder of its own: the workloads go to its band.
        var command = Workbench.Platform == PlatformKind.Windows
            ? $"\"{dotnet}\" workload install {names}"
            : $"cd \"$(mktemp -d)\" && printf '{{\"sdk\":{{\"version\":\"{sdk}\"}}}}' > global.json && \"{dotnet}\" workload install {names}";
        var plan = new InstallPlan("dotnet", InstallKind.Terminal, Localizer.Format("msg.sdks.workloadsSummary", "The workloads {0} for .NET SDK {1}", string.Join(", ", workloads), sdk), command);
        var install = Localizer.Get("msg.sdks.installWorkloads", "Install Workloads…");
        var offer = Workbench.Notifications.Info(Localizer.Format("msg.sdks.workloadsOffer", "Workloads belong to an SDK's feature band, and .NET SDK {0}'s has none yet. Install the ones an older band has ({1})?", sdk, string.Join(", ", workloads)), install);
        _ = offer.Result.ContinueWith(t =>
        {
            if (t.Result == install)
            {
                Ui.Post(() => _ = RunSdkPlanAsync(SdkTool(plan.Summary), [plan]));
            }
        }, TaskScheduler.Default);
    }

    /// <summary>The distribution's package that holds a path, for a package's folder; null elsewhere or when it can't be told.</summary>
    private async Task<string?> OwnerPackageAsync(DotnetInstallation installation, string path)
    {
        if (installation.Owner != DotnetRootOwner.LinuxPackage || DotnetSdkPlanner.PackageOwnerQuery(_sdkFacts?.Install.LinuxDistribution, path) is not (string program, string[] arguments))
        {
            return null;
        }

        var (code, output) = SdkCommandStandIn is { } standIn
            ? await standIn(program, arguments, null, CancellationToken.None).ConfigureAwait(true)
            : EnvironmentCheck.FindOnPath(program) is { } found
                ? await EnvironmentCheck.RunProcessAsync(found, arguments, null, TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(true)
                : (-1, string.Empty);
        return code == 0 ? DotnetSdkPlanner.PackageOwner(output) : null;
    }
}
