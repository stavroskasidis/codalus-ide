using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Daedalus.Spike.Avalonia;

public sealed class App : Application
{
    internal static SpikeOptions Options { get; set; } = new(null, null, null, null);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var shell = new ShellViewModel(Options);
            desktop.MainWindow = new MainWindow(shell);
            desktop.ShutdownRequested += (_, _) => shell.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
