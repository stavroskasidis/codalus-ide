using System.Collections.ObjectModel;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Publishing;

namespace Codalus.Shell.ViewModels;

/// <summary>
/// The Publish page of a project (M17c, ADR 0042): its publish profiles (<c>Properties/PublishProfiles/*.pubxml</c>),
/// one edited at a time (folder or container image; configuration, framework, runtime, self-contained, single-file,
/// trimmed, ReadyToRun, Native AOT), saved, and published through the build host.
/// </summary>
public sealed class PublishViewModel : ObservableObject
{
    private readonly Func<PublishProfile, Task<string>> _publish;
    private readonly Func<string, Task<string?>> _askName;
    private PublishProfile? _profile;
    private string? _selectedName;
    private string? _status;
    private bool _isBusy;

    public PublishViewModel(string project, IReadOnlyList<string> frameworks, Func<PublishProfile, Task<string>> publish, Func<string, Task<string?>> askName)
    {
        Project = project;
        Frameworks = frameworks;
        _publish = publish;
        _askName = askName;
        PublishCommand = new RelayCommand(() => _ = PublishAsync(), () => _profile is not null && !_isBusy);
        SaveCommand = new RelayCommand(() => Save(), () => _profile is not null);
        NewCommand = new RelayCommand(() => _ = NewAsync());
        Reload();
        if (Profiles.Count == 0)
        {
            Add(new PublishProfile { Name = "FolderProfile", TargetFramework = frameworks.Count > 0 ? frameworks[0] : null });
        }
    }

    public string Project { get; }

    public string ProjectName => Path.GetFileNameWithoutExtension(Project);

    /// <summary>The project's folder, which the publish folder is relative to.</summary>
    public string? ProjectFolder => Path.GetDirectoryName(Project);

    public IReadOnlyList<string> Frameworks { get; }

    public IReadOnlyList<string> Configurations { get; } = ["Release", "Debug"];

    public IReadOnlyList<string> Targets { get; } = ["Folder", "Container image"];

    /// <summary>Common runtime identifiers; any other can be typed.</summary>
    public IReadOnlyList<string> Runtimes { get; } = ["", "win-x64", "win-arm64", "linux-x64", "linux-arm64", "linux-musl-x64", "osx-arm64", "osx-x64"];

    public ObservableCollection<string> Profiles { get; } = [];

    public RelayCommand PublishCommand { get; }

    public RelayCommand SaveCommand { get; }

    public RelayCommand NewCommand { get; }

    public PublishProfile? Profile => _profile;

    public string? SelectedName
    {
        get => _selectedName;
        set
        {
            if (SetField(ref _selectedName, value))
            {
                _profile = value is null ? null : PublishProfiles.List(Project).FirstOrDefault(p => p.Name == value) ?? (_profile?.Name == value ? _profile : null);
                Refresh();
            }
        }
    }

    public string? Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                PublishCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // ------------------------------------------------------------------ the edited profile

    public int TargetIndex
    {
        get => _profile?.Target == PublishTarget.Container ? 1 : 0;
        set => Edit(p => p.Target = value == 1 ? PublishTarget.Container : PublishTarget.Folder);
    }

    public bool IsFolder => _profile?.Target != PublishTarget.Container;

    public bool IsContainer => _profile?.Target == PublishTarget.Container;

    public string Configuration
    {
        get => _profile?.Configuration ?? "Release";
        set => Edit(p => p.Configuration = value);
    }

    public string? TargetFramework
    {
        get => _profile?.TargetFramework;
        set => Edit(p => p.TargetFramework = value);
    }

    public string RuntimeIdentifier
    {
        get => _profile?.RuntimeIdentifier ?? string.Empty;
        set => Edit(p => p.RuntimeIdentifier = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public bool SelfContained
    {
        get => _profile?.SelfContained == true;
        set => Edit(p => p.SelfContained = value);
    }

    public bool SingleFile
    {
        get => _profile?.SingleFile == true;
        set => Edit(p => p.SingleFile = value);
    }

    public bool Trimmed
    {
        get => _profile?.Trimmed == true;
        set => Edit(p => p.Trimmed = value);
    }

    public bool ReadyToRun
    {
        get => _profile?.ReadyToRun == true;
        set => Edit(p => p.ReadyToRun = value);
    }

    public bool NativeAot
    {
        get => _profile?.NativeAot == true;
        set => Edit(p => p.NativeAot = value);
    }

    public string Folder
    {
        get => _profile?.Folder ?? string.Empty;
        set => Edit(p => p.Folder = value);
    }

    public string ContainerRegistry
    {
        get => _profile?.ContainerRegistry ?? string.Empty;
        set => Edit(p => p.ContainerRegistry = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public string ContainerRepository
    {
        get => _profile?.ContainerRepository ?? string.Empty;
        set => Edit(p => p.ContainerRepository = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public string ContainerImageTag
    {
        get => _profile?.ContainerImageTag ?? string.Empty;
        set => Edit(p => p.ContainerImageTag = string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    /// <summary>Why the profile can't publish as it is, or null.</summary>
    public string? Problem => _profile?.Problem();

    public bool HasProblem => Problem is not null;

    private void Edit(Action<PublishProfile> change)
    {
        if (_profile is null)
        {
            return;
        }

        change(_profile);
        Refresh();
    }

    private void Refresh()
    {
        foreach (var name in new[]
        {
            nameof(TargetIndex), nameof(IsFolder), nameof(IsContainer), nameof(Configuration), nameof(TargetFramework), nameof(RuntimeIdentifier),
            nameof(SelfContained), nameof(SingleFile), nameof(Trimmed), nameof(ReadyToRun), nameof(NativeAot), nameof(Folder),
            nameof(ContainerRegistry), nameof(ContainerRepository), nameof(ContainerImageTag), nameof(Problem), nameof(HasProblem), nameof(Profile),
        })
        {
            OnPropertyChanged(name);
        }

        PublishCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
    }

    private void Reload()
    {
        var selected = _selectedName;
        Profiles.Clear();
        foreach (var profile in PublishProfiles.List(Project))
        {
            Profiles.Add(profile.Name);
        }

        if (selected is not null && Profiles.Contains(selected))
        {
            _selectedName = null;
            SelectedName = selected;
        }
        else if (Profiles.Count > 0)
        {
            SelectedName = Profiles[0];
        }
    }

    /// <summary>Adds a new profile (saved at once) and selects it.</summary>
    public void Add(PublishProfile profile)
    {
        if (profile.Target == PublishTarget.Container)
        {
            profile.ContainerRepository ??= ProjectName.ToLowerInvariant();
            profile.ContainerImageTag ??= "latest";
        }
        else if (profile.Folder == "bin/Release/publish/")
        {
            profile.Folder = $"bin/Release/{profile.TargetFramework ?? "net"}/publish/";
        }

        PublishProfiles.Save(Project, profile);
        _profile = profile;
        Reload();
        SelectedName = profile.Name;
    }

    private async Task NewAsync()
    {
        var name = await _askName(NextName()).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        Add(new PublishProfile { Name = name.Trim(), TargetFramework = Frameworks.Count > 0 ? Frameworks[0] : null });
    }

    private string NextName()
    {
        for (var i = 1; ; i++)
        {
            var name = i == 1 ? "FolderProfile" : $"FolderProfile{i}";
            if (!Profiles.Contains(name))
            {
                return name;
            }
        }
    }

    /// <summary>Saves the edited profile; returns its file.</summary>
    public string? Save()
    {
        if (_profile is null)
        {
            return null;
        }

        var file = PublishProfiles.Save(Project, _profile);
        Status = $"Saved {Path.GetFileName(file)}.";
        return file;
    }

    /// <summary>Saves, then publishes with the profile; the status says what came out.</summary>
    public async Task PublishAsync()
    {
        if (_profile is null)
        {
            return;
        }

        if (_profile.Problem() is { } problem)
        {
            Status = problem;
            return;
        }

        Save();
        IsBusy = true;
        Status = $"Publishing {ProjectName} with {_profile.Name}…";
        try
        {
            Status = await _publish(_profile).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
