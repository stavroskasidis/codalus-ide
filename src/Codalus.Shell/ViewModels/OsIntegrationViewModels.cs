using System.Text.Json;
using Codalus.Platform;
using Codalus.Workbench.Commands;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Settings;
using Codalus.Workbench.Setup;

namespace Codalus.Shell.ViewModels;

/// <summary>The words of System Integration (M51, ADR 0084), shared by Options and the first-run setup.</summary>
public static class OsIntegrationText
{
    public static string Category => Localizer.Get("msg.osIntegration.category", "System Integration");

    public static string Title(OsIntegrationFeature feature) => feature == OsIntegrationFeature.Folders
        ? Localizer.Get("msg.osIntegration.foldersTitle", "Open Folders from the File Manager")
        : Localizer.Get("msg.osIntegration.fileTypesTitle", "Open Solutions and Project Files");

    /// <summary>What turning it on does on this OS, and that turning it off removes it.</summary>
    public static string Description(OsIntegrationFeature feature, PlatformKind platform) => (feature, platform) switch
    {
        (OsIntegrationFeature.Folders, PlatformKind.Windows) => Localizer.Get("msg.osIntegration.foldersWindows", "Adds Open with Codalus IDE to a folder's right-click in File Explorer, for you only. Turning it off removes it."),
        (OsIntegrationFeature.Folders, PlatformKind.MacOS) => Localizer.Get("msg.osIntegration.foldersMac", "Adds an Open with Codalus IDE Quick Action to a folder's right-click in the Finder. Turning it off removes it."),
        (OsIntegrationFeature.Folders, _) => Localizer.Get("msg.osIntegration.foldersLinux", "Adds Open with Codalus IDE to a folder's right-click in GNOME Files (Scripts), Dolphin, Nemo and Thunar. Turning it off removes it."),
        (_, PlatformKind.Windows) => Localizer.Get("msg.osIntegration.fileTypesWindows", "Solutions (.sln, .slnx, .slnf) and project files (.csproj, .vbproj, .fsproj) open in Codalus IDE. Windows doesn't let an app make itself the default, so it may ask once which app to use: choose Codalus IDE. Turning it off removes it."),
        (_, PlatformKind.MacOS) => Localizer.Get("msg.osIntegration.fileTypesMac", "Solutions (.sln, .slnx, .slnf) and project files (.csproj, .vbproj, .fsproj) open in Codalus IDE from the Finder. Turning it off gives them back to the app that opened them before."),
        _ => Localizer.Get("msg.osIntegration.fileTypesLinux", "Solutions (.sln, .slnx, .slnf) and project files (.csproj, .vbproj, .fsproj) open in Codalus IDE: their file types are registered for you and Codalus IDE becomes their default application. Turning it off removes exactly that."),
    };

    /// <summary>A line on how it stands, or null when it's simply off.</summary>
    public static string? Status(OsIntegrationStatus status, string program) => status.State switch
    {
        OsIntegrationState.Registered => Localizer.Format("msg.osIntegration.registered", "On, for {0}.", program),
        OsIntegrationState.Partial => Localizer.Get("msg.osIntegration.partial", "Partly there (something was removed, or a file manager was installed since). Clicking removes what's left; clicking again registers it all."),
        OsIntegrationState.OtherCopy => status.Detail is { } found
            ? Localizer.Format("msg.osIntegration.otherCopyFound", "On for another copy of Codalus IDE ({0}). Clicking removes it; clicking again registers this copy.", found)
            : Localizer.Get("msg.osIntegration.otherCopy", "On for another copy of Codalus IDE. Clicking removes it; clicking again registers this copy."),
        OsIntegrationState.Unavailable => status.Detail,
        _ => null,
    };
}

/// <summary>
/// A System Integration toggle in Options (M51): a check box that registers or removes the feature, showing what the
/// registry or the files say (undetermined when it's partly there or another copy's). It isn't a setting: nothing goes to
/// settings.json, so the page can't disagree with the system.
/// </summary>
public sealed class OsIntegrationSettingViewModel : SettingItemViewModel
{
    private readonly ShellHost _shell;
    private OsIntegrationStatus _state = new(OsIntegrationState.NotRegistered);
    private bool? _isOn = false;
    private bool _isBusy;

    public OsIntegrationSettingViewModel(ShellHost shell, OsIntegrationFeature feature, SettingsService settings, Func<SettingScope> scope)
        : base(DescriptorFor(feature, shell.Workbench.Platform), settings, scope)
    {
        _shell = shell;
        Feature = feature;
        Refresh();
    }

    public OsIntegrationFeature Feature { get; }

    /// <summary>The keys these items search by; they aren't in settings.json.</summary>
    public static string KeyOf(OsIntegrationFeature feature) => feature == OsIntegrationFeature.Folders ? "os.integration.folders" : "os.integration.fileTypes";

    private static SettingDescriptor DescriptorFor(OsIntegrationFeature feature, PlatformKind platform) =>
        new(KeyOf(feature), SettingType.Boolean, Json("false"), ContributionSource.Shell)
        {
            Title = OsIntegrationText.Title(feature),
            Description = OsIntegrationText.Description(feature, platform),
            Category = OsIntegrationText.Category,
            Scope = SettingScope.Workspace, // editable on either tab: it's the machine's, not a layer's
        };

    public override bool IsBool => true;

    public override bool IsSetting => false;

    public OsIntegrationStatus State => _state;

    public override string? Status => _isBusy
        ? (_isOn == true ? Localizer.Get("msg.osIntegration.registering", "Registering…") : Localizer.Get("msg.osIntegration.removing", "Removing…"))
        : OsIntegrationText.Status(_state, _shell.OsIntegration.Launcher.Program);

    /// <summary>On, off, or undetermined (partly there, another copy's): clicking an undetermined one removes it.</summary>
    public bool? IsOn
    {
        get => _isOn;
        set
        {
            if (Refreshing || _isBusy || value == _isOn)
            {
                return;
            }

            _isOn = value;
            OnPropertyChanged();
            _ = ApplyAsync(value == true);
        }
    }

    /// <summary>The check box can be clicked (not while it works, not where the feature can't be offered).</summary>
    public bool CanToggle => !_isBusy && _state.State != OsIntegrationState.Unavailable;

    public bool IsBusy => _isBusy;

    /// <summary>Registers or removes the feature; done when the status has been read back.</summary>
    public async Task ApplyAsync(bool on)
    {
        _isBusy = true;
        Error = null;
        Changed();
        try
        {
            var (status, error) = await _shell.SetOsIntegrationAsync(Feature, on).ConfigureAwait(true);
            _state = status;
            Error = error;
        }
        finally
        {
            _isBusy = false;
            _isOn = _state.IsOn;
            Changed();
        }
    }

    /// <summary>Reads the status again (quick: the registry or a few files).</summary>
    protected override void Load(JsonElement value)
    {
        if (_isBusy)
        {
            return;
        }

        _state = _shell.OsIntegration.GetStatus(Feature);
        _isOn = _state.IsOn;
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(IsBusy));
    }
}
