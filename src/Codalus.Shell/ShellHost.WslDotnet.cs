using System.Security.Cryptography;
using System.Text;
using Codalus.Projects.Protocol;
using Codalus.Rpc;
using Codalus.Shell.Infrastructure;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging.Remote;
using Codalus.Workbench.Localization;
using Codalus.Workbench.Setup;

namespace Codalus.Shell;

/// <summary>
/// The .NET of the WSL launch profile (M14c): before each start in a distribution (F5 and Ctrl+F5), the <c>dotnet</c> there
/// that has the app's frameworks, else an offer to install the .NET SDK there with the same choices as on Linux, after
/// which the start runs again; and the app's static web assets, given to it with paths WSL can read.
/// </summary>
public sealed partial class ShellHost
{
    /// <summary>Tests: what stands in for <c>wsl.exe</c>.</summary>
    internal string WslProgram { get; set; } = "wsl.exe";

    private Workbench.Notifications.Notification? _wslDotnetNotification;

    private WslTarget WslTargetFor(string distribution) => new(distribution, WslProgram);

    /// <summary><see cref="RunService.PrepareWsl"/>: any thread.</summary>
    internal async Task<LaunchPlan> PrepareWslAsync(RunInfo info, LaunchPlan plan, CancellationToken cancellationToken)
    {
        var wsl = plan.Wsl!;
        var name = Path.GetFileNameWithoutExtension(info.Path);
        var target = WslTargetFor(wsl.Distribution);
        var config = ReadRuntimeConfig(info, plan);
        var probe = await ProbeWslDotnetAsync(target, cancellationToken).ConfigureAwait(false);
        var choice = WslDotnet.Choose(probe, config);
        if (choice.Dotnet is not { } dotnet)
        {
            var channel = WslDotnet.Channel(config, info.TargetFramework);
            var message = choice.Found is null
                ? Localizer.Format("msg.wsl.dotnetMissing", ".NET isn't installed in {0}, so {1} can't run there.", target.Display, name)
                : Localizer.Format("msg.wsl.runtimeMissing", "{1} needs {2}, which the .NET in {0} doesn't have (it has {3}), so it can't run there.", target.Display, name,
                    string.Join(", ", choice.Missing.Select(m => $"{m.Reference.Name} {m.Reference.Version}")),
                    choice.Missing[0].Installed.Count == 0 ? Localizer.Get("msg.wsl.none", "none") : FrameworkResolver.Describe(choice.Missing[0].Installed));
            OfferWslDotnet(target, message, info, plan, channel, Volatile.Read(ref _launch)?.Debug ?? false);
            throw new RunNotStartedException(message);
        }

        var environment = new Dictionary<string, string>(plan.Environment, StringComparer.Ordinal);
        if (!environment.ContainsKey(WslDotnet.StaticWebAssetsVariable) && WslStaticWebAssets(info) is { } manifest)
        {
            environment[WslDotnet.StaticWebAssetsVariable] = manifest;
        }

        return WslLaunch.Apply(plan with { Wsl = wsl with { Program = dotnet.Path }, Environment = environment });
    }

    /// <summary>The app's <c>runtimeconfig.json</c>, read with the run's own roll-forward variables; null without one.</summary>
    private static RuntimeConfig? ReadRuntimeConfig(RunInfo info, LaunchPlan plan) =>
        WslDotnet.RuntimeConfigPath(info) is { } path && File.Exists(path)
            ? RuntimeConfig.Read(path, variable => plan.Environment.TryGetValue(variable, out var value) ? value : null)
            : null;

    private static async Task<WslDotnetProbe> ProbeWslDotnetAsync(WslTarget target, CancellationToken cancellationToken)
    {
        RemoteResult result;
        try
        {
            result = await RemoteShell.RunAsync(target, WslDotnet.ProbeCommand, cancellationToken).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"WSL isn't available: {ex.Message}", ex);
        }

        var probe = WslDotnet.Parse(result.Output);
        if (probe.Home.Length == 0)
        {
            // The distribution isn't there, or wsl.exe refused: its own message says why.
            var why = (result.Error.Length > 0 ? result.Error : result.Output).Trim();
            throw new InvalidOperationException($"{target.Display} didn't answer (exit code {result.ExitCode}){(why.Length > 0 ? ": " + why : ".")}");
        }

        return probe;
    }

    /// <summary>
    /// The build's static web assets manifest with its content roots under <c>/mnt</c>, written to the IDE's cache, as WSL
    /// sees it; null when the app has none. In the distribution, ASP.NET Core can't use the Windows folders the build
    /// wrote (the app stops with "The path must be absolute").
    /// </summary>
    private string? WslStaticWebAssets(RunInfo info)
    {
        if (WslDotnet.StaticWebAssetsManifest(info) is not { } source || !File.Exists(source))
        {
            return null;
        }

        try
        {
            if (WslDotnet.TranslateStaticWebAssets(File.ReadAllText(source), path => WslLaunch.ToLinuxPath(path)) is not { } translated)
            {
                return null;
            }

            var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(source).ToUpperInvariant())))[..12];
            var copy = Path.Combine(Paths.Cache, "wsl", "static-web-assets", $"{Path.GetFileNameWithoutExtension(info.Path)}-{key}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.WriteAllText(copy, translated);
            return WslLaunch.ToLinuxPath(copy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Workbench.Log.AppendLine($"[wsl] The static web assets of {info.Path} stay as the build wrote them: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Says why the program can't run in the distribution, with Install .NET SDK: one notification while it shows (a
    /// second start replaces it).
    /// </summary>
    private void OfferWslDotnet(WslTarget target, string message, RunInfo info, LaunchPlan plan, string channel, bool debug)
    {
        var install = Localizer.Format("msg.wsl.install", "Install .NET SDK {0}…", channel);
        Workbench.Notifications.Notification notification;
        Workbench.Notifications.Notification? stale;
        lock (_offerGate)
        {
            stale = _wslDotnetNotification is { Result.IsCompleted: false } showing ? showing : null;
            notification = _wslDotnetNotification = Workbench.Notifications.Warning(message, install);
        }

        if (stale is not null)
        {
            Workbench.Notifications.Dismiss(stale);
        }

        _ = notification.Result.ContinueWith(
            t =>
            {
                if (t.Result == install)
                {
                    Ui.Post(() => _ = InstallDotnetInWslAsync(target, info, plan, channel, () => StartAgainAfterWslInstallAsync(info.Path, debug)));
                }
            },
            TaskScheduler.Default);
    }

    /// <summary>
    /// Installs the .NET SDK of <paramref name="channel"/> in the distribution, the way the user picks (the options of
    /// Linux: the distribution's package in the terminal, dotnet-install into <c>~/.dotnet</c>, the download page), then
    /// looks again: once a <c>dotnet</c> there has the app's frameworks, <paramref name="then"/> (the start again).
    /// The IDE's own .NET doesn't change.
    /// </summary>
    /// <param name="choose">Tests: picks the way.</param>
    /// <param name="run">Tests: runs an install the IDE runs itself.</param>
    internal async Task InstallDotnetInWslAsync(WslTarget target, RunInfo info, LaunchPlan plan, string channel, Func<Task> then, Func<IReadOnlyList<InstallPlan>, Task<InstallPlan?>>? choose = null, ProcessRunner? run = null)
    {
        var name = Path.GetFileNameWithoutExtension(info.Path);
        try
        {
            var probe = await ProbeWslDotnetAsync(target, CancellationToken.None).ConfigureAwait(true);
            string? candidate = null;
            if (WslDotnet.PackageQueryCommand(probe, channel) is { } query)
            {
                var answer = await RemoteShell.RunAsync(target, query, CancellationToken.None).ConfigureAwait(true);
                candidate = answer.Succeeded ? InstallPlans.PackageCandidate(answer.Output) : null;
            }

            var tool = new ToolStatus("dotnet", Localizer.Format("msg.wsl.sdkTitle", ".NET SDK {0} in {1}", channel, target.Display), false, ToolState.Missing, $"Running {name} in WSL")
            {
                WantedChannel = channel,
            };
            var plans = WslDotnet.InstallOptions(tool, WslDotnet.Facts(probe, candidate), target.Distribution, target.Display, WslProgram);
            if (await PickAndRunInstallAsync(tool, plans, confirm: null, run, choose).ConfigureAwait(true) is not { } ran)
            {
                return;
            }

            if (ran.Code != 0)
            {
                Workbench.Notifications.Error(Localizer.Format("msg.setup.installFailed", "Installing {0} failed (exit code {1}). The Environment Setup output has the details.", tool.Title, ran.Code));
                return;
            }

            var after = WslDotnet.Choose(await ProbeWslDotnetAsync(target, CancellationToken.None).ConfigureAwait(true), ReadRuntimeConfig(info, plan));
            if (after.Dotnet is not { } dotnet)
            {
                Workbench.Notifications.Warning(Localizer.Format("msg.wsl.stillMissing", "The install finished, but {0} still has no .NET that runs {1}. The Environment Setup output has the details.", target.Display, name));
                return;
            }

            SetupLog.AppendLine($"[{DateTime.Now:T}] {name} runs on {dotnet.Path} in {target.Display}.");
            await then().ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            Workbench.Notifications.Error(Localizer.Format("msg.wsl.installFailed", "Could not install .NET in {0}: {1}", target.Display, ex.Message));
        }
    }

    /// <summary>
    /// The start that waited for .NET in WSL, again: the project alone, debugged or not as it was. A debug session that
    /// is running now (other startup projects) can't take it: the user starts it.
    /// </summary>
    private async Task StartAgainAfterWslInstallAsync(string project, bool debug)
    {
        var name = Path.GetFileNameWithoutExtension(project);
        if (IsLaunching || (debug && Debugger.IsActive))
        {
            Workbench.Notifications.Info(Localizer.Format("msg.wsl.installedStart", ".NET is installed in WSL. Start {0} again to run it there.", name));
            return;
        }

        StatusBar.ShowMessage(Localizer.Format("msg.wsl.installedStarting", ".NET is installed in WSL: starting {0}…", name), TimeSpan.FromSeconds(6));
        await (debug ? StartDebuggingAsync(project) : StartWithoutDebuggingAsync(project)).ConfigureAwait(true);
    }
}
