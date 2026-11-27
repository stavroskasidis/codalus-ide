using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Sdks;
using Codalus.Workbench.Setup;

namespace Codalus.Shell.ViewModels;

/// <summary>The SDK manager's three lists (ADR 0113).</summary>
public enum DotnetSdksTab
{
    Installed,
    Updates,
    Available,
}

/// <summary>What the shell read for the SDK manager: the facts, and the lists the planner made of them.</summary>
public sealed record DotnetSdksData(DotnetSdkFacts Facts, IReadOnlyList<InstalledSdk> Sdks, IReadOnlyList<InstalledRuntime> Runtimes, IReadOnlyList<AvailableDotnet> Available)
{
    /// <summary>The row a prompt asked for (ADR 0113): shown first in Available.</summary>
    public AvailableDotnet? Requested { get; init; }

    /// <summary>Show Available with <see cref="Requested"/> selected (once, when the prompt opens the window).</summary>
    public bool SelectRequested { get; init; }

    /// <summary>When the release information is from (local time, short), or null when there's none.</summary>
    public string? ReleaseInfoTime { get; init; }

    /// <summary>Why the release information is old or missing; null when it's fresh.</summary>
    public string? ReleaseInfoProblem { get; init; }
}

/// <summary>What a selection's buttons do: the ways its install or update runs, and what Remove does.</summary>
public sealed record DotnetSdkPreview(IReadOnlyList<InstallPlan> Plans, DotnetRemoval? Removal);

/// <summary>What the SDK manager asks the shell to do: everything that reads a process or changes the machine.</summary>
public interface IDotnetSdksActions
{
    /// <summary>The plans of a selection (<see cref="InstalledSdk"/>, <see cref="InstalledRuntime"/> or <see cref="AvailableDotnet"/>).</summary>
    Task<DotnetSdkPreview> PreviewAsync(object item, CancellationToken cancellationToken);

    Task InstallAsync(AvailableDotnet item, InstallPlan plan);

    /// <summary>Updates an <see cref="InstalledSdk"/> or an <see cref="InstalledRuntime"/> the way <paramref name="plan"/> says.</summary>
    Task UpdateAsync(object item, InstallPlan plan);

    /// <summary>Removes an <see cref="InstalledSdk"/> or an <see cref="InstalledRuntime"/>, after the confirmation.</summary>
    Task RemoveAsync(object item);

    Task RemoveOldPatchesAsync(InstalledSdk sdk);

    Task RefreshAsync();

    /// <summary>Show previews or Show out of support changed: the shell lists what's available again.</summary>
    void FiltersChanged();
}

/// <summary>
/// Tools → .NET SDKs (ADR 0113, layout B of <c>docs/design/controls/DotnetSdks.dc.html</c>): Installed, Updates and
/// Available over one list grouped by .NET folder, and the selection's details with the exact command its button runs.
/// Install and Update run from the details' button (the command is on screen); Remove still confirms.
/// </summary>
public sealed class DotnetSdksViewModel : ObservableObject, IDisposable
{
    private readonly IDotnetSdksActions _actions;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private DotnetSdksData? _data;
    private DotnetSdksTab _tab;
    private string _search = string.Empty;
    private bool _showPreviews;
    private bool _showOutOfSupport;
    private DotnetSdkRowViewModel? _selected;
    private DotnetSdkDetailsViewModel? _details;
    private bool _isBusy;
    private string? _message;
    private CancellationTokenSource? _preview;
    private bool _rebuilding;

    public DotnetSdksViewModel(IDotnetSdksActions actions)
    {
        _actions = actions;
        RefreshCommand = new RelayCommand(() => _ = _actions.RefreshAsync(), () => !IsBusy);
        DismissMessageCommand = new RelayCommand(() => Message = null);
        ToggleGroupCommand = new RelayCommand(p =>
        {
            if (p is DotnetSdkRowViewModel row)
            {
                ToggleGroup(row);
            }
        });
    }

    public ObservableCollection<DotnetSdkRowViewModel> Rows { get; } = [];

    public ICommand RefreshCommand { get; }

    public ICommand DismissMessageCommand { get; }

    public ICommand ToggleGroupCommand { get; }

    public DotnetSdksTab Tab
    {
        get => _tab;
        set
        {
            if (SetField(ref _tab, value))
            {
                OnPropertyChanged(nameof(TabIndex));
                OnPropertyChanged(nameof(IsAvailableTab));
                Rebuild(keepSelection: false);
            }
        }
    }

    /// <summary>The tab strip's index.</summary>
    public int TabIndex
    {
        get => (int)_tab;
        set => Tab = (DotnetSdksTab)Math.Clamp(value, 0, 2);
    }

    public bool IsAvailableTab => _tab == DotnetSdksTab.Available;

    public string Search
    {
        get => _search;
        set
        {
            if (SetField(ref _search, value ?? string.Empty))
            {
                Rebuild(keepSelection: true);
            }
        }
    }

    public bool ShowPreviews
    {
        get => _showPreviews;
        set
        {
            if (SetField(ref _showPreviews, value))
            {
                _actions.FiltersChanged();
            }
        }
    }

    public bool ShowOutOfSupport
    {
        get => _showOutOfSupport;
        set
        {
            if (SetField(ref _showOutOfSupport, value))
            {
                _actions.FiltersChanged();
            }
        }
    }

    /// <summary>The folders haven't been read yet.</summary>
    public bool IsLoading => _data is null;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetField(ref _isBusy, value))
            {
                ((RelayCommand)RefreshCommand).RaiseCanExecuteChanged();
                _details?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>What the last change did (installed, removed, failed, refused), until it's dismissed or replaced.</summary>
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

    public bool HasMessage => !string.IsNullOrEmpty(_message);

    public int InstalledCount => _data is { } d ? d.Sdks.Count + d.Runtimes.Count : 0;

    public int UpdatesCount => _data is { } d ? d.Sdks.Count(s => s.Update is not null) + d.Runtimes.Count(r => r.Update is not null) : 0;

    public bool HasUpdates => UpdatesCount > 0;

    /// <summary>"Release information from …", beside Refresh.</summary>
    public string ReleaseInfo => _data?.ReleaseInfoTime is { } when
        ? Localizer.Format("msg.sdks.releaseInfoFrom", "Release information from {0}", when)
        : string.Empty;

    public string ReleaseInfoTip => Localizer.Get("msg.sdks.releaseInfoTip", "Updates and what's available come from Microsoft's release information, read when this window opens and on Refresh, and kept for a day.");

    /// <summary>Why the release information is old or missing.</summary>
    public string? ReleaseInfoProblem => _data?.ReleaseInfoProblem;

    public bool HasReleaseInfoProblem => !string.IsNullOrEmpty(ReleaseInfoProblem);

    /// <summary>Where Available's installs go.</summary>
    public string InstallsInto => _data?.Facts.Target is { } target
        ? Localizer.Format("msg.sdks.installsIntoShort", "Installs go into {0}, managed by {1}.", target.Root, OwnerText(target.Owner))
        : _data is { } d ? Localizer.Format("msg.sdks.installsIntoNew", "Installs go into {0}.", DotnetSdkPlanner.UserRoot(d.Facts)) : string.Empty;

    public bool IsEmpty => !IsLoading && Rows.Count == 0;

    public string EmptyText => _tab switch
    {
        _ when _search.Length > 0 => Localizer.Format("msg.sdks.noMatch", "Nothing here matches \"{0}\".", _search),
        DotnetSdksTab.Installed => Localizer.Get("msg.sdks.none", "No .NET is installed where Codalus IDE looks for it. Install an SDK from Available."),
        DotnetSdksTab.Updates when _data?.Facts.Channels.Count == 0 => Localizer.Get("msg.sdks.noUpdatesWithoutInfo", "Updates need Microsoft's release information, which couldn't be read."),
        DotnetSdksTab.Updates => Localizer.Get("msg.sdks.allUpToDate", "Every SDK and runtime here is up to date."),
        _ => Localizer.Get("msg.sdks.nothingAvailable", "Every supported .NET is installed. Show previews or Show out of support list more."),
    };

    public DotnetSdkRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (value is { IsSelectable: false })
            {
                // A folder's heading: the selection stays where it was (the list puts it back).
                OnPropertyChanged();
                return;
            }

            if (SetField(ref _selected, value) && !_rebuilding)
            {
                ShowDetails();
            }
        }
    }

    public DotnetSdkDetailsViewModel? Details
    {
        get => _details;
        private set
        {
            if (SetField(ref _details, value))
            {
                OnPropertyChanged(nameof(HasDetails));
            }
        }
    }

    public bool HasDetails => _details is not null;

    /// <summary>Tests: the preview of the current selection has been read.</summary>
    internal Task PreviewTask { get; private set; } = Task.CompletedTask;

    public void Dispose()
    {
        _preview?.Cancel();
        _preview?.Dispose();
        _preview = null;
    }

    /// <summary>The shell read the folders and the release information again.</summary>
    public void SetData(DotnetSdksData data)
    {
        var requested = data.SelectRequested ? data.Requested : null;
        _data = data;
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(InstalledCount));
        OnPropertyChanged(nameof(UpdatesCount));
        OnPropertyChanged(nameof(HasUpdates));
        OnPropertyChanged(nameof(ReleaseInfo));
        OnPropertyChanged(nameof(ReleaseInfoProblem));
        OnPropertyChanged(nameof(HasReleaseInfoProblem));
        OnPropertyChanged(nameof(InstallsInto));
        if (requested is not null)
        {
            // What a prompt asked for: Available, with it selected.
            _search = string.Empty;
            OnPropertyChanged(nameof(Search));
            if (SetField(ref _tab, DotnetSdksTab.Available, nameof(Tab)))
            {
                OnPropertyChanged(nameof(TabIndex));
                OnPropertyChanged(nameof(IsAvailableTab));
            }

            Rebuild(keepSelection: false, select: Key(requested));
            return;
        }

        Rebuild(keepSelection: true);
    }

    /// <summary>Selects a row by its key (tests, the smoke step), opening its runtimes' group when it's in one.</summary>
    internal bool Select(string key)
    {
        if (Rows.FirstOrDefault(r => r.Key == key) is { } row)
        {
            Selected = row;
            return true;
        }

        if (_tab == DotnetSdksTab.Installed && key.StartsWith("rt|", StringComparison.Ordinal) && _data?.Runtimes.FirstOrDefault(r => Key(r) == key) is { } runtime)
        {
            _expanded.Add(runtime.Installation.Root);
            Rebuild(keepSelection: false, select: key);
            return _selected?.Key == key;
        }

        return false;
    }

    /// <summary>The details' main button: Install, or Update to the newer patch, the way the chosen option says.</summary>
    internal async Task PrimaryAsync()
    {
        if (_details is not { Plan: { } plan } details || _isBusy)
        {
            return;
        }

        switch (details.Item)
        {
            case AvailableDotnet available:
                await _actions.InstallAsync(available, plan).ConfigureAwait(true);
                break;
            case InstalledSdk or InstalledRuntime:
                await _actions.UpdateAsync(details.Item, plan).ConfigureAwait(true);
                break;
        }
    }

    internal async Task RemoveAsync()
    {
        if (_details is { CanRemove: true, Item: { } item } && !_isBusy)
        {
            await _actions.RemoveAsync(item).ConfigureAwait(true);
        }
    }

    internal async Task RemoveOldPatchesAsync()
    {
        if (_details is { HasOldPatches: true, Item: InstalledSdk sdk } && !_isBusy)
        {
            await _actions.RemoveOldPatchesAsync(sdk).ConfigureAwait(true);
        }
    }

    internal void ToggleGroup(DotnetSdkRowViewModel row)
    {
        if (row.Kind != DotnetSdkRowKind.RuntimeGroup || row.Installation is not { } installation)
        {
            return;
        }

        if (!_expanded.Add(installation.Root))
        {
            _expanded.Remove(installation.Root);
        }

        Rebuild(keepSelection: true);
    }

    internal static string Key(object item) => item switch
    {
        InstalledSdk s => $"sdk|{s.Installation.Root}|{s.Version}",
        InstalledRuntime r => $"rt|{r.Installation.Root}|{r.Runtime.Framework}|{r.Runtime.Version}",
        AvailableDotnet a => $"av|{a.Framework}|{a.Version}",
        _ => string.Empty,
    };

    internal static string OwnerText(DotnetRootOwner owner) => owner switch
    {
        DotnetRootOwner.User => Localizer.Get("msg.sdks.ownerUser", "you"),
        DotnetRootOwner.LinuxPackage => Localizer.Get("msg.sdks.ownerPackage", "the distribution's packages"),
        DotnetRootOwner.Snap => Localizer.Get("msg.sdks.ownerSnap", "the dotnet-sdk snap"),
        DotnetRootOwner.MacPackage => Localizer.Get("msg.sdks.ownerPkg", "Microsoft's installer"),
        DotnetRootOwner.WindowsInstaller => Localizer.Get("msg.sdks.ownerWindows", "Microsoft's installer or winget"),
        _ => Localizer.Get("msg.sdks.ownerAdministrator", "an administrator"),
    };

    private void Rebuild(bool keepSelection, string? select = null)
    {
        var keep = select ?? (keepSelection ? _selected?.Key : null);
        _rebuilding = true;
        try
        {
            Rows.Clear();
            if (_data is { } data)
            {
                foreach (var row in BuildRows(data))
                {
                    Rows.Add(row);
                }
            }

            var next = (keep is null ? null : Rows.FirstOrDefault(r => r.Key == keep))
                ?? Rows.FirstOrDefault(r => r is { IsSelectable: true, Item: InstalledSdk { UsedBySolution: true } })
                ?? Rows.FirstOrDefault(r => r.IsSelectable);
            SetField(ref _selected, next, nameof(Selected));
        }
        finally
        {
            _rebuilding = false;
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        if (_details?.Key != _selected?.Key || _details?.Data != _data)
        {
            ShowDetails();
        }
    }

    private bool Matches(string text) => _search.Length == 0 || text.Contains(_search, StringComparison.OrdinalIgnoreCase);

    private IEnumerable<DotnetSdkRowViewModel> BuildRows(DotnetSdksData data)
    {
        var facts = data.Facts;
        if (_tab == DotnetSdksTab.Available)
        {
            var available = data.Requested is { } requested
                ? [requested, .. data.Available.Where(a => !(a.Framework == requested.Framework && a.Version == requested.Version))]
                : data.Available;
            foreach (var a in available)
            {
                var row = DotnetSdkRowViewModel.ForAvailable(a, a == data.Requested);
                if (Matches(row.Title + " " + row.Detail))
                {
                    yield return row;
                }
            }

            yield break;
        }

        var updatesOnly = _tab == DotnetSdksTab.Updates;
        foreach (var installation in facts.Installations)
        {
            var sdks = data.Sdks.Where(s => s.Installation == installation && (!updatesOnly || s.Update is not null))
                .Select(s => DotnetSdkRowViewModel.ForSdk(facts, s)).Where(r => Matches(r.Title + " " + r.Detail)).ToList();
            var runtimes = data.Runtimes.Where(r => r.Installation == installation && (!updatesOnly || r.Update is not null))
                .Select(r => DotnetSdkRowViewModel.ForRuntime(r, indented: !updatesOnly)).Where(r => Matches(r.Title + " " + r.Detail)).ToList();
            if (sdks.Count == 0 && runtimes.Count == 0 && (updatesOnly || _search.Length > 0))
            {
                continue;
            }

            yield return DotnetSdkRowViewModel.ForFolder(installation);
            foreach (var sdk in sdks)
            {
                yield return sdk;
            }

            if (runtimes.Count == 0)
            {
                continue;
            }

            if (updatesOnly)
            {
                foreach (var runtime in runtimes)
                {
                    yield return runtime;
                }

                continue;
            }

            // Installed: the runtimes folded under their folder, open while a search is typed or once opened.
            var open = _search.Length > 0 || _expanded.Contains(installation.Root);
            yield return DotnetSdkRowViewModel.ForRuntimeGroup(installation, data.Runtimes.Where(r => r.Installation == installation).ToList(), open);
            if (open)
            {
                foreach (var runtime in runtimes)
                {
                    yield return runtime;
                }
            }
        }
    }

    private void ShowDetails()
    {
        Dispose();
        if (_selected is not { } row || _data is not { } data)
        {
            Details = null;
            return;
        }

        var details = new DotnetSdkDetailsViewModel(this, data, row);
        Details = details;
        if (row.Item is InstalledSdk or InstalledRuntime or AvailableDotnet)
        {
            var cts = _preview = new CancellationTokenSource();
            PreviewTask = LoadPreviewAsync(details, row.Item, cts.Token);
        }
        else
        {
            PreviewTask = Task.CompletedTask;
        }
    }

    private async Task LoadPreviewAsync(DotnetSdkDetailsViewModel details, object item, CancellationToken cancellationToken)
    {
        try
        {
            var preview = await _actions.PreviewAsync(item, cancellationToken).ConfigureAwait(true);
            if (!cancellationToken.IsCancellationRequested && _details == details)
            {
                details.SetPreview(preview);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}

public enum DotnetSdkRowKind
{
    Folder,
    Sdk,
    RuntimeGroup,
    Runtime,
    Available,
}

/// <summary>A chip on a row or in the details: its text and its color (information, warning, error, or plain).</summary>
public sealed record DotnetSdkChip(string Text, string Tip, string Kind)
{
    /// <summary>What the details say on it: an update's chip says what it is there ("Security update: 10.0.405").</summary>
    public string Long { get; init; } = Text;

    public bool IsInfo => Kind == "info";

    public bool IsWarning => Kind == "warning";

    public bool IsError => Kind == "error";

    public bool IsSuccess => Kind == "success";

    public bool IsSquare => Kind == "square";

    internal static DotnetSdkChip? Update(string? update, bool security) => update is null
        ? null
        : security
            ? new(update, Localizer.Format("msg.sdks.securityUpdate", "Security update: {0}", update), "warning") { Long = Localizer.Format("msg.sdks.securityUpdate", "Security update: {0}", update) }
            : new(update, Localizer.Format("msg.sdks.update", "Update: {0}", update), "info") { Long = Localizer.Format("msg.sdks.update", "Update: {0}", update) };

    internal static DotnetSdkChip OutOfSupport(string support) => new(Localizer.Get("msg.sdks.chipOutOfSupport", "Out of support"), support, "error");
}

/// <summary>A row of the list: a .NET folder's heading, an SDK, its folder's runtimes (folded), a runtime, or what can be installed.</summary>
public sealed class DotnetSdkRowViewModel
{
    private DotnetSdkRowViewModel(DotnetSdkRowKind kind, string key, string title, string detail)
    {
        Kind = kind;
        Key = key;
        Title = title;
        Detail = detail;
    }

    public DotnetSdkRowKind Kind { get; }

    public string Key { get; }

    public string Title { get; }

    /// <summary>The second line: the channel and its support; a folder's owner.</summary>
    public string Detail { get; }

    public object? Item { get; private init; }

    public DotnetInstallation? Installation { get; private init; }

    public IReadOnlyList<DotnetSdkChip> Chips { get; private init; } = [];

    public bool IsExpanded { get; private init; }

    public bool IsHeader => Kind == DotnetSdkRowKind.Folder;

    public bool IsGroup => Kind == DotnetSdkRowKind.RuntimeGroup;

    public bool IsItem => Kind is not (DotnetSdkRowKind.Folder or DotnetSdkRowKind.RuntimeGroup);

    public bool IsSelectable => Kind != DotnetSdkRowKind.Folder;

    public bool IsIndented { get; private init; }

    public string Icon => Kind switch
    {
        DotnetSdkRowKind.Folder => Installation?.InUse == true ? "folder-open" : "folder",
        DotnetSdkRowKind.Runtime or DotnetSdkRowKind.RuntimeGroup => "layers",
        DotnetSdkRowKind.Available when Item is AvailableDotnet { IsSdk: false } => "layers",
        _ => "cube",
    };

    public string Chevron => IsExpanded ? "chevron-down" : "chevron-right";

    public string ExpandName => IsExpanded ? Localizer.Get("msg.sdks.collapseRuntimes", "Hide the runtimes") : Localizer.Get("msg.sdks.expandRuntimes", "Show the runtimes");

    public string AccessibleName => string.Join(", ", new[] { Title, Detail }.Concat(Chips.Select(c => c.Tip)).Where(s => s.Length > 0));

    internal static DotnetSdkRowViewModel ForFolder(DotnetInstallation installation) =>
        new(DotnetSdkRowKind.Folder, "dir|" + installation.Root, installation.Root, installation.InUse
            ? Localizer.Format("msg.sdks.folderInUseShort", "Codalus IDE uses it · managed by {0}", DotnetSdksViewModel.OwnerText(installation.Owner))
            : Localizer.Format("msg.sdks.folderManagedShort", "Managed by {0}", DotnetSdksViewModel.OwnerText(installation.Owner)))
        {
            Installation = installation,
        };

    internal static DotnetSdkRowViewModel ForSdk(DotnetSdkFacts facts, InstalledSdk sdk)
    {
        var chips = new List<DotnetSdkChip>();
        if (sdk.OutOfSupport)
        {
            chips.Add(DotnetSdkChip.OutOfSupport(sdk.Support));
        }

        if (DotnetSdkChip.Update(sdk.Update, sdk.SecurityUpdate) is { } update)
        {
            chips.Add(update);
        }

        if (sdk.UsedBySolution)
        {
            chips.Add(new(Localizer.Get("msg.sdks.chipInUse", "In use"), Localizer.Get("msg.sdks.usedBySolution", "the open solution builds with it"), "info"));
        }
        else if (sdk.Update is null && DotnetSdkPlanner.OldPatches(sdk.Installation, sdk.Channel).Contains(sdk.Version))
        {
            chips.Add(new(Localizer.Get("msg.sdks.chipOldPatch", "Old patch"), Localizer.Get("msg.sdks.oldPatchTip", "A newer patch of its feature band is installed beside it"), "plain"));
        }

        return new(DotnetSdkRowKind.Sdk, DotnetSdksViewModel.Key(sdk), Localizer.Format("msg.sdks.sdkTitle", "SDK {0}", sdk.Version), ChannelLine(sdk.Channel, sdk.Support))
        {
            Item = sdk,
            Installation = sdk.Installation,
            Chips = chips,
        };
    }

    internal static DotnetSdkRowViewModel ForRuntime(InstalledRuntime runtime, bool indented)
    {
        var chips = new List<DotnetSdkChip>();
        if (runtime.OutOfSupport)
        {
            chips.Add(DotnetSdkChip.OutOfSupport(runtime.Support));
        }

        if (DotnetSdkChip.Update(runtime.Update, runtime.SecurityUpdate) is { } update)
        {
            chips.Add(update);
        }

        return new(DotnetSdkRowKind.Runtime, DotnetSdksViewModel.Key(runtime), $"{DotnetFrameworks.Title(runtime.Runtime.Framework)} {runtime.Runtime.Version}", ChannelLine(runtime.Channel, runtime.Support))
        {
            Item = runtime,
            Installation = runtime.Installation,
            Chips = chips,
            IsIndented = indented,
        };
    }

    internal static DotnetSdkRowViewModel ForRuntimeGroup(DotnetInstallation installation, IReadOnlyList<InstalledRuntime> runtimes, bool expanded)
    {
        var updates = runtimes.Count(r => r.Update is not null);
        var summary = string.Join(" · ", runtimes.GroupBy(r => r.Runtime.Framework).Select(g => $"{DotnetFrameworks.Title(g.Key)} {string.Join(", ", g.Select(r => r.Runtime.Version))}"));
        IReadOnlyList<DotnetSdkChip> chips = updates == 0 ? [] : [new(
            updates == 1 ? Localizer.Get("msg.sdks.oneUpdate", "1 update") : Localizer.Format("msg.sdks.nUpdates", "{0} updates", updates),
            Localizer.Get("msg.sdks.runtimeUpdatesTip", "A newer patch is out for some of these runtimes"),
            runtimes.Any(r => r.SecurityUpdate) ? "warning" : "info")];
        return new(DotnetSdkRowKind.RuntimeGroup, "grp|" + installation.Root, Localizer.Format("msg.sdks.runtimesGroup", "Runtimes ({0})", runtimes.Count), summary)
        {
            Installation = installation,
            Chips = chips,
            IsExpanded = expanded,
        };
    }

    internal static DotnetSdkRowViewModel ForAvailable(AvailableDotnet available, bool requested)
    {
        var chips = new List<DotnetSdkChip>();
        if (requested)
        {
            chips.Add(new(Localizer.Get("msg.sdks.chipAskedFor", "Asked for"), available.Support, "info"));
        }

        if (available.Preview)
        {
            chips.Add(new(Localizer.Get("msg.sdks.chipPreview", "Preview"), available.Support, "warning"));
        }

        if (available.OutOfSupport)
        {
            chips.Add(DotnetSdkChip.OutOfSupport(available.Support));
        }

        return new(DotnetSdkRowKind.Available, DotnetSdksViewModel.Key(available), available.Title, requested ? available.Support : ChannelLine(available.Channel, available.Support))
        {
            Item = available,
            Chips = chips,
        };
    }

    internal static string ChannelLine(string channel, string support) =>
        support.Length == 0 ? $".NET {channel}" : $".NET {channel} · {support}";
}

/// <summary>One way to run an install or an update, as the details list it (a radio button when there are several).</summary>
public sealed class DotnetSdkOptionViewModel(InstallPlan plan, bool selected) : ObservableObject
{
    private bool _isSelected = selected;

    public InstallPlan Plan { get; } = plan;

    public string Title => Plan.Title ?? Plan.Summary;

    public string Command => Plan.Command;

    public string? Note => Plan.Kind == InstallKind.Terminal
        ? string.Join(" ", new[] { Plan.Note, Localizer.Get("msg.sdks.runsInTerminal", "It runs in the terminal, where it asks for your password.") }.OfType<string>())
        : Plan.Note;

    public bool HasNote => !string.IsNullOrEmpty(Note);

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }
}

/// <summary>A fact in the details: its label and its value.</summary>
public sealed record DotnetSdkFact(string Label, string Value);

/// <summary>
/// The selection's details: what it is, its folder and support, what it brought or what brought it, who pins it, and the
/// exact command its main button runs (each way, when there are several) and what Remove does.
/// </summary>
public sealed class DotnetSdkDetailsViewModel : ObservableObject
{
    private readonly RelayCommand _primary;
    private readonly RelayCommand _remove;
    private readonly RelayCommand _oldPatches;
    private DotnetSdkPreview? _preview;
    private bool _isLoadingPreview;

    internal DotnetSdkDetailsViewModel(DotnetSdksViewModel owner, DotnetSdksData data, DotnetSdkRowViewModel row)
    {
        Data = data;
        Key = row.Key;
        Item = row.Item;
        Icon = row.Icon;
        _primary = new RelayCommand(() => _ = owner.PrimaryAsync(), () => !owner.IsBusy && Plan is not null);
        _remove = new RelayCommand(() => _ = owner.RemoveAsync(), () => !owner.IsBusy);
        _oldPatches = new RelayCommand(() => _ = owner.RemoveOldPatchesAsync(), () => !owner.IsBusy);
        var facts = data.Facts;
        var chips = new List<DotnetSdkChip>();
        var list = new List<DotnetSdkFact>();
        switch (row.Item)
        {
            case InstalledSdk sdk:
                Heading = Localizer.Format("msg.sdks.sdkHeading", ".NET SDK {0}", sdk.Version);
                AddReleaseType(chips, facts, sdk.Channel);
                chips.AddRange(row.Chips);
                list.Add(FolderFact(sdk.Installation));
                list.Add(new(Localizer.Get("msg.sdks.factSupport", "Support"), DotnetSdkRowViewModel.ChannelLine(sdk.Channel, sdk.Support)));
                list.Add(new(Localizer.Get("msg.sdks.factBand", "Feature band"), sdk.Band));
                var brought = DotnetFrameworks.All.Select(f => DotnetSdkPlanner.RuntimeOfSdk(facts, sdk.Version, f) is { } v && sdk.Installation.VersionsOf(f).Contains(v) ? $"{DotnetFrameworks.Title(f)} {v}" : null).OfType<string>().ToList();
                if (brought.Count > 0)
                {
                    list.Add(new(Localizer.Get("msg.sdks.factBrought", "Runtimes it brought"), string.Join(", ", brought)));
                }

                if (sdk.UsedBySolution)
                {
                    list.Add(new(Localizer.Get("msg.sdks.factUsedBy", "Used by"), Localizer.Get("msg.sdks.usedBySolutionSentence", "The open solution builds with it.")));
                }

                list.Add(new(Localizer.Get("msg.sdks.factPinned", "Pinned"), facts.Pinned.Contains(sdk.Version)
                    ? Localizer.Get("msg.sdks.pinnedYes", "A recent solution's global.json pins this version.")
                    : Localizer.Get("msg.sdks.pinnedNo", "No recent solution's global.json pins it.")));
                if (sdk.Update is { } to)
                {
                    PrimaryLabel = Localizer.Format("msg.sdks.updateToNow", "Update to {0}", to);
                    PrimaryIcon = "cloud-download";
                    ActionHeading = Localizer.Format("msg.sdks.updateRuns", "Update to {0} runs", to);
                    ActionFooter = Localizer.Format("msg.sdks.updateThen", "Afterwards Codalus IDE offers to remove {0}, unless a recent solution's global.json pins it.", sdk.Version);
                }

                var old = DotnetSdkPlanner.OldPatches(sdk.Installation, sdk.Channel).Where(s => !facts.Pinned.Contains(s)).ToList();
                if (old.Count > 0)
                {
                    HasOldPatches = true;
                    OldPatchesLabel = Localizer.Format("msg.sdks.oldPatchesOf", "Remove Old .NET {0} Patches…", sdk.Channel);
                    OldPatchesTip = string.Join(", ", old);
                }

                break;
            case InstalledRuntime runtime:
                Heading = $"{DotnetFrameworks.Title(runtime.Runtime.Framework)} {runtime.Runtime.Version}";
                AddReleaseType(chips, facts, runtime.Channel);
                chips.AddRange(row.Chips);
                list.Add(FolderFact(runtime.Installation));
                list.Add(new(Localizer.Get("msg.sdks.factSupport", "Support"), DotnetSdkRowViewModel.ChannelLine(runtime.Channel, runtime.Support)));
                var by = runtime.Installation.Sdks.Where(s => DotnetSdkPlanner.RuntimeOfSdk(facts, s, runtime.Runtime.Framework) == runtime.Runtime.Version).ToList();
                list.Add(new(Localizer.Get("msg.sdks.factBroughtBy", "Brought by"), by.Count > 0
                    ? string.Join(", ", by.Select(s => Localizer.Format("msg.sdks.sdkTitle", "SDK {0}", s)))
                    : Localizer.Get("msg.sdks.broughtByNone", "No SDK in this folder: it was installed on its own.")));
                if (runtime.Update is { } runtimeTo)
                {
                    PrimaryLabel = Localizer.Format("msg.sdks.updateToNow", "Update to {0}", runtimeTo);
                    PrimaryIcon = "cloud-download";
                    ActionHeading = Localizer.Format("msg.sdks.updateRuns", "Update to {0} runs", runtimeTo);
                }

                break;
            case AvailableDotnet available:
                Heading = available.Title;
                AddReleaseType(chips, facts, available.Channel);
                chips.AddRange(row.Chips);
                // A prompt's row carries its reason in Support: the channel's own support is asked again.
                var support = available == data.Requested ? DotnetSdkPlanner.Support(facts, available.Channel, available.Preview).Text : available.Support;
                if (available == data.Requested)
                {
                    list.Add(new(Localizer.Get("msg.sdks.factWhy", "Why"), available.Support));
                }

                list.Add(new(Localizer.Get("msg.sdks.factSupport", "Support"), DotnetSdkRowViewModel.ChannelLine(available.Channel, support)));
                list.Add(facts.Target is { } target
                    ? FolderFact(target) with { Label = Localizer.Get("msg.sdks.factInto", "Installs into") }
                    : new(Localizer.Get("msg.sdks.factInto", "Installs into"), DotnetSdkPlanner.UserRoot(facts)));
                if (!available.IsSdk)
                {
                    list.Add(new(Localizer.Get("msg.sdks.factWhy", "Why"), Localizer.Get("msg.sdks.runtimeAloneWhy", "A runtime alone runs projects that target this .NET without its SDK.")));
                }

                PrimaryLabel = Localizer.Get("msg.sdks.installNow", "Install");
                PrimaryIcon = "download";
                ActionHeading = Localizer.Get("msg.sdks.installRuns", "Install runs");
                break;
            default:
                // A folder's runtimes, folded: what's in it, and how to get at one.
                Heading = row.Title;
                if (row.Installation is { } installation)
                {
                    list.Add(FolderFact(installation));
                    foreach (var group in data.Runtimes.Where(r => r.Installation == installation).GroupBy(r => r.Runtime.Framework))
                    {
                        list.Add(new(DotnetFrameworks.Title(group.Key), string.Join(", ", group.Select(r => r.Runtime.Version))));
                    }
                }

                Note = Localizer.Get("msg.sdks.groupNote", "Open the list (the arrow, or Right) to update or remove a runtime.");
                break;
        }

        Chips = chips;
        Facts = list;
        IsLoadingPreview = row.Item is InstalledSdk or InstalledRuntime or AvailableDotnet;
    }

    internal DotnetSdksData Data { get; }

    public string Key { get; }

    public object? Item { get; }

    public string Icon { get; }

    public string Heading { get; } = string.Empty;

    public IReadOnlyList<DotnetSdkChip> Chips { get; }

    public IReadOnlyList<DotnetSdkFact> Facts { get; }

    public string? Note { get; }

    public bool HasNote => !string.IsNullOrEmpty(Note);

    public ICommand PrimaryCommand => _primary;

    public ICommand RemoveCommand => _remove;

    public ICommand OldPatchesCommand => _oldPatches;

    /// <summary>"Install" or "Update to 10.0.405"; null when there's nothing to install or update.</summary>
    public string? PrimaryLabel { get; }

    public string PrimaryIcon { get; } = "download";

    public bool HasPrimary => PrimaryLabel is not null && Options.Count > 0;

    public string? ActionHeading { get; }

    public string? ActionFooter { get; }

    public bool HasActionFooter => ActionFooter is not null && HasPrimary;

    public bool HasOldPatches { get; }

    public string? OldPatchesLabel { get; }

    public string? OldPatchesTip { get; }

    public bool IsLoadingPreview
    {
        get => _isLoadingPreview;
        private set => SetField(ref _isLoadingPreview, value);
    }

    public ObservableCollection<DotnetSdkOptionViewModel> Options { get; } = [];

    public bool HasManyOptions => Options.Count > 1;

    public bool HasOneOption => Options.Count == 1;

    /// <summary>The way the main button runs: the chosen option.</summary>
    public InstallPlan? Plan => Options.FirstOrDefault(o => o.IsSelected)?.Plan ?? (Options.Count > 0 ? Options[0].Plan : null);

    /// <summary>Why there's no way to install or update from here, when the planner has none.</summary>
    public string? NoPlan { get; private set; }

    public bool HasNoPlan => !string.IsNullOrEmpty(NoPlan);

    public bool CanRemove => _preview?.Removal is { Refused: null };

    public string RemoveLabel => Localizer.Get("msg.sdks.remove", "Remove…");

    /// <summary>Why it can't be removed (the open solution builds with it, the IDE's hosts need it…).</summary>
    public string? RemoveRefused => _preview?.Removal?.Refused;

    public bool HasRemoveRefused => !string.IsNullOrEmpty(RemoveRefused);

    /// <summary>What Remove does: the folders it deletes or the command it runs, and what stays.</summary>
    public string? RemovalText { get; private set; }

    public bool HasRemovalText => !string.IsNullOrEmpty(RemovalText);

    internal void SetPreview(DotnetSdkPreview preview)
    {
        _preview = preview;
        if (PrimaryLabel is not null)
        {
            var chosen = preview.Plans.FirstOrDefault(p => p.Recommended) ?? (preview.Plans.Count > 0 ? preview.Plans[0] : null);
            foreach (var plan in preview.Plans)
            {
                var option = new DotnetSdkOptionViewModel(plan, plan == chosen);
                option.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(DotnetSdkOptionViewModel.IsSelected))
                    {
                        OnPropertyChanged(nameof(Plan));
                    }
                };
                Options.Add(option);
            }

            if (preview.Plans.Count == 0)
            {
                NoPlan = Localizer.Format("msg.setup.noPlan", "There's no way to install {0} from here on this system.", Heading);
            }
        }

        if (preview.Removal is { Refused: null } removal)
        {
            var lines = new List<string>
            {
                removal.Command is { } command
                    ? Localizer.Format("msg.sdks.thisRuns", "This runs:\n{0}", command.Command)
                    : Localizer.Format("msg.sdks.thisDeletes", "This deletes:\n{0}", string.Join("\n", removal.Folders)),
            };
            lines.AddRange(removal.Kept);
            RemovalText = string.Join("\n\n", lines);
        }

        IsLoadingPreview = false;
        foreach (var name in new[] { nameof(HasPrimary), nameof(HasManyOptions), nameof(HasOneOption), nameof(Plan), nameof(NoPlan), nameof(HasNoPlan), nameof(HasActionFooter), nameof(CanRemove), nameof(RemoveRefused), nameof(HasRemoveRefused), nameof(RemovalText), nameof(HasRemovalText) })
        {
            OnPropertyChanged(name);
        }

        RaiseCanExecuteChanged();
    }

    internal void RaiseCanExecuteChanged()
    {
        _primary.RaiseCanExecuteChanged();
        _remove.RaiseCanExecuteChanged();
        _oldPatches.RaiseCanExecuteChanged();
    }

    private static DotnetSdkFact FolderFact(DotnetInstallation installation) => new(
        Localizer.Get("msg.sdks.factFolder", "Folder"),
        installation.InUse
            ? Localizer.Format("msg.sdks.folderFactInUse", "{0} · the .NET Codalus IDE uses, managed by {1}", installation.Root, DotnetSdksViewModel.OwnerText(installation.Owner))
            : Localizer.Format("msg.sdks.folderFact", "{0} · managed by {1}", installation.Root, DotnetSdksViewModel.OwnerText(installation.Owner)));

    private static void AddReleaseType(List<DotnetSdkChip> chips, DotnetSdkFacts facts, string channel)
    {
        if (facts.ChannelInfo(channel)?.ReleaseType is { Length: > 0 } type)
        {
            var upper = type.ToUpperInvariant();
            chips.Add(new(upper, upper == "LTS"
                ? Localizer.Get("msg.sdks.ltsTip", "Long-term support: three years")
                : Localizer.Get("msg.sdks.stsTip", "Standard-term support: two years"), "square"));
        }
    }
}
