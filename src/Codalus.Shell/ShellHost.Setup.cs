using System.Diagnostics;
using Codalus.Platform;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Setup;

namespace Codalus.Shell;

/// <summary>
/// Environment setup (M37): what's installed of the tools Codalus IDE uses, and installing what's missing with the
/// user's consent. When the IDE starts, a missing .NET SDK or git is offered; when a solution opens, a <c>global.json</c>
/// SDK that isn't installed is. Help → Check Environment lists everything with a fix. Every install shows its exact
/// command first; what needs a password runs in the integrated terminal, where the installer asks itself.
/// </summary>
public sealed partial class ShellHost
{
    private EnvironmentCheck? _environmentCheck;

    public EnvironmentCheck EnvironmentCheck => _environmentCheck ??= new EnvironmentCheck(Workbench.Platform, Environment.GetEnvironmentVariable);

    /// <summary>The last check's results.</summary>
    public IReadOnlyList<ToolStatus> Tools { get; private set; } = [];

    public event EventHandler? ToolsChanged;

    private Workbench.Output.OutputChannel SetupLog => Workbench.Output.GetOrCreate("codalus.setup", Localizer.Get("msg.setup.channel", "Environment Setup"));

    /// <summary>
    /// A .NET SDK installed only in the user's <c>~/.dotnet</c> (dotnet-install, M37's own installs) isn't on the system's
    /// paths: the IDE's processes get it as <c>DOTNET_ROOT</c> and on <c>PATH</c>.
    /// </summary>
    private void UseUserDotnet()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } || EnvironmentCheck.FindOnPath("dotnet") is not null)
        {
            return;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folder = Path.Combine(home, ".dotnet");
        if (File.Exists(Path.Combine(folder, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")))
        {
            AddDotnetToEnvironment(folder);
        }
    }

    private void AddDotnetToEnvironment(string folder)
    {
        Environment.SetEnvironmentVariable("DOTNET_ROOT", folder);
        Environment.SetEnvironmentVariable("PATH", folder + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        Workbench.Log.AppendLine($"[setup] Using the .NET SDK in {folder} (DOTNET_ROOT).");
    }

    /// <summary>Checks the environment; <paramref name="offer"/> offers to install what the IDE needs and is missing.</summary>
    public async Task<IReadOnlyList<ToolStatus>> CheckEnvironmentAsync(bool offer)
    {
        try
        {
            Tools = await EnvironmentCheck.CheckAsync(Solutions.Solution?.Directory, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Workbench.Log.AppendLine($"[setup] The environment check failed: {ex.Message}");
            return Tools;
        }

        ToolsChanged?.Invoke(this, EventArgs.Empty);
        foreach (var tool in Tools)
        {
            Workbench.Log.AppendLine($"[setup] {tool.Title}: {tool.State}{(tool.Version is { } v ? " " + v : string.Empty)}{(tool.Path is { } p ? " (" + p + ")" : string.Empty)}");
        }

        if (offer)
        {
            foreach (var tool in Tools.Where(t => t is { Required: true, NeedsAttention: true }))
            {
                _ = OfferInstallAsync(tool);
            }
        }

        return Tools;
    }

    private async Task OfferInstallAsync(ToolStatus tool)
    {
        var message = tool switch
        {
            { Id: "dotnet", State: ToolState.Mismatch } => Localizer.Format("msg.setup.sdkMismatch", "{0} Codalus IDE uses another SDK for now.", tool.Detail),
            { Id: "dotnet" } => Localizer.Get("msg.setup.sdkMissing", "The .NET SDK isn't installed. Codalus IDE needs it to open, build and debug .NET projects."),
            { Id: "git" } => Localizer.Get("msg.setup.gitMissing", "Git isn't installed. Source control and cloning need it."),
            _ => Localizer.Format("msg.setup.toolMissing", "{0} isn't installed.", tool.Title),
        };
        var install = Localizer.Get("msg.setup.install", "Install…");
        var details = Localizer.Get("msg.setup.details", "Check Environment");
        var choice = await Workbench.Notifications.Warning(message, install, details).Result.ConfigureAwait(true);
        if (choice == install)
        {
            await InstallToolAsync(tool).ConfigureAwait(true);
        }
        else if (choice == details)
        {
            OpenCheckEnvironment();
        }
    }

    /// <summary>The install plan for a tool on this machine, or null.</summary>
    public InstallPlan? PlanFor(ToolStatus tool) => InstallPlans.For(
        tool,
        Workbench.Platform,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        hasWinget: Workbench.Platform == PlatformKind.Windows && EnvironmentCheck.FindOnPath("winget") is not null,
        linuxDistribution: Workbench.Platform == PlatformKind.Linux ? InstallPlans.LinuxDistribution() : null);

    /// <summary>
    /// Installs a tool after the user agrees to the exact command (<paramref name="confirm"/>: tests answer for the user),
    /// then checks again.
    /// </summary>
    public async Task InstallToolAsync(ToolStatus tool, Func<InstallPlan, Task<bool>>? confirm = null, ProcessRunner? run = null)
    {
        if (PlanFor(tool) is not { } plan)
        {
            Workbench.Notifications.Info(Localizer.Format("msg.setup.noPlan", "There's no way to install {0} from here on this system.", tool.Title));
            return;
        }

        var agreed = confirm is not null
            ? await confirm(plan).ConfigureAwait(true)
            : await Dialogs.ConfirmAsync(
                Localizer.Format("msg.setup.installTitle", "Install {0}", tool.Title),
                Localizer.Format("msg.setup.installMessage", "{0}.\n\nThis runs:\n{1}", plan.Summary, plan.Command),
                plan.Kind switch
                {
                    InstallKind.OpenPage => Localizer.Get("msg.setup.openPage", "Open Page"),
                    InstallKind.Terminal => Localizer.Get("msg.setup.runInTerminal", "Run in Terminal"),
                    _ => Localizer.Get("msg.setup.installNow", "Install"),
                }).ConfigureAwait(true);
        if (!agreed)
        {
            return;
        }

        SetupLog.AppendLine($"[{DateTime.Now:T}] {plan.Summary}: {plan.Command}");
        switch (plan.Kind)
        {
            case InstallKind.OpenPage:
                ShellIntegration.OpenUrl(new Uri(plan.Command));
                return;
            case InstallKind.Terminal:
                RunInstallInTerminal(plan);
                Workbench.Notifications.Info(Localizer.Get("msg.setup.terminalStarted", "The install runs in the terminal. When it's done, check the environment again (Help → Check Environment)."));
                return;
        }

        StatusBar.ShowMessage(Localizer.Format("msg.setup.installing", "Installing {0}…", tool.Title), TimeSpan.FromSeconds(10));
        Workbench.Output.Show(SetupLog);
        var (code, output) = await (run ?? StreamInstallAsync)(plan.Program!, plan.Arguments ?? [], null, CancellationToken.None).ConfigureAwait(true);
        if (run is not null)
        {
            SetupLog.Append(output);
        }

        if (code != 0)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.setup.installFailed", "Installing {0} failed (exit code {1}). The Environment Setup output has the details.", tool.Title, code));
            return;
        }

        if (plan.AddsToPath is { } folder)
        {
            AddDotnetToEnvironment(folder);
        }

        var after = await CheckEnvironmentAsync(offer: false).ConfigureAwait(true);
        if (after.FirstOrDefault(t => t.Id == tool.Id) is { NeedsAttention: false } installed)
        {
            // Offered, not awaited: the install is done whether or not the user restarts now.
            var restart = Localizer.Get("msg.setup.restart", "Restart Now");
            var offer = Workbench.Notifications.Info(Localizer.Format("msg.setup.installed", "{0} {1} is installed. Restart Codalus IDE so that all of it uses it.", installed.Title, installed.Version), restart);
            _ = offer.Result.ContinueWith(t => { if (t.Result == restart) { Infrastructure.Ui.Post(Restart); } }, TaskScheduler.Default);
        }
        else
        {
            Workbench.Notifications.Warning(Localizer.Format("msg.setup.stillMissing", "The install finished, but {0} still isn't found. The Environment Setup output has the details.", tool.Title));
        }
    }

    private void RunInstallInTerminal(InstallPlan plan)
    {
        var (program, arguments) = Workbench.Platform == PlatformKind.Windows
            ? ("cmd.exe", (IReadOnlyList<string>)["/c", plan.Command + " & echo. & pause"])
            : ("bash", ["-c", plan.Command + "; echo; read -r -p 'Done. Press Enter to close. '"]);
        var session = Terminals.Run(plan.Summary, program, arguments, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), null);
        Layout.ShowToolWindow(TerminalViewModel.ToolWindowId);
        TerminalWindow.Show(session);
    }

    /// <summary>Runs an install, its output going to the Environment Setup channel as it comes.</summary>
    private async Task<(int, string)> StreamInstallAsync(string program, IReadOnlyList<string> arguments, string? directory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, e) => { if (e.Data is { } line) { SetupLog.AppendLine(line); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is { } line) { SetupLog.AppendLine(line); } };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(true);
            return (process.ExitCode, string.Empty);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetupLog.AppendLine(ex.Message);
            return (-1, ex.Message);
        }
    }

    /// <summary>When a solution opens: its <c>global.json</c> SDK, offered when it isn't installed.</summary>
    private async Task CheckSolutionSdkAsync(string solutionPath)
    {
        if (Path.GetDirectoryName(solutionPath) is not { } folder || EnvironmentCheck.GlobalJson(folder) is null)
        {
            return;
        }

        var tools = await EnvironmentCheck.CheckAsync(folder, CancellationToken.None).ConfigureAwait(true);
        if (tools.FirstOrDefault(t => t is { Id: "dotnet", State: ToolState.Mismatch }) is { } sdk)
        {
            Tools = tools;
            ToolsChanged?.Invoke(this, EventArgs.Empty);
            await OfferInstallAsync(sdk).ConfigureAwait(true);
        }
    }

    public void OpenCheckEnvironment()
    {
        Layout.OpenDocument(Layout.FindDocument<EnvironmentDocument>(EnvironmentDocument.DocumentId) ?? new EnvironmentDocument(new EnvironmentViewModel(this)));
    }
}
