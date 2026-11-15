using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Mobile;

namespace Codalus.Shell.ViewModels;

/// <summary>A package in the Android SDK Manager: checked means installed once the changes are applied.</summary>
public sealed class SdkPackageRow(SdkPackage package, Action changed) : ObservableObject
{
    private bool _isChecked = package.Installed;

    public SdkPackage Package { get; } = package;

    public string Path => Package.Path;

    public string Description => Package.Description.Length > 0 ? Package.Description : Package.Path;

    public string Version => Package.Version;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetField(ref _isChecked, value))
            {
                OnPropertyChanged(nameof(Status));
                changed();
            }
        }
    }

    /// <summary>Whether applying installs it (checked, not installed) or uninstalls it (installed, unchecked).</summary>
    public bool WillInstall => IsChecked && !Package.Installed;

    public bool WillUninstall => !IsChecked && Package.Installed;

    public string Status =>
        WillInstall ? Localizer.Get("msg.androidSdk.willInstall", "Will be installed")
        : WillUninstall ? Localizer.Get("msg.androidSdk.willUninstall", "Will be uninstalled")
        : Package.Update is { } update ? Localizer.Format("msg.androidSdk.updateAvailable", "Update available: {0}", update)
        : Package.Installed ? Localizer.Get("msg.androidSdk.installed", "Installed")
        : string.Empty;

    public bool HasUpdate => Package.Update is not null;

    public string AccessibleName => Description + ", " + Path + ", " + Version + (Status.Length > 0 ? ", " + Status : string.Empty);
}

/// <summary>
/// The Android SDK Manager (ADR 0104): the SDK's packages from <c>sdkmanager --list</c> by kind (platforms, system
/// images, build tools, tools…), with search; check to install, uncheck to uninstall, then Apply Changes, which shows
/// the licenses first and runs <c>sdkmanager</c> (elevated when the SDK's folder needs an administrator); Update All.
/// </summary>
public sealed class AndroidSdkManagerViewModel : ObservableObject
{
    public const string ToolWindowId = "android.sdkManager";

    private readonly ShellHost _shell;
    private IReadOnlyList<SdkPackage> _packages = [];
    private int _category;
    private string _search = string.Empty;
    private bool _installedOnly;
    private bool _busy;
    private bool _loaded;
    private string _status = string.Empty;
    private string _location = string.Empty;
    private bool _canInstallTools;
    private readonly RelayCommand _apply;
    private readonly RelayCommand _updateAll;

    public AndroidSdkManagerViewModel(ShellHost shell)
    {
        _shell = shell;
        _apply = new RelayCommand(() => _ = ApplyAsync(), () => !_busy && PendingCount > 0);
        _updateAll = new RelayCommand(() => _ = UpdateAllAsync(), () => !_busy && _packages.Any(p => p.Update is not null));
        RefreshCommand = new RelayCommand(() => _ = LoadAsync());
        InstallToolsCommand = new RelayCommand(() => _ = shell.InstallAndroidDependenciesAsync());
        RevertCommand = new RelayCommand(Revert);
    }

    public ObservableCollection<SdkPackageRow> Rows { get; } = [];

    /// <summary>All rows (the filter shows some of them), keeping the user's checks across filters.</summary>
    private List<SdkPackageRow> _all = [];

    public IReadOnlyList<string> Categories { get; } =
    [
        Localizer.Get("msg.androidSdk.all", "All packages"),
        Localizer.Get("msg.androidSdk.platforms", "Platforms"),
        Localizer.Get("msg.androidSdk.systemImages", "System images"),
        Localizer.Get("msg.androidSdk.buildTools", "Build tools"),
        Localizer.Get("msg.androidSdk.tools", "Tools"),
        Localizer.Get("msg.androidSdk.sources", "Sources"),
        Localizer.Get("msg.androidSdk.other", "Other"),
    ];

    /// <summary>The category filter: 0 for all, else the <see cref="SdkPackageCategory"/> plus one.</summary>
    public int Category
    {
        get => _category;
        set
        {
            if (SetField(ref _category, value))
            {
                Filter();
            }
        }
    }

    public string Search
    {
        get => _search;
        set
        {
            if (SetField(ref _search, value ?? string.Empty))
            {
                Filter();
            }
        }
    }

    public bool InstalledOnly
    {
        get => _installedOnly;
        set
        {
            if (SetField(ref _installedOnly, value))
            {
                Filter();
            }
        }
    }

    /// <summary>Loading, what's missing, or how many changes are pending.</summary>
    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    /// <summary>The SDK's folder, and whether changing it needs an administrator.</summary>
    public string Location
    {
        get => _location;
        private set => SetField(ref _location, value);
    }

    /// <summary>The SDK (or its command-line tools) is missing: the Install button shows.</summary>
    public bool CanInstallTools
    {
        get => _canInstallTools;
        private set => SetField(ref _canInstallTools, value);
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (SetField(ref _busy, value))
            {
                _apply.RaiseCanExecuteChanged();
                _updateAll.RaiseCanExecuteChanged();
            }
        }
    }

    public int PendingCount => _all.Count(r => r.WillInstall || r.WillUninstall);

    public bool HasPending => PendingCount > 0;

    public ICommand ApplyCommand => _apply;

    public ICommand UpdateAllCommand => _updateAll;

    public ICommand RefreshCommand { get; }

    public ICommand InstallToolsCommand { get; }

    public ICommand RevertCommand { get; }

    /// <summary>Loads the list the first time the window shows.</summary>
    public Task EnsureLoadedAsync() => _loaded || _busy ? Task.CompletedTask : LoadAsync();

    /// <summary>Reads the packages (<c>sdkmanager --list</c>, which asks Google's repository).</summary>
    public async Task LoadAsync()
    {
        var locations = _shell.AndroidSdk.Locations();
        if (locations.Sdk is not { } sdk)
        {
            Show([], Localizer.Get("msg.androidSdk.noSdk", "No Android SDK was found. Install it through the Android workload (Install), or set its folder in Tools → Options → Mobile."), string.Empty, installTools: true);
            return;
        }

        var location = Localizer.Format("msg.androidSdk.location", "Android SDK: {0}", sdk) + (_shell.AndroidSdk.IsSdkWritable() ? string.Empty : Localizer.Get("msg.androidSdk.needsAdmin", " (changes need an administrator)"));
        if (_shell.AndroidSdk.Tools() is null)
        {
            Show([], Localizer.Get("msg.androidSdk.noTools", "The SDK's command-line tools (sdkmanager) aren't installed. Install adds them through the Android workload."), location, installTools: true);
            return;
        }

        IsBusy = true;
        Status = Localizer.Get("msg.androidSdk.loading", "Reading the packages…");
        Location = location;
        try
        {
            var packages = await _shell.AndroidSdk.ListPackagesAsync(CancellationToken.None).ConfigureAwait(true);
            _loaded = true;
            Show(packages, string.Empty, location, installTools: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Show(_packages, Localizer.Format("msg.androidSdk.loadFailed", "Reading the packages failed: {0}", ex.Message), location, installTools: false);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Show(IReadOnlyList<SdkPackage> packages, string status, string location, bool installTools)
    {
        _packages = packages;
        _all = [.. packages.Select(p => new SdkPackageRow(p, OnChecked))];
        Location = location;
        CanInstallTools = installTools;
        Filter();
        Status = status.Length > 0 ? status : Summary();
        _updateAll.RaiseCanExecuteChanged();
        OnChecked();
    }

    private void Filter()
    {
        Rows.Clear();
        foreach (var row in _all.Where(Matches))
        {
            Rows.Add(row);
        }
    }

    private bool Matches(SdkPackageRow row) =>
        (_category == 0 || (int)row.Package.Category == _category - 1)
        && (!_installedOnly || row.Package.Installed)
        && (_search.Length == 0 || row.Path.Contains(_search, StringComparison.OrdinalIgnoreCase) || row.Description.Contains(_search, StringComparison.OrdinalIgnoreCase));

    private void OnChecked()
    {
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(HasPending));
        _apply.RaiseCanExecuteChanged();
        if (!_busy && _loaded)
        {
            Status = Summary();
        }
    }

    private string Summary()
    {
        var install = _all.Count(r => r.WillInstall);
        var uninstall = _all.Count(r => r.WillUninstall);
        return install + uninstall > 0
            ? Localizer.Format("msg.androidSdk.pending", "{0} to install, {1} to uninstall: Apply Changes.", install, uninstall)
            : Localizer.Format("msg.androidSdk.summary", "{0} installed, {1} with updates.", _packages.Count(p => p.Installed), _packages.Count(p => p.Update is not null));
    }

    private void Revert()
    {
        foreach (var row in _all)
        {
            row.IsChecked = row.Package.Installed;
        }
    }

    /// <summary>Installs the checked packages and uninstalls the unchecked ones, after the licenses; then reads the list again.</summary>
    public async Task ApplyAsync()
    {
        var install = _all.Where(r => r.WillInstall).Select(r => r.Path).ToList();
        var uninstall = _all.Where(r => r.WillUninstall).Select(r => r.Path).ToList();
        if (install.Count + uninstall.Count == 0)
        {
            return;
        }

        if (uninstall.Count > 0 && !await _shell.Dialogs.ConfirmAsync(
                Localizer.Get("msg.androidSdk.uninstallTitle", "Uninstall Android SDK Packages"),
                Localizer.Format("msg.androidSdk.uninstallMessage", "This removes {0} from the Android SDK.", string.Join(", ", uninstall)),
                Localizer.Get("msg.androidSdk.apply", "Apply Changes")).ConfigureAwait(true))
        {
            return;
        }

        IsBusy = true;
        Status = Localizer.Get("msg.androidSdk.applying", "Applying the changes (the Android SDK output shows how it goes)…");
        try
        {
            await _shell.ChangeSdkPackagesAsync(install, uninstall).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync().ConfigureAwait(true);
    }

    public async Task UpdateAllAsync()
    {
        IsBusy = true;
        Status = Localizer.Get("msg.androidSdk.updating", "Updating (the Android SDK output shows how it goes)…");
        try
        {
            await _shell.UpdateAllSdkPackagesAsync([.. _packages.Where(p => p.Update is not null).Select(p => p.Path)]).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync().ConfigureAwait(true);
    }
}
