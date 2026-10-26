using System.Collections.ObjectModel;
using System.Windows.Input;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Feedback;
using Codalus.Workbench.Localization;

namespace Codalus.Shell.ViewModels;

/// <summary>What the Report a Problem dialog needs from the shell (tests fake it): nothing here reaches the network.</summary>
public interface IFeedbackHost
{
    /// <summary>The IDE's version, OS and runtime (the SDK comes later, from <see cref="FindSdkAsync"/>).</summary>
    IssueEnvironment Environment { get; }

    /// <summary>The IDE's own log: Output's Codalus IDE channel.</summary>
    string LogText { get; }

    /// <summary>The user's home folder, written as <c>~</c> in what's attached.</summary>
    string? Home { get; }

    /// <summary>The .NET SDK <c>dotnet --version</c> names (in the solution's folder), or null.</summary>
    Task<string?> FindSdkAsync(CancellationToken cancellationToken);

    /// <summary>Saves the attachments that don't fit the link in the logs folder; the file's path, or null when it couldn't be written.</summary>
    string? SaveLogs(IssueDraft draft, string fileName);

    /// <summary>Opens the new issue in the browser; false when it couldn't (the host has said why).</summary>
    bool OpenUrl(Uri url);

    /// <summary>Shows a file in the file manager.</summary>
    void Reveal(string path);

    Task CopyAsync(string text);
}

/// <summary>A footer button of the feedback dialog.</summary>
public sealed record FeedbackButton(string Label, bool IsPrimary, ICommand Command);

/// <summary>
/// Report a Problem and Suggest a Feature (M51): an in-window dialog with the issue's title, description and steps (or
/// proposal), and a read-only preview of exactly what is attached (the version, the OS, the runtime, the SDK and, for a
/// problem, the IDE log's last lines, redacted). Open in Browser opens a new issue on the repository, prefilled
/// (<see cref="IssueReport"/>), for the user to submit there; logs too long for the link are saved to a file and shown.
/// Copy puts the whole report on the clipboard. Nothing is sent from the IDE.
/// </summary>
public sealed class FeedbackViewModel : ObservableObject, IDisposable
{
    private readonly IFeedbackHost _host;
    private readonly RelayCommand _open;
    private TaskCompletionSource? _closed;
    private CancellationTokenSource? _cts;
    private IssueKind _kind;
    private IssueEnvironment? _environment;
    private string? _logs;
    private string _issueTitle = string.Empty;
    private string _description = string.Empty;
    private string _details = string.Empty;
    private bool _includeLogs;
    private string _preview = string.Empty;
    private string? _note;

    public FeedbackViewModel(IFeedbackHost host)
    {
        _host = host;
        _open = new RelayCommand(OpenInBrowser, () => CanOpen);
        CopyCommand = new RelayCommand(() => _ = CopyAsync());
        CancelCommand = new RelayCommand(Cancel);
    }

    public event EventHandler? Opened;

    public bool IsOpen => _closed is not null;

    public IssueKind Kind
    {
        get => _kind;
        private set
        {
            if (SetField(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsProblem));
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Icon));
                OnPropertyChanged(nameof(DescriptionLabel));
                OnPropertyChanged(nameof(DescriptionHint));
                OnPropertyChanged(nameof(DetailsLabel));
                OnPropertyChanged(nameof(DetailsHint));
                OnPropertyChanged(nameof(CanIncludeLogs));
            }
        }
    }

    public bool IsProblem => Kind == IssueKind.Problem;

    public string Title => IsProblem
        ? Localizer.Get("msg.feedback.reportProblem", "Report a Problem")
        : Localizer.Get("msg.feedback.suggestFeature", "Suggest a Feature");

    public string Context => Localizer.Format("msg.feedback.context", "A new issue on github.com/{0}, opened in your browser for you to submit", IssueReport.Repository);

    /// <summary>The header's icon: the issue (orange) for a problem, the light bulb (yellow) for a feature.</summary>
    public string Icon => IsProblem ? "issue" : "lightbulb";

    public string DescriptionLabel => IsProblem
        ? Localizer.Get("msg.feedback.whatHappened", "What happened")
        : Localizer.Get("msg.feedback.theProblem", "The problem");

    public string DescriptionHint => IsProblem
        ? Localizer.Get("msg.feedback.whatHappenedHint", "What you did, what you expected, and what happened instead.")
        : Localizer.Get("msg.feedback.theProblemHint", "What are you trying to do, and what gets in the way today?");

    public string DetailsLabel => IsProblem
        ? Localizer.Get("msg.feedback.stepsToReproduce", "Steps to reproduce")
        : Localizer.Get("msg.feedback.proposal", "Proposal");

    public string DetailsHint => IsProblem
        ? Localizer.Get("msg.feedback.stepsHint", "Numbered steps. A small solution or project that shows the problem helps most.")
        : Localizer.Get("msg.feedback.proposalHint", "How you'd like it to work. Mention other tools that do it well, if any.");

    public string IssueTitle
    {
        get => _issueTitle;
        set
        {
            if (SetField(ref _issueTitle, value ?? string.Empty))
            {
                Update();
            }
        }
    }

    public string Description
    {
        get => _description;
        set
        {
            if (SetField(ref _description, value ?? string.Empty))
            {
                Update();
            }
        }
    }

    public string Details
    {
        get => _details;
        set
        {
            if (SetField(ref _details, value ?? string.Empty))
            {
                Update();
            }
        }
    }

    /// <summary>A problem attaches the log's last lines (on by default); a feature request attaches only the version and the OS.</summary>
    public bool IncludeLogs
    {
        get => _includeLogs;
        set
        {
            if (SetField(ref _includeLogs, value))
            {
                Update();
            }
        }
    }

    public bool CanIncludeLogs => IsProblem && _logs is { Length: > 0 };

    public string IncludeLogsLabel => Localizer.Format("msg.feedback.includeLogs", "Include the last {0} lines of the IDE's log (secrets and your home folder redacted)", IssueReport.LogLines);

    /// <summary>Exactly what is attached besides the text above it, as it is sent.</summary>
    public string Preview
    {
        get => _preview;
        private set => SetField(ref _preview, value);
    }

    /// <summary>Why something won't go in the link (the logs go to a file, or the text is cut), or null.</summary>
    public string? Note
    {
        get => _note;
        private set
        {
            if (SetField(ref _note, value))
            {
                OnPropertyChanged(nameof(HasNote));
            }
        }
    }

    public bool HasNote => _note is not null;

    /// <summary>A title and a description: the form requires the description, and an issue needs a title.</summary>
    public bool CanOpen => _issueTitle.Trim().Length > 0 && _description.Trim().Length > 0;

    public ObservableCollection<FeedbackButton> Buttons { get; } = [];

    public ICommand OpenCommand => _open;

    public ICommand CopyCommand { get; }

    public ICommand CancelCommand { get; }

    /// <summary>The report as it stands.</summary>
    public IssueDraft Draft => new(Kind, _issueTitle.Trim(), _description.Trim(), _details.Trim(), _environment ?? _host.Environment)
    {
        Logs = IsProblem && _includeLogs ? _logs : null,
    };

    /// <summary>Opens the dialog for <paramref name="kind"/>; completes when it closes. Called again while open, it returns the open one.</summary>
    public Task OpenAsync(IssueKind kind)
    {
        if (_closed is { } open)
        {
            return open.Task;
        }

        _closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cts = new CancellationTokenSource();
        _environment = _host.Environment;
        _logs = kind == IssueKind.Problem ? IssueReport.LogTail(_host.LogText, _host.Home) : null;
        Kind = kind;
        OnPropertyChanged(nameof(Context));
        OnPropertyChanged(nameof(CanIncludeLogs));
        OnPropertyChanged(nameof(IncludeLogsLabel));
        _issueTitle = _description = _details = string.Empty;
        OnPropertyChanged(nameof(IssueTitle));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Details));
        _includeLogs = CanIncludeLogs;
        OnPropertyChanged(nameof(IncludeLogs));
        Buttons.Clear();
        var buttons = new[]
        {
            new FeedbackButton(Localizer.Get("msg.feedback.openInBrowser", "Open in Browser"), true, _open),
            new FeedbackButton(Localizer.Get("msg.feedback.copy", "Copy"), false, CopyCommand),
            new FeedbackButton(Localizer.Get("msg.feedback.cancel", "Cancel"), false, CancelCommand),
        };
        foreach (var label in DialogViewModel.Order([.. buttons.Select(b => b.Label)], buttons[0].Label, DialogViewModel.PrimaryLastOnThisOs))
        {
            Buttons.Add(buttons.First(b => b.Label == label));
        }

        Update();
        OnPropertyChanged(nameof(IsOpen));
        Opened?.Invoke(this, EventArgs.Empty);
        _ = FindSdkAsync(_cts.Token);
        return _closed.Task;
    }

    private async Task FindSdkAsync(CancellationToken cancellationToken)
    {
        string? sdk;
        try
        {
            sdk = await _host.FindSdkAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return;
        }

        if (sdk is { Length: > 0 } && !cancellationToken.IsCancellationRequested && _environment is { } environment)
        {
            _environment = environment with { Sdk = sdk.Trim() };
            Update();
        }
    }

    /// <summary>The preview, the note and Open in Browser's state after any change.</summary>
    private void Update()
    {
        if (_environment is null)
        {
            return;
        }

        var draft = Draft;
        Preview = IssueReport.AttachmentsText(draft);
        var link = IssueReport.Build(draft, IssueReport.LogFileName(DateTime.Now));
        Note = link.LogsInFile
            ? Localizer.Get("msg.feedback.logsInFile", "The log is too long for a link: Open in Browser saves it to a file in the logs folder and shows it, for you to attach to the issue.")
            : link.Shortened
                ? Localizer.Get("msg.feedback.shortened", "The text is too long for a link: the issue gets the start of it. Copy has all of it, to paste into the issue.")
                : null;
        _open.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanOpen));
    }

    /// <summary>
    /// Open in Browser: saves the logs to a file when they don't fit the link, opens the new issue in the browser, shows the
    /// file, and closes. The user submits the issue on GitHub; nothing is sent from here.
    /// </summary>
    public void OpenInBrowser()
    {
        if (!IsOpen || !CanOpen)
        {
            return;
        }

        var draft = Draft;
        var fileName = IssueReport.LogFileName(DateTime.Now);
        var link = IssueReport.Build(draft, fileName);
        string? saved = null;
        if (link.LogsInFile && (saved = _host.SaveLogs(draft, fileName)) is null)
        {
            return; // the host said why; the dialog stays, with Copy
        }

        if (!_host.OpenUrl(link.Url))
        {
            return;
        }

        if (saved is not null)
        {
            _host.Reveal(saved);
        }

        Close();
    }

    /// <summary>Copy: the whole report as Markdown (nothing cut), for an issue written by hand.</summary>
    public Task CopyAsync() => IsOpen ? _host.CopyAsync(IssueReport.Markdown(Draft)) : Task.CompletedTask;

    public void Cancel() => Close();

    public void Dispose() => Close();

    private void Close()
    {
        if (_closed is not { } closed)
        {
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _closed = null;
        Buttons.Clear();
        OnPropertyChanged(nameof(IsOpen));
        closed.TrySetResult();
    }
}
