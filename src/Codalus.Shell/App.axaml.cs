using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Codalus.Shell.Views;

namespace Codalus.Shell;

public sealed class App : Application
{
    internal static ShellOptions Options { get; set; } = new();

    public ShellHost? Shell { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = Shell = new ShellHost(this, Options);
            desktop.MainWindow = new MainWindow(shell);
            desktop.Exit += (_, _) => shell.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
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
