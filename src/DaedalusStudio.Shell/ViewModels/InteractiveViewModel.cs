using System.Globalization;
using System.Windows.Input;
using DaedalusStudio.Interactive.Protocol;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Interactive;
using DaedalusStudio.Workbench.Output;

namespace DaedalusStudio.Shell.ViewModels;

/// <summary>
/// The C# Interactive window (ADR 0015): a transcript and a multi-line input whose submissions run in
/// <c>daedalus-replhost</c>. Enter runs a complete submission (an incomplete one gets a new line); Alt+Up and Alt+Down
/// walk the history.
/// </summary>
public sealed class InteractiveViewModel : ObservableObject
{
    public const string ToolWindowId = "interactive";

    private readonly List<string> _history = [];
    private string _input = string.Empty;
    private int _historyIndex;
    private bool _isBusy;
    private string _status = "Ready";
    private Editor.EditorTheme _colors = Editor.EditorTheme.Dark;

    public InteractiveViewModel(InteractiveService service, EditorFont font)
    {
        Service = service;
        Font = font;
        Transcript = new OutputChannel(ToolWindowId, "C# Interactive");
        ResetCommand = new RelayCommand(() => _ = ResetAsync(null));
        ClearCommand = new RelayCommand(Transcript.Clear);
        Service.Output += (_, e) => Transcript.Append(e.Text);
        Service.HostRestarted += (_, _) => Transcript.AppendLine("The interactive host stopped and was restarted. The state was reset.");
        Service.Reset += (_, result) =>
        {
            foreach (var error in result.Errors)
            {
                Transcript.AppendLine(error);
            }
        };
        Service.Log += (_, line) => Transcript.AppendLine(line);
        Transcript.AppendLine("C# Interactive. Enter runs a complete submission; #r \"path\" or #r \"nuget: Id, Version\" add references; Reset starts over.");
    }

    public InteractiveService Service { get; }

    public EditorFont Font { get; }

    /// <summary>Submissions, their output and results.</summary>
    public OutputChannel Transcript { get; }

    public ICommand ResetCommand { get; }

    public ICommand ClearCommand { get; }

    /// <summary>Token colors of the input (the editor theme).</summary>
    public Editor.EditorTheme Colors
    {
        get => _colors;
        set => SetField(ref _colors, value);
    }

    public string Input
    {
        get => _input;
        set => SetField(ref _input, value ?? string.Empty);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public IReadOnlyList<string> History => _history;

    /// <summary>Starts the host in the background so the first submission is quick.</summary>
    public void Warm() => _ = Guard(() => Service.StartAsync(CancellationToken.None));

    /// <summary>
    /// Runs <see cref="Input"/> when it is a complete submission (or <paramref name="force"/>). Returns false when the
    /// input needs more lines; the view then inserts a line break.
    /// </summary>
    public async Task<bool> SubmitAsync(bool force = false)
    {
        var code = Input;
        if (code.Trim().Length == 0)
        {
            return true;
        }

        if (!force)
        {
            try
            {
                if (!await Service.IsCompleteAsync(code, CancellationToken.None).ConfigureAwait(true))
                {
                    return false;
                }
            }
            catch (Exception e) when (IsHostFailure(e))
            {
                // Run it anyway; ExecuteAsync reports the problem.
            }
        }

        Input = string.Empty;
        await ExecuteAsync(code).ConfigureAwait(true);
        return true;
    }

    /// <summary>Runs <paramref name="code"/> as it is (Send to Interactive), keeping whatever is typed in the input.</summary>
    public Task ExecuteAsync(string code)
    {
        code = code.TrimEnd();
        if (code.Length == 0)
        {
            return Task.CompletedTask;
        }

        _history.Remove(code);
        _history.Add(code);
        _historyIndex = _history.Count;
        return RunAsync(code);
    }

    private async Task RunAsync(string code)
    {
        var lines = code.ReplaceLineEndings("\n").Split('\n');
        Transcript.AppendLine("> " + lines[0]);
        foreach (var line in lines.Skip(1))
        {
            Transcript.AppendLine(". " + line);
        }

        IsBusy = true;
        Status = "Running...";
        try
        {
            var result = await Service.ExecuteAsync(code, CancellationToken.None).ConfigureAwait(true);
            EnsureLineStart();
            foreach (var diagnostic in result.Diagnostics)
            {
                Transcript.AppendLine(string.Create(CultureInfo.InvariantCulture, $"({diagnostic.Line + 1},{diagnostic.Character + 1}): {diagnostic.Severity} {diagnostic.Id}: {diagnostic.Message}"));
            }

            if (result.Error is { } error)
            {
                Transcript.AppendLine(error);
            }

            if (result.Value is { } value)
            {
                Transcript.AppendLine(value);
            }

            Status = result.Success ? string.Create(CultureInfo.InvariantCulture, $"Ran in {result.ElapsedMilliseconds:0} ms") : "Failed";
        }
        catch (Exception e) when (IsHostFailure(e))
        {
            Transcript.AppendLine(e.Message);
            Status = "Failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Starts a new state, optionally with a new configuration (references, usings, working directory).</summary>
    public async Task ResetAsync(ResetParams? configuration, string? message = null)
    {
        Transcript.AppendLine(message ?? "Resetting execution engine.");
        Status = "Resetting...";
        await Guard(() => Service.ResetAsync(configuration, CancellationToken.None)).ConfigureAwait(true);
        Status = "Ready";
    }

    /// <summary>Alt+Up and Alt+Down walk the history; past the newest entry the input is empty.</summary>
    public void Navigate(int direction)
    {
        if (_history.Count == 0)
        {
            return;
        }

        _historyIndex = Math.Clamp(_historyIndex + direction, 0, _history.Count);
        Input = _historyIndex < _history.Count ? _history[_historyIndex] : string.Empty;
    }

    private void EnsureLineStart()
    {
        var text = Transcript.GetText();
        if (text.Length > 0 && text[^1] != '\n')
        {
            Transcript.Append("\n");
        }
    }

    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception e) when (IsHostFailure(e))
        {
            Transcript.AppendLine(e.Message);
        }
    }

    private static bool IsHostFailure(Exception e) =>
        e is DaedalusStudio.Rpc.JsonRpcConnectionLostException or DaedalusStudio.Rpc.JsonRpcRemoteException or InvalidOperationException
            or System.ComponentModel.Win32Exception or IOException or OperationCanceledException;
}
