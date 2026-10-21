using System.Windows.Input;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.ViewModels;

/// <summary>The three pages of New Project (ADR 0082).</summary>
public enum NewProjectPage
{
    /// <summary>Create a new project: recent templates, search, filters and the template list.</summary>
    Choose,

    /// <summary>Configure your new project: names, location, the solution and trust.</summary>
    Configure,

    /// <summary>Additional information: the framework and the template's options.</summary>
    Additional,
}

/// <summary>A piece of a template's name, matching the search or not (the view draws matches bold).</summary>
public sealed record HighlightRun(string Text, bool IsMatch);

/// <summary>An entry of a filter combo box: a vocabulary id (null: all) and its label.</summary>
public sealed record FilterOption(string? Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>An entry of the Solution combo box on the Configure page.</summary>
public sealed record SolutionModeChoice(SolutionMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>An authentication type on the Additional information page.</summary>
public sealed record AuthChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// A row of New Project's template list or of its recent templates: the icon, the name (with the search's matches),
/// the description and the tag chips; for a template package that isn't installed, its install state.
/// </summary>
public sealed class NewProjectTemplateRow : ObservableObject
{
    private readonly NewProjectViewModel _owner;
    private IReadOnlyList<HighlightRun> _nameRuns;
    private bool _isPinned;
    private bool _isInstalling;
    private string? _installError;

    internal NewProjectTemplateRow(NewProjectViewModel owner, ProjectTemplateGroup group, bool isRecent, bool isPinned)
    {
        _owner = owner;
        Group = group;
        IsRecent = isRecent;
        _isPinned = isPinned;
        _nameRuns = [new HighlightRun(group.Name, false)];
        PinCommand = new RelayCommand(() => owner.SetPinned(this, !IsPinned));
        RemoveCommand = new RelayCommand(() => owner.RemoveRecent(this), () => IsRecent);
    }

    public ProjectTemplateGroup Group { get; }

    public string Id => Group.Id;

    public string Name => Group.Name;

    public string Description => Group.Description;

    public string IconKind => Group.IconKind;

    public IReadOnlyList<string> Chips => Group.Chips;

    /// <summary>The name in pieces, the search's matches marked.</summary>
    public IReadOnlyList<HighlightRun> NameRuns
    {
        get => _nameRuns;
        internal set => SetField(ref _nameRuns, value);
    }

    /// <summary>Its projects run only on Windows, and this isn't Windows: the row says so.</summary>
    public bool ShowWindowsOnly => Group.RunsOnlyOnWindows && !_owner.IsWindows;

    public string WindowsOnlyText => Localizer.Get("msg.newProject.windowsOnly", "Windows only");

    public string WindowsOnlyTip => Localizer.Get("msg.newProject.windowsOnlyTip", "Its projects build here but run only on Windows.");

    public bool IsRecent { get; }

    public bool IsPinned
    {
        get => _isPinned;
        internal set
        {
            if (SetField(ref _isPinned, value))
            {
                OnPropertyChanged(nameof(PinTitle));
            }
        }
    }

    /// <summary>The context menu's Pin to List or Unpin from List.</summary>
    public string PinTitle => IsPinned ? Localizer.Get("msg.newProject.unpinFromList", "Unpin from List") : Localizer.Get("msg.newProject.pinToList", "Pin to List");

    public string RemoveTitle => Localizer.Get("msg.newProject.removeFromList", "Remove from List");

    public ICommand PinCommand { get; }

    public ICommand RemoveCommand { get; }

    /// <summary>A template package to install (the primary button reads Install).</summary>
    public bool IsPack => Group.Kind == ProjectTemplateKind.Pack;

    public string? InstallDescription => IsPack ? Localizer.Format("msg.newProject.installsPack", "Installs {0} from nuget.org with dotnet new install.", Group.Package) : null;

    public bool IsInstalling
    {
        get => _isInstalling;
        internal set
        {
            if (SetField(ref _isInstalling, value))
            {
                OnPropertyChanged(nameof(InstallStatus));
            }
        }
    }

    public string? InstallStatus => IsInstalling ? Localizer.Format("msg.newProject.installingPack", "Installing {0}…", Group.Package) : null;

    public string? InstallError
    {
        get => _installError;
        internal set => SetField(ref _installError, value);
    }

    /// <summary>What a screen reader says for the row: the name, the description and the tags.</summary>
    public string AccessibleName => string.Join(", ", new[] { Name, Description }.Where(t => t.Length > 0).Concat(Chips).Concat(ShowWindowsOnly ? [WindowsOnlyText] : []));
}

/// <summary>
/// An option of the Additional information page: one of the template's parameters (<see cref="TemplateParameter"/>),
/// with its label's access key, its tooltip, and whether its condition enables it now.
/// </summary>
public abstract class TemplateOptionViewModel : ObservableObject
{
    private bool _isEnabled = true;
    private string _label;

    protected TemplateOptionViewModel(TemplateParameter parameter)
    {
        Parameter = parameter;
        _label = parameter.Label;
    }

    /// <summary>A value changed (the page re-evaluates the conditions).</summary>
    public event EventHandler? ValueChanged;

    public TemplateParameter Parameter { get; }

    public string Name => Parameter.Name;

    /// <summary>The label with its access key (<see cref="AccessKeyAllocator"/>).</summary>
    public string Label
    {
        get => _label;
        internal set => SetField(ref _label, value);
    }

    public string? ToolTip => Parameter.Description;

    public bool IsEnabled
    {
        get => _isEnabled;
        internal set => SetField(ref _isEnabled, value);
    }

    /// <summary>The value as the template sees it (an inverted check box turned back).</summary>
    public abstract string? SymbolValue { get; set; }

    protected void RaiseValueChanged() => ValueChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>A check box: the template's bool parameter, shown inverted when its IDE host file says so.</summary>
public sealed class BoolOptionViewModel : TemplateOptionViewModel
{
    private bool _isChecked;

    public BoolOptionViewModel(TemplateParameter parameter, string? symbolValue)
        : base(parameter) => SymbolValue = symbolValue ?? parameter.DefaultValue ?? "false";

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetField(ref _isChecked, value))
            {
                RaiseValueChanged();
            }
        }
    }

    public override string? SymbolValue
    {
        get => (Parameter.Invert ? !IsChecked : IsChecked) ? "true" : "false";
        set
        {
            var flag = bool.TryParse(value, out var parsed) && parsed;
            IsChecked = Parameter.Invert ? !flag : flag;
        }
    }
}

/// <summary>A combo box: the template's choice parameter.</summary>
public sealed class ChoiceOptionViewModel : TemplateOptionViewModel
{
    private TemplateChoice? _selected;

    public ChoiceOptionViewModel(TemplateParameter parameter, string? symbolValue)
        : base(parameter) => SymbolValue = symbolValue ?? parameter.DefaultValue;

    public IReadOnlyList<TemplateChoice> Choices => Parameter.Choices;

    public TemplateChoice? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                RaiseValueChanged();
            }
        }
    }

    public override string? SymbolValue
    {
        get => Selected?.Value;
        set => Selected = Choices.FirstOrDefault(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase)) ?? Choices.FirstOrDefault(c => c.Value == Parameter.DefaultValue) ?? (Choices.Count > 0 ? Choices[0] : null);
    }
}

/// <summary>A text box: the template's text or number parameter.</summary>
public sealed class TextOptionViewModel : TemplateOptionViewModel
{
    private string _text = string.Empty;

    public TextOptionViewModel(TemplateParameter parameter, string? symbolValue)
        : base(parameter) => _text = symbolValue ?? parameter.DefaultValue ?? string.Empty;

    public string Text
    {
        get => _text;
        set
        {
            if (SetField(ref _text, value))
            {
                RaiseValueChanged();
            }
        }
    }

    public override string? SymbolValue
    {
        get => Text;
        set => Text = value ?? string.Empty;
    }
}
