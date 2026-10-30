using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Setup;

namespace Codalus.Shell.ViewModels;

/// <summary>Help → Check Environment (M37) as a document.</summary>
public sealed class EnvironmentDocument : ShellDocument
{
    public const string DocumentId = "checkEnvironment";

    public EnvironmentDocument(EnvironmentViewModel viewModel)
    {
        Id = DocumentId;
        Title = Localizer.Get("msg.setup.pageTitle", "Check Environment");
        ViewModel = viewModel;
    }

    public override string Kind => "checkEnvironment";

    public EnvironmentViewModel ViewModel { get; }
}

/// <summary>A tool's row: how it stands, and its fix.</summary>
public sealed class ToolRowViewModel(ToolStatus tool, InstallPlan? plan, Func<ToolStatus, Task> install)
{
    public ToolStatus Tool { get; } = tool;

    public string Title => Tool.Title;

    public string State => Tool.State switch
    {
        ToolState.Found => Tool.Version is { } v ? Localizer.Format("msg.setup.found", "Found, {0}", v) : Localizer.Get("msg.setup.foundNoVersion", "Found"),
        ToolState.Mismatch => Localizer.Get("msg.setup.mismatch", "Not the version needed"),
        _ => Tool.Required ? Localizer.Get("msg.setup.missingRequired", "Missing (needed)") : Localizer.Get("msg.setup.missingOptional", "Not installed (optional)"),
    };

    public string Icon => Tool.State switch
    {
        ToolState.Found => "test-passed",
        _ when Tool.Required => "error",
        _ => "warning",
    };

    public string NeededFor => Tool.NeededFor;

    public string Details => string.Join(" · ", new[] { Tool.Path, Tool.Detail }.Where(s => !string.IsNullOrEmpty(s)));

    public bool HasDetails => Details.Length > 0;

    /// <summary>Install…, Open Page… or Run in Terminal…, when there's a plan and it's needed.</summary>
    public string FixTitle => plan?.Kind switch
    {
        InstallKind.OpenPage => Localizer.Get("msg.setup.fixPage", "Open Download Page…"),
        InstallKind.Terminal => Localizer.Get("msg.setup.fixTerminal", "Install in Terminal…"),
        _ => Localizer.Get("msg.setup.fixInstall", "Install…"),
    };

    public bool CanFix => plan is not null && Tool.NeedsAttention;

    public string? Command => plan?.Command;

    public ICommand FixCommand => new RelayCommand(() => _ = install(Tool));
}

/// <summary>Every tool Codalus IDE uses, as found now, with a fix for what's missing; Check Again runs the check.</summary>
public sealed class EnvironmentViewModel : ObservableObject
{
    private readonly ShellHost _shell;
    private bool _checking;

    public EnvironmentViewModel(ShellHost shell)
    {
        _shell = shell;
        shell.ToolsChanged += (_, _) => Ui.Post(Fill);
        CheckCommand = new RelayCommand(() => _ = CheckAsync(), () => !_checking);
        Fill();
        if (shell.Tools.Count == 0 || !shell.ToolsComplete)
        {
            _ = CheckAsync();
        }
    }

    public ObservableCollection<ToolRowViewModel> Rows { get; } = [];

    public bool IsChecking
    {
        get => _checking;
        private set => SetField(ref _checking, value);
    }

    public ICommand CheckCommand { get; }

    public async Task CheckAsync()
    {
        IsChecking = true;
        try
        {
            await _shell.CheckEnvironmentAsync(offer: false).ConfigureAwait(true);
        }
        finally
        {
            IsChecking = false;
        }
    }

    private void Fill()
    {
        Rows.Clear();
        foreach (var tool in _shell.Tools)
        {
            Rows.Add(new ToolRowViewModel(tool, _shell.PlanFor(tool), t => _shell.InstallToolAsync(t)));
        }
    }
}
