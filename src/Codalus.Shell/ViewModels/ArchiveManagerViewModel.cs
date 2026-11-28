using System.Collections.ObjectModel;
using System.Globalization;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Mobile;

namespace Codalus.Shell.ViewModels;

/// <summary>A MAUI app of the solution the Archive Manager archives: its project and the platforms it targets.</summary>
public sealed record ArchiveApp(string Project, string Name, IReadOnlyList<string> Frameworks);

/// <summary>What the project file says about an app's identity and versions (MSBuild's evaluation).</summary>
public sealed record AppVersions(string? ApplicationId, string? Title, string? DisplayVersion, string? Version, string? WindowsPackageType);

/// <summary>What the Archive Manager asks of the shell: the project's properties, the build, the tools, and the OS.</summary>
public sealed class ArchiveManagerActions
{
    public required Func<string, Task<AppVersions>> ReadVersions { get; init; }

    /// <summary>Writes <c>ApplicationDisplayVersion</c> and <c>ApplicationVersion</c> into the project file.</summary>
    public required Func<string, string, string, Task> WriteVersions { get; init; }

    public required Func<ArchiveRequest, Task<ArchiveResult>> Archive { get; init; }

    public required Func<AppArchive, DistributionChannel, DistributionSigning, Task<DistributionResult>> Distribute { get; init; }

    /// <summary>A line for the Archive output channel (the tools' output, the command lines).</summary>
    public Action<string> Log { get; init; } = _ => { };

    public Action<string> Reveal { get; init; } = _ => { };

    public Action<Uri> OpenUrl { get; init; } = _ => { };

    /// <summary>Asks a yes or no (title, message, action), danger styled.</summary>
    public Func<string, string, string, Task<bool>> Confirm { get; init; } = (_, _, _) => Task.FromResult(false);
}

/// <summary>A choice in one of the page's lists: its label, and what it stands for.</summary>
public sealed class Choice<T>(string label, T value)
{
    public string Label { get; } = label;

    public T Value { get; } = value;

    public override string ToString() => Label;
}

/// <summary>An archive in the list.</summary>
public sealed class ArchiveRowViewModel(AppArchive archive)
{
    public AppArchive Archive { get; } = archive;

    public string Name => Archive.Name;

    public string Platform => MobileTargets.Title(Archive.Platform);

    public string Version => Archive.VersionLabel;

    public string Created => Archive.Created.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    /// <summary>Where it lives: Xcode's archives (Xcode's Organizer lists it too) or the IDE's.</summary>
    public string Location => Archive.IsXcodeArchive ? "Xcode" : "Codalus";
}

/// <summary>
/// The Archive Manager (ADR 0114): a MAUI app's archives for each platform, a new archive (its versions shown, edited
/// and the build number offered +1 when this version was archived already), and Distribute: the selected archive
/// signed for a channel with the user's signing identities, then handed off (its folder, the store's upload page).
/// </summary>
public sealed class ArchiveManagerViewModel : ObservableObject
{
    public const string AllApps = "All apps";

    private readonly ArchiveService _service;
    private readonly ArchiveManagerActions _actions;
    private readonly IReadOnlyList<ArchiveApp> _apps;
    private int _appIndex;
    private Choice<MobilePlatform>? _platform;
    private AppVersions? _versions;
    private string _displayVersion = string.Empty;
    private string _version = string.Empty;
    private bool _incrementBuild;
    private bool _isBusy;
    private string? _status;
    private ArchiveRowViewModel? _selected;
    private Choice<DistributionChannel>? _channel;
    private Choice<AndroidKeystore>? _keystore;
    private Choice<SigningIdentity>? _identity;
    private Choice<ProvisioningProfile>? _profile;
    private Choice<SigningIdentity>? _installer;
    private Choice<object>? _certificate;
    private string? _distributeStatus;
    private DistributionResult? _lastDistribution;
    private IReadOnlyList<SigningIdentity> _appleIdentities = [];
    private IReadOnlyList<SigningIdentity> _allAppleIdentities = [];
    private IReadOnlyList<ProvisioningProfile> _profiles = [];
    private IReadOnlyList<StoreCertificate> _storeCertificates = [];
    private string? _testCertificateFile;
    private bool _isCreatingKeystore;
    private bool _isImportingKeystore;
    private bool _isImportingCertificate;

    public ArchiveManagerViewModel(ArchiveService service, IReadOnlyList<ArchiveApp> apps, string? project, ArchiveManagerActions actions)
    {
        _service = service;
        _apps = apps;
        _actions = actions;
        AppChoices = apps.Count > 1 ? [AllApps, .. apps.Select(a => a.Name)] : [.. apps.Select(a => a.Name)];
        ArchiveCommand = new RelayCommand(() => _ = ArchiveAsync(), () => CanArchive);
        DistributeCommand = new RelayCommand(() => _ = DistributeAsync(), () => _selected is not null && _channel is not null && !_isBusy);
        RefreshCommand = new RelayCommand(Refresh);
        DeleteCommand = new RelayCommand(() => _ = DeleteAsync(), () => _selected is not null && !_isBusy);
        RevealCommand = new RelayCommand(() => { if (_selected is { } row) { _actions.Reveal(row.Archive.Path); } }, () => _selected is not null);
        OpenDistributionCommand = new RelayCommand(() => { if (_lastDistribution is { Succeeded: true } d) { _actions.Reveal((d.Files.Count > 0 ? d.Files[0] : d.Folder)); } }, () => _lastDistribution is { Succeeded: true });
        OpenUploadPageCommand = new RelayCommand(() => { if (UploadPage is { } page) { _actions.OpenUrl(page); } }, () => UploadPage is not null && _lastDistribution is { Succeeded: true });
        CreateKeystoreCommand = new RelayCommand(() => _ = CreateKeystoreAsync());
        ImportKeystoreCommand = new RelayCommand(() => _ = ImportKeystoreAsync());
        RemoveKeystoreCommand = new RelayCommand(() => _ = RemoveKeystoreAsync(), () => _keystore is not null);
        ShowCreateKeystoreCommand = new RelayCommand(() =>
        {
            IsCreatingKeystore = !IsCreatingKeystore;
            IsImportingKeystore = false;
            if (IsCreatingKeystore && string.IsNullOrWhiteSpace(KeystoreFolder))
            {
                // A folder of the user's, outside the repository: the key is theirs to keep and back up.
                KeystoreFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Keystores");
            }
        });
        ShowImportKeystoreCommand = new RelayCommand(() => { IsImportingKeystore = !IsImportingKeystore; IsCreatingKeystore = false; });
        CreateTestCertificateCommand = new RelayCommand(() => _ = CreateTestCertificateAsync(), () => _selected?.Archive is { Platform: MobilePlatform.Windows, IsPackaged: true });
        ShowImportCertificateCommand = new RelayCommand(() => IsImportingCertificate = !IsImportingCertificate);
        ImportCertificateCommand = new RelayCommand(() => _ = ImportCertificateAsync());
        var index = project is null ? -1 : apps.ToList().FindIndex(a => Codalus.Workbench.Projects.SolutionService.PathComparer.Equals(a.Project, project));
        _appIndex = index < 0 ? 0 : index + (apps.Count > 1 ? 1 : 0);
        Reload();
        Refresh();
    }

    public IReadOnlyList<string> AppChoices { get; }

    public RelayCommand ArchiveCommand { get; }

    public RelayCommand DistributeCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public RelayCommand RevealCommand { get; }

    public RelayCommand OpenDistributionCommand { get; }

    public RelayCommand OpenUploadPageCommand { get; }

    public RelayCommand CreateKeystoreCommand { get; }

    public RelayCommand ImportKeystoreCommand { get; }

    public RelayCommand RemoveKeystoreCommand { get; }

    public RelayCommand ShowCreateKeystoreCommand { get; }

    public RelayCommand ShowImportKeystoreCommand { get; }

    public RelayCommand CreateTestCertificateCommand { get; }

    public RelayCommand ShowImportCertificateCommand { get; }

    public RelayCommand ImportCertificateCommand { get; }

    /// <summary>The selected app, or null for all of them.</summary>
    public ArchiveApp? App => _apps.Count > 1 ? (_appIndex == 0 ? null : _apps[_appIndex - 1]) : (_apps.Count > 0 ? _apps[0] : null);

    public int AppIndex
    {
        get => _appIndex;
        set
        {
            if (SetField(ref _appIndex, value))
            {
                Reload();
                Refresh();
            }
        }
    }

    public string Title => App is { } app ? "Archives: " + app.Name : "Archive Manager";

    // ------------------------------------------------------------------ a new archive

    public ObservableCollection<Choice<MobilePlatform>> Platforms { get; } = [];

    public Choice<MobilePlatform>? Platform
    {
        get => _platform;
        set
        {
            if (SetField(ref _platform, value))
            {
                UpdateIncrement();
                OnPropertyChanged(nameof(PlatformProblem));
                OnPropertyChanged(nameof(HasPlatformProblem));
                ArchiveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Why the chosen platform can't be archived here, or null.</summary>
    public string? PlatformProblem => App is null ? "Choose an app to archive." : _platform is { } p ? _service.CannotArchive(p.Value) : "The app targets no platform this IDE archives.";

    public bool HasPlatformProblem => PlatformProblem is not null;

    public bool HasApp => App is not null;

    public string? ApplicationId => _versions?.ApplicationId;

    public string DisplayVersion
    {
        get => _displayVersion;
        set
        {
            if (SetField(ref _displayVersion, value))
            {
                UpdateIncrement();
            }
        }
    }

    public string Version
    {
        get => _version;
        set
        {
            if (SetField(ref _version, value))
            {
                UpdateIncrement();
            }
        }
    }

    /// <summary>Whether Archive raises the build number first (checked when this version was archived for the platform).</summary>
    public bool IncrementBuild
    {
        get => _incrementBuild;
        set
        {
            if (SetField(ref _incrementBuild, value))
            {
                OnPropertyChanged(nameof(NextVersionNote));
            }
        }
    }

    /// <summary>"The build number becomes 8." while incrementing.</summary>
    public string? NextVersionNote => _incrementBuild ? $"The build number becomes {AppArchives.NextBuildNumber(_version)}." : null;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                ArchiveCommand.RaiseCanExecuteChanged();
                DistributeCommand.RaiseCanExecuteChanged();
                DeleteCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    private bool CanArchive => App is not null && _platform is not null && PlatformProblem is null && !_isBusy && _versions is not null;

    // ------------------------------------------------------------------ the archives

    public ObservableCollection<ArchiveRowViewModel> Archives { get; } = [];

    public bool HasNoArchives => Archives.Count == 0;

    public ArchiveRowViewModel? SelectedArchive
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                _lastDistribution = null;
                DistributeStatus = null;
                Channels.Clear();
                foreach (var channel in value is null ? [] : Distribution.Channels(value.Archive))
                {
                    Channels.Add(new Choice<DistributionChannel>(Distribution.Title(channel), channel));
                }

                Channel = Channels.FirstOrDefault();
                DeleteCommand.RaiseCanExecuteChanged();
                RevealCommand.RaiseCanExecuteChanged();
                CreateTestCertificateCommand.RaiseCanExecuteChanged();
                RaiseDistribution();
            }
        }
    }

    public bool HasSelection => _selected is not null;

    // ------------------------------------------------------------------ distribute

    public ObservableCollection<Choice<DistributionChannel>> Channels { get; } = [];

    public Choice<DistributionChannel>? Channel
    {
        get => _channel;
        set
        {
            if (SetField(ref _channel, value))
            {
                _lastDistribution = null;
                FillSigning();
                RaiseDistribution();
            }
        }
    }

    public string? ChannelDescription => _selected is { } row && _channel is { } channel ? Distribution.Description(row.Archive.Platform, channel.Value, row.Archive.IsPackaged) : null;

    public Uri? UploadPage => _channel is { } channel ? Distribution.UploadPage(channel.Value) : null;

    public bool IsAndroid => _selected?.Archive.Platform == MobilePlatform.Android;

    public bool IsApple => _selected?.Archive.Platform is MobilePlatform.iOS or MobilePlatform.MacCatalyst;

    public bool IsWindowsSigned => _selected?.Archive is { Platform: MobilePlatform.Windows, IsPackaged: true } && _channel?.Value == DistributionChannel.Sideload;

    public bool NeedsInstaller => _selected?.Archive.Platform == MobilePlatform.MacCatalyst && _channel?.Value == DistributionChannel.AppStore;

    public ObservableCollection<Choice<AndroidKeystore>> Keystores { get; } = [];

    public Choice<AndroidKeystore>? Keystore
    {
        get => _keystore;
        set
        {
            if (SetField(ref _keystore, value))
            {
                RemoveKeystoreCommand.RaiseCanExecuteChanged();
                RaiseDistribution();
            }
        }
    }

    public ObservableCollection<Choice<SigningIdentity>> Identities { get; } = [];

    public Choice<SigningIdentity>? Identity
    {
        get => _identity;
        set
        {
            if (SetField(ref _identity, value))
            {
                RaiseDistribution();
            }
        }
    }

    public ObservableCollection<Choice<ProvisioningProfile>> Profiles { get; } = [];

    public Choice<ProvisioningProfile>? Profile
    {
        get => _profile;
        set
        {
            if (SetField(ref _profile, value))
            {
                RaiseDistribution();
            }
        }
    }

    public ObservableCollection<Choice<SigningIdentity>> Installers { get; } = [];

    public Choice<SigningIdentity>? Installer
    {
        get => _installer;
        set
        {
            if (SetField(ref _installer, value))
            {
                RaiseDistribution();
            }
        }
    }

    /// <summary>Windows' certificates: the store's (a <see cref="StoreCertificate"/>) and the user's <c>.pfx</c> files (a <see cref="CertificateFile"/>).</summary>
    public ObservableCollection<Choice<object>> Certificates { get; } = [];

    public Choice<object>? Certificate
    {
        get => _certificate;
        set
        {
            if (SetField(ref _certificate, value))
            {
                RaiseDistribution();
            }
        }
    }

    /// <summary>What's missing before Distribute can sign, or null.</summary>
    public string? DistributeProblem => _selected is { } row && _channel is { } channel ? _service.Missing(row.Archive, channel.Value, Signing()) : null;

    public bool HasDistributeProblem => DistributeProblem is not null;

    public string? DistributeStatus
    {
        get => _distributeStatus;
        private set => SetField(ref _distributeStatus, value);
    }

    public bool HasDistribution => _lastDistribution is { Succeeded: true };

    public bool IsCreatingKeystore
    {
        get => _isCreatingKeystore;
        set => SetField(ref _isCreatingKeystore, value);
    }

    public bool IsImportingKeystore
    {
        get => _isImportingKeystore;
        set => SetField(ref _isImportingKeystore, value);
    }

    public bool IsImportingCertificate
    {
        get => _isImportingCertificate;
        set => SetField(ref _isImportingCertificate, value);
    }

    // The Create and Import forms' fields.
    public string KeystoreName { get; set; } = string.Empty;

    public string KeystorePath
    {
        get => _keystorePath;
        set => SetField(ref _keystorePath, value);
    }

    private string _keystorePath = string.Empty;

    /// <summary>The folder a new keystore is created in (its file is named after <see cref="KeystoreName"/>, else the app).</summary>
    public string KeystoreFolder
    {
        get => _keystoreFolder;
        set => SetField(ref _keystoreFolder, value);
    }

    private string _keystoreFolder = string.Empty;

    public string KeystoreAlias { get; set; } = "upload";

    public string KeystorePassword { get; set; } = string.Empty;

    public string KeystoreConfirmPassword { get; set; } = string.Empty;

    public string KeyPassword { get; set; } = string.Empty;

    public string KeystoreOwner { get; set; } = string.Empty;

    public string KeystoreOrganization { get; set; } = string.Empty;

    public string KeystoreCountry { get; set; } = string.Empty;

    public int KeystoreYears { get; set; } = 30;

    /// <summary><see cref="KeystoreYears"/> for the number box.</summary>
    public decimal? KeystoreYearsValue
    {
        get => KeystoreYears;
        set => KeystoreYears = value is { } years ? (int)Math.Clamp(years, 1, 100) : 30;
    }

    public string CertificatePath { get; set; } = string.Empty;

    public string CertificatePassword { get; set; } = string.Empty;

    /// <summary>A signing form's last message (an error, or what it made).</summary>
    public string? SigningStatus
    {
        get => _signingStatus;
        private set => SetField(ref _signingStatus, value);
    }

    private string? _signingStatus;

    // ------------------------------------------------------------------ loading

    /// <summary>Reads the archives again (the IDE's and Xcode's), keeping the selection.</summary>
    public void Refresh()
    {
        var selected = _selected?.Archive.Path;
        Archives.Clear();
        foreach (var archive in _service.Archives.List().Where(Belongs))
        {
            Archives.Add(new ArchiveRowViewModel(archive));
        }

        OnPropertyChanged(nameof(HasNoArchives));
        SelectedArchive = Archives.FirstOrDefault(a => a.Archive.Path == selected) ?? Archives.FirstOrDefault();
        UpdateIncrement();
    }

    /// <summary>Whether an archive is one of the selected app's (one of the solution's apps for All apps): its project, else its bundle id.</summary>
    private bool Belongs(AppArchive archive)
    {
        IEnumerable<ArchiveApp> apps = App is { } app ? [app] : _apps;
        return apps.Any(a =>
            (archive.Project is not null && Codalus.Workbench.Projects.SolutionService.PathComparer.Equals(archive.Project, a.Project))
            || (archive.ApplicationId is { } id && _knownIds.TryGetValue(a.Project, out var known) && id == known)
            || (archive.Project is null && archive.ApplicationId is null && archive.Name == a.Name));
    }

    private readonly Dictionary<string, string> _knownIds = new(Codalus.Workbench.Projects.SolutionService.PathComparer);

    private void Reload()
    {
        Platforms.Clear();
        _versions = null;
        if (App is { } app)
        {
            foreach (var framework in app.Frameworks)
            {
                var platform = MobileTargets.PlatformOf(framework);
                if ((platform is MobilePlatform.Android or MobilePlatform.iOS or MobilePlatform.MacCatalyst or MobilePlatform.Windows) && Platforms.All(p => p.Value != platform))
                {
                    Platforms.Add(new Choice<MobilePlatform>(MobileTargets.Title(platform), platform));
                }
            }
        }

        Platform = Platforms.FirstOrDefault(p => _service.CannotArchive(p.Value) is null) ?? Platforms.FirstOrDefault();
        foreach (var name in new[] { nameof(App), nameof(Title), nameof(HasApp), nameof(PlatformProblem), nameof(HasPlatformProblem), nameof(ApplicationId) })
        {
            OnPropertyChanged(name);
        }

        ArchiveCommand.RaiseCanExecuteChanged();
        _ = LoadVersionsAsync();
        _ = LoadIdentitiesAsync();
    }

    /// <summary>Reads every app's id (to match Xcode's archives), then the selected app's versions.</summary>
    public async Task LoadVersionsAsync()
    {
        foreach (var app in _apps.Where(a => !_knownIds.ContainsKey(a.Project)))
        {
            try
            {
                if ((await _actions.ReadVersions(app.Project).ConfigureAwait(true)).ApplicationId is { Length: > 0 } id)
                {
                    _knownIds[app.Project] = id;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The app's archives then match by project only.
            }
        }

        if (App is not { } selected)
        {
            Refresh();
            return;
        }

        try
        {
            _versions = await _actions.ReadVersions(selected.Project).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _versions = new AppVersions(null, null, null, null, null);
            Status = "The project's versions couldn't be read: " + ex.Message;
        }

        DisplayVersion = _versions.DisplayVersion ?? "1.0";
        Version = _versions.Version ?? "1";
        OnPropertyChanged(nameof(ApplicationId));
        ArchiveCommand.RaiseCanExecuteChanged();
        Refresh();
    }

    /// <summary>The keychain's identities and the installed profiles (macOS), the store's certificates (Windows), the user's keystores.</summary>
    public async Task LoadIdentitiesAsync()
    {
        try
        {
            _appleIdentities = await _service.AppleIdentitiesAsync(codeSigningOnly: true, CancellationToken.None).ConfigureAwait(true);
            _allAppleIdentities = await _service.AppleIdentitiesAsync(codeSigningOnly: false, CancellationToken.None).ConfigureAwait(true);
            _profiles = _service.InstalledProfiles();
            _storeCertificates = await _service.StoreCertificatesAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SigningStatus = "The signing identities couldn't be read: " + ex.Message;
        }

        FillSigning();
        RaiseDistribution();
    }

    private void UpdateIncrement()
    {
        if (_platform is not { } platform)
        {
            return;
        }

        IncrementBuild = Archives.Any(a => a.Archive.Platform == platform.Value && a.Archive.DisplayVersion == _displayVersion && a.Archive.Version == _version);
        OnPropertyChanged(nameof(NextVersionNote));
    }

    private void FillSigning()
    {
        var keystore = _keystore?.Value.Id;
        Keystores.Clear();
        foreach (var k in _service.Identities.Keystores())
        {
            Keystores.Add(new Choice<AndroidKeystore>($"{k.Name} ({k.Alias}, {Path.GetFileName(k.Path)})", k));
        }

        Keystore = Keystores.FirstOrDefault(k => k.Value.Id == keystore) ?? Keystores.FirstOrDefault();

        Identities.Clear();
        Profiles.Clear();
        Installers.Clear();
        if (_selected?.Archive is { } archive && _channel is { } channel && IsApple)
        {
            foreach (var identity in _appleIdentities.Where(i => Distribution.SignsFor(i, channel.Value)))
            {
                Identities.Add(new Choice<SigningIdentity>(identity.Name, identity));
            }

            foreach (var profile in Distribution.Profiles(_profiles, archive, channel.Value, DateTimeOffset.UtcNow))
            {
                var expires = profile.Expires is { } date ? $", expires {date.ToLocalTime():d}" : string.Empty;
                Profiles.Add(new Choice<ProvisioningProfile>($"{profile.Name} ({profile.ApplicationId}{expires})", profile));
            }

            foreach (var installer in _allAppleIdentities.Where(Distribution.IsInstallerIdentity))
            {
                Installers.Add(new Choice<SigningIdentity>(installer.Name, installer));
            }
        }

        Identity = Identities.FirstOrDefault(i => Profiles.FirstOrDefault()?.Value.Team is not { } team || i.Value.Team == team) ?? Identities.FirstOrDefault();
        Profile = Profiles.FirstOrDefault();
        Installer = Installers.FirstOrDefault();

        var certificate = _certificate?.Label;
        Certificates.Clear();
        var publisher = _selected?.Archive.Publisher;
        foreach (var store in _storeCertificates.Where(c => Distribution.SubjectMatches(c.Subject, publisher)))
        {
            Certificates.Add(new Choice<object>($"{store.Subject} ({store.Thumbprint[..8]}…, certificate store)", store));
        }

        foreach (var file in _service.Identities.Certificates())
        {
            Certificates.Add(new Choice<object>($"{file.Name} ({Path.GetFileName(file.Path)})", file));
        }

        Certificate = Certificates.FirstOrDefault(c => c.Label == certificate) ?? Certificates.FirstOrDefault();
    }

    private DistributionSigning Signing() => new()
    {
        Keystore = _keystore?.Value,
        AppleIdentity = _identity?.Value,
        Profile = _profile?.Value,
        Installer = _installer?.Value,
        StoreCertificate = _certificate?.Value as StoreCertificate,
        CertificateFile = _certificate?.Value as CertificateFile,
        PublicCertificate = _certificate?.Value is StoreCertificate store && store.Thumbprint.Length > 0 && _testThumbprint == store.Thumbprint ? _testCertificateFile : null,
    };

    private string? _testThumbprint;

    private void RaiseDistribution()
    {
        foreach (var name in new[]
        {
            nameof(ChannelDescription), nameof(UploadPage), nameof(IsAndroid), nameof(IsApple), nameof(IsWindowsSigned), nameof(NeedsInstaller),
            nameof(DistributeProblem), nameof(HasDistributeProblem), nameof(HasDistribution),
        })
        {
            OnPropertyChanged(name);
        }

        DistributeCommand.RaiseCanExecuteChanged();
        OpenDistributionCommand.RaiseCanExecuteChanged();
        OpenUploadPageCommand.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------ actions

    /// <summary>Writes the versions (the build number +1 when asked), builds the archive, and selects it.</summary>
    public async Task ArchiveAsync()
    {
        if (App is not { } app || _platform is not { } platform || PlatformProblem is not null)
        {
            Status = PlatformProblem;
            return;
        }

        if (string.IsNullOrWhiteSpace(_displayVersion) || string.IsNullOrWhiteSpace(_version))
        {
            Status = "Enter the version and the build number.";
            return;
        }

        IsBusy = true;
        try
        {
            var version = _incrementBuild ? AppArchives.NextBuildNumber(_version) : _version.Trim();
            var display = _displayVersion.Trim();
            if (display != _versions?.DisplayVersion || version != _versions?.Version)
            {
                await _actions.WriteVersions(app.Project, display, version).ConfigureAwait(true);
                _versions = (_versions ?? new AppVersions(null, null, null, null, null)) with { DisplayVersion = display, Version = version };
            }

            _version = version;
            OnPropertyChanged(nameof(Version));
            Status = $"Archiving {app.Name} {display} ({version}) for {platform.Label}…";
            var framework = app.Frameworks.First(f => MobileTargets.PlatformOf(f) == platform.Value);
            var result = await _actions.Archive(new ArchiveRequest(app.Project, framework, platform.Value)
            {
                Name = app.Name,
                ApplicationId = _versions?.ApplicationId,
                DisplayVersion = display,
                Version = version,
                WindowsPackageType = _versions?.WindowsPackageType,
            }).ConfigureAwait(true);
            Status = result.Message;
            if (result.Archive is { } archive)
            {
                if (archive.ApplicationId is { } id)
                {
                    _knownIds[app.Project] = id;
                }

                Refresh();
                SelectedArchive = Archives.FirstOrDefault(a => a.Archive.Path == archive.Path) ?? SelectedArchive;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = "The archive failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Signs the selected archive for the channel; then its folder and upload page are a click away.</summary>
    public async Task DistributeAsync()
    {
        if (_selected is not { } row || _channel is not { } channel)
        {
            return;
        }

        if (DistributeProblem is { } problem)
        {
            DistributeStatus = problem;
            return;
        }

        IsBusy = true;
        DistributeStatus = $"Signing {row.Name} {row.Version} for {channel.Label}…";
        try
        {
            _lastDistribution = await _actions.Distribute(row.Archive, channel.Value, Signing()).ConfigureAwait(true);
            DistributeStatus = _lastDistribution.Succeeded && UploadPage is not null
                ? _lastDistribution.Message + $" Upload it in {(channel.Value == DistributionChannel.AppStore ? "Transporter or App Store Connect" : channel.Value == DistributionChannel.GooglePlay ? "the Play Console" : "Partner Center")}."
                : _lastDistribution.Message;
        }
        finally
        {
            IsBusy = false;
            RaiseDistribution();
        }
    }

    private async Task DeleteAsync()
    {
        if (_selected is not { } row)
        {
            return;
        }

        var where = row.Archive.IsXcodeArchive ? " It is Xcode's archive too: Xcode's Organizer won't list it either." : string.Empty;
        if (!await _actions.Confirm("Delete Archive", $"Delete the archive of {row.Name} {row.Version} for {row.Platform} from {row.Created}, and its distributions? This can't be undone.{where}", "Delete").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            _service.Archives.Delete(row.Archive);
            Status = $"Deleted the archive of {row.Name} {row.Version}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = "The archive couldn't be deleted: " + ex.Message;
        }

        Refresh();
    }

    public async Task CreateKeystoreAsync()
    {
        if (string.IsNullOrWhiteSpace(KeystoreFolder) || string.IsNullOrWhiteSpace(KeystoreAlias) || string.IsNullOrWhiteSpace(KeystoreOwner))
        {
            SigningStatus = "Enter the keystore's folder, the key's alias and your name.";
            return;
        }

        if (KeystorePassword != KeystoreConfirmPassword)
        {
            SigningStatus = "The passwords don't match.";
            return;
        }

        var name = string.IsNullOrWhiteSpace(KeystoreName) ? App?.Name ?? "upload" : KeystoreName.Trim();
        var path = Path.Combine(KeystoreFolder.Trim(), string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".keystore");
        var (keystore, message) = await _service.CreateKeystoreAsync(name, path, KeystoreAlias.Trim(), KeystorePassword, Distribution.DistinguishedName(KeystoreOwner, KeystoreOrganization, KeystoreCountry), KeystoreYears, _actions.Log, CancellationToken.None).ConfigureAwait(true);
        SigningStatus = message;
        if (keystore is not null)
        {
            ClearPasswords();
            IsCreatingKeystore = false;
            FillSigning();
            Keystore = Keystores.FirstOrDefault(k => k.Value.Id == keystore.Id);
        }
    }

    public async Task ImportKeystoreAsync()
    {
        if (string.IsNullOrWhiteSpace(KeystorePath) || string.IsNullOrWhiteSpace(KeystoreAlias))
        {
            SigningStatus = "Enter the keystore's file and the key's alias.";
            return;
        }

        var name = string.IsNullOrWhiteSpace(KeystoreName) ? Path.GetFileNameWithoutExtension(KeystorePath) : KeystoreName.Trim();
        var (keystore, message) = await _service.ImportKeystoreAsync(name, KeystorePath.Trim(), KeystoreAlias.Trim(), KeystorePassword, KeyPassword, CancellationToken.None).ConfigureAwait(true);
        SigningStatus = message;
        if (keystore is not null)
        {
            ClearPasswords();
            IsImportingKeystore = false;
            FillSigning();
            Keystore = Keystores.FirstOrDefault(k => k.Value.Id == keystore.Id);
        }
    }

    /// <summary>Forgets the forms' passwords once the credential store has them.</summary>
    private void ClearPasswords()
    {
        KeystorePassword = KeystoreConfirmPassword = KeyPassword = CertificatePassword = string.Empty;
        foreach (var name in new[] { nameof(KeystorePassword), nameof(KeystoreConfirmPassword), nameof(KeyPassword), nameof(CertificatePassword) })
        {
            OnPropertyChanged(name);
        }
    }

    private async Task RemoveKeystoreAsync()
    {
        if (_keystore is not { } keystore
            || !await _actions.Confirm("Remove Signing Key", $"Remove {keystore.Label} from your signing keys, and its passwords from the credential store? The keystore file stays where it is.", "Remove").ConfigureAwait(true))
        {
            return;
        }

        await _service.Identities.RemoveAsync(keystore.Value.Id, CancellationToken.None).ConfigureAwait(true);
        SigningStatus = $"Removed {keystore.Value.Name}.";
        FillSigning();
        RaiseDistribution();
    }

    private async Task CreateTestCertificateAsync()
    {
        if (_selected?.Archive is not { } archive)
        {
            return;
        }

        var (certificate, cer, message) = await _service.CreateTestCertificateAsync(archive, _actions.Log, CancellationToken.None).ConfigureAwait(true);
        SigningStatus = message;
        if (certificate is not null)
        {
            _testThumbprint = certificate.Thumbprint;
            _testCertificateFile = cer;
            _storeCertificates = [.. _storeCertificates, certificate];
            FillSigning();
            Certificate = Certificates.FirstOrDefault(c => c.Value is StoreCertificate s && s.Thumbprint == certificate.Thumbprint);
        }
    }

    private async Task ImportCertificateAsync()
    {
        if (!File.Exists(CertificatePath))
        {
            SigningStatus = "Choose the .pfx file.";
            return;
        }

        var file = await _service.Identities.AddCertificateAsync(Path.GetFileNameWithoutExtension(CertificatePath), CertificatePath, CertificatePassword, CancellationToken.None).ConfigureAwait(true);
        IsImportingCertificate = false;
        ClearPasswords();
        SigningStatus = $"Added {file.Name}.";
        FillSigning();
        Certificate = Certificates.FirstOrDefault(c => c.Value is CertificateFile f && f.Id == file.Id);
    }
}
