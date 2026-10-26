using Codalus.Shell.Infrastructure;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Feedback;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Setup;

namespace Codalus.Shell;

/// <summary>
/// Help → Report a Problem and Suggest a Feature (M51): the dialog that shows what a new GitHub issue would carry and
/// opens it, prefilled, in the browser (<see cref="IssueReport"/>). The IDE sends nothing itself.
/// </summary>
public sealed partial class ShellHost
{
    private FeedbackViewModel? _feedback;

    /// <summary>The feedback dialog (one, opened per kind).</summary>
    public FeedbackViewModel Feedback => _feedback ??= new FeedbackViewModel(new ShellFeedbackHost(this));

    /// <summary>Finds the .NET SDK's version for a report (tests replace it): <c>dotnet --version</c> in the solution's folder, within 5 s.</summary>
    internal Func<CancellationToken, Task<string?>>? FindSdkVersion { get; set; }

    /// <summary>The folder logs too long for a link are saved in: <c>feedback</c> under the logs folder.</summary>
    public string FeedbackFolder => Path.Combine(Paths.Logs, "feedback");

    public Task ReportProblemAsync() => Feedback.OpenAsync(IssueKind.Problem);

    public Task SuggestFeatureAsync() => Feedback.OpenAsync(IssueKind.Feature);

    private async Task<string?> FindSdkVersionAsync(CancellationToken cancellationToken)
    {
        if (EnvironmentCheck.FindDotnet() is not { } dotnet)
        {
            return null;
        }

        var (code, output) = await EnvironmentCheck.RunProcessAsync(dotnet, ["--version"], Solutions.Solution?.Directory, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        var version = output.Trim();
        return code == 0 && version.Length is > 0 and < 64 && !version.Contains('\n', StringComparison.Ordinal) ? version : null;
    }

    private sealed class ShellFeedbackHost(ShellHost shell) : IFeedbackHost
    {
        public IssueEnvironment Environment => IssueEnvironment.Current(ShellInfo.Version);

        public string LogText => shell.Workbench.Log.GetText();

        public string? Home => System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

        public Task<string?> FindSdkAsync(CancellationToken cancellationToken) => (shell.FindSdkVersion ?? shell.FindSdkVersionAsync)(cancellationToken);

        public string? SaveLogs(IssueDraft draft, string fileName)
        {
            try
            {
                return IssueReport.SaveLogs(shell.FeedbackFolder, draft, fileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                shell.Workbench.Notifications.Error(Localizer.Format("msg.feedback.couldNotSave", "Could not save the log to attach: {0}. Copy puts the whole report on the clipboard instead.", ex.Message));
                return null;
            }
        }

        public bool OpenUrl(Uri url)
        {
            try
            {
                shell.OpenExternalUrl(url);
                return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
            {
                shell.Workbench.Notifications.Error(Localizer.Format("msg.feedback.couldNotOpen", "Could not open the browser: {0}. Copy puts the whole report on the clipboard, to paste into a new issue.", ex.Message));
                return false;
            }
        }

        public void Reveal(string path)
        {
            try
            {
                shell.RevealPath(path);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                shell.Workbench.Notifications.Error(Localizer.Format("msg.shellHost.couldNotOpen", "Could not open {0}: {1}", path, ex.Message));
            }
        }

        public async Task CopyAsync(string text)
        {
            if (shell.Window?.Clipboard is { } clipboard)
            {
                await SafeClipboard.TrySetTextAsync(clipboard, text).ConfigureAwait(true);
                shell.ShowMessage(Localizer.Get("msg.feedback.copied", "Copied the report."));
            }
        }
    }
}
