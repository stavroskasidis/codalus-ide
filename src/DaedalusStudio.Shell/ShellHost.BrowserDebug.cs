using DaedalusStudio.Debugging;
using DaedalusStudio.Shell.Infrastructure;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Debugging;
using DaedalusStudio.Workbench.Node;

namespace DaedalusStudio.Shell;

/// <summary>
/// Browser debugging (ADR 0026): F5 on a Blazor WebAssembly app starts its dev server, then debugs the page in a
/// Chromium browser through vscode-js-debug and the dev server's debugging proxy. The session and the server end
/// together.
/// </summary>
public sealed partial class ShellHost
{
    private static readonly TimeSpan s_devServerTimeout = TimeSpan.FromMinutes(2);

    private RunningProgram? _browserDebugServer;

    /// <summary>vscode-js-debug on the on-demand Node.js runtime.</summary>
    public JsDebug JsDebug { get; private set; } = null!;

    private void InitializeBrowserDebugging(string cache)
    {
        JsDebug = new JsDebug(Node, cache);
        Debugger.Ended += (_, _) =>
        {
            var server = Interlocked.Exchange(ref _browserDebugServer, null);
            server?.Stop();
        };
    }

    private async Task StartBrowserDebuggingAsync(string project, (string Url, string InspectUri) target)
    {
        var browser = Browsers.Find(Workbench.Settings.GetString(Browsers.SettingKey, "auto"));
        if (browser is null)
        {
            Workbench.Notifications.Error("Debugging a Blazor WebAssembly app needs Chrome, Edge or Chromium. Install one, or set 'debug.browser' to a Chromium browser's path.");
            return;
        }

        if (NodeRuntime.Platform is null)
        {
            Workbench.Notifications.Error("Debugging in the browser needs Node.js, which isn't available for this platform.");
            return;
        }

        DebugLog.AppendLine($"Debugging in {browser.Display} ({browser.Path}) through vscode-js-debug {JsDebug.Version}.");
        await JsDebug.EnsureAsync(new Progress<string>(line => DebugLog.AppendLine(line)), CancellationToken.None).ConfigureAwait(true);

        var server = await Runs.StartAsync(project, externalConsole: false, CancellationToken.None, openBrowser: false).ConfigureAwait(true);
        try
        {
            await server.Listening.WaitAsync(s_devServerTimeout).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            server.Stop();
            throw new InvalidOperationException($"The dev server didn't start listening (see Output → Run: {server.Name}).", ex);
        }

        _browserDebugServer = server;
        var arguments = new BrowserLaunchArguments(
            browser.LaunchType,
            target.Url,
            Path.GetDirectoryName(project)!,
            target.InspectUri,
            browser.Path,
            Browsers.Headless ? ["--headless=new"] : null);
        DebugLog.AppendLine($"> {browser.Display} {target.Url}");
        await Debugger.LaunchBrowserAsync(
            ct => JsDebugAdapter.StartAsync(JsDebug.NodePath, JsDebug.ServerScript, browser.LaunchType, ct),
            arguments,
            CancellationToken.None).ConfigureAwait(true);

        // The dev server stopped on its own: the page has nothing left to debug.
        _ = server.Exited.ContinueWith(_ => Ui.Post(() =>
        {
            if (_browserDebugServer == server)
            {
                _ = Debugger.StopAsync();
            }
        }), TaskScheduler.Default);
    }
}
