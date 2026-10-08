using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Daedalus.Diagnostics.BenchEvents;

namespace Daedalus.Spike.Avalonia;

public sealed partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;

    public MainWindow() : this(new ShellViewModel(new SpikeOptions(null, null, null, null)))
    {
    }

    internal MainWindow(ShellViewModel shell)
    {
        _shell = shell;
        DataContext = shell;
        AvaloniaXamlLoader.Load(this);
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        // A requestAnimationFrame callback runs at the start of a frame; the second one proves that the
        // first frame has been produced. "interactive" additionally waits for the UI thread to go idle.
        RequestAnimationFrame(firstFrameTime => RequestAnimationFrame(secondFrameTime =>
        {
            BenchEvents.Emit(BenchEventNames.FirstFrame);
            Dispatcher.UIThread.Post(() =>
            {
                BenchEvents.Emit(BenchEventNames.Interactive);
                var bench = new BenchDriver(this, _shell);
                bench.ExitIfRequested(BenchEventNames.Interactive);
                _ = StartAsync(bench);
            }, DispatcherPriority.Background);
        }));
    }

    private async Task StartAsync(BenchDriver bench)
    {
        try
        {
            await _shell.InitializeAsync(CancellationToken.None).ConfigureAwait(true);
            await bench.RunAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _shell.Output.Append($"[error] {ex.Message}");
        }
    }
}
