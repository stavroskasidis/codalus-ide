using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Codalus.Shell.Views;

namespace Codalus.Shell;

public sealed class App : Application
{
    internal static ShellOptions Options { get; set; } = new();

    /// <summary>
    /// The login shell's PATH and network settings being taken (Program.Main). The window doesn't wait for it (M56): the
    /// startup's work and the processes started meanwhile do (<see cref="ShellHost.EnvironmentReady"/>).
    /// </summary>
    internal static Task<bool> LoginShellImport { get; set; } = Task.FromResult(false);

    public ShellHost? Shell { get; private set; }

    public override void RegisterServices()
    {
        base.RegisterServices();
        Infrastructure.ShellSystemFonts.Install();
    }

    public override void Initialize()
    {
        // Dock's deferred content fades a tab's view in from Opacity 0.85 when a document or tool tab is switched, and its
        // transition races the value it posts at the end: every other switch left the whole document view at 0.85, so the
        // editor's and the compare view's colors read 15% dimmer (#83). The classic IDE has no fade either; zero turns it off.
        Dock.Controls.DeferredContentControl.DeferredContentPresentationSettings.RevealDuration = TimeSpan.Zero;
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = Shell = new ShellHost(this, Options);
            desktop.MainWindow = new MainWindow(shell);
            // The main window shuts the shell down behind it when the user closes it (M56); a forced shutdown (the smoke
            // test's, the benchmarks'), or the OS's, still waits here, as long as the shutdown may take.
            desktop.Exit += (_, _) => shell.ShutDownAsync(ShellHost.ShutdownLimit).Wait(ShellHost.ShutdownLimit + TimeSpan.FromSeconds(2));
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>A bug in a view must not take the IDE down: log it, tell the user, keep running.</summary>
    private void OnUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (Infrastructure.SafeClipboard.IsBusy(e.Exception))
        {
            // Another program holds the clipboard: the editor's own Copy and Paste can't be wrapped, so say it as ours do.
            Infrastructure.SafeClipboard.ReportBusy();
            e.Handled = true;
            return;
        }

        if (Shell is { } shell)
        {
            shell.Workbench.Log.AppendLine($"[unhandled] {e.Exception}");
            Infrastructure.CrashReporter.Write("error", e.Exception.ToString());
            shell.ReportError($"An unexpected error occurred: {e.Exception.Message}");
            e.Handled = true;
        }
    }

    /// <summary>The editor's Copy and Cut set the clipboard without awaiting it: a busy clipboard surfaces here.</summary>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        if (Infrastructure.SafeClipboard.IsBusy(e.Exception))
        {
            e.SetObserved();
            Infrastructure.SafeClipboard.ReportBusy();
        }
    }
}
