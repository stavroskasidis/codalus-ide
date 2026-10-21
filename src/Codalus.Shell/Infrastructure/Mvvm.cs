using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;

namespace Codalus.Shell.Infrastructure;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Workbench services raise events on any thread; views marshal through these helpers.</summary>
public static class Ui
{
    private static int s_watchers;
    private static string? s_runningJob;

    /// <summary>
    /// The job posted through <see cref="Post"/> that the UI thread is running, by the file and line that posted it, while
    /// a stall monitor watches (the smoke test's command sweep): what a stall it measures was spent on. Null between jobs.
    /// </summary>
    internal static string? RunningJob => Volatile.Read(ref s_runningJob);

    public static void Post(Action action, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else if (Volatile.Read(ref s_watchers) > 0)
        {
            Dispatcher.UIThread.Post(() => RunWatched(action, file, line));
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    /// <summary>Has posted jobs say what they are in <see cref="RunningJob"/> from now on, until <see cref="Unwatch"/>.</summary>
    internal static void Watch() => Interlocked.Increment(ref s_watchers);

    internal static void Unwatch() => Interlocked.Decrement(ref s_watchers);

    public static Task RunAsync(Action action) =>
        Dispatcher.UIThread.CheckAccess() ? RunInline(action) : Dispatcher.UIThread.InvokeAsync(action).GetTask();

    public static Task RunAsync(Func<Task> action) =>
        Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.InvokeAsync(action);

    public static Task<T> RunAsync<T>(Func<T> func) =>
        Dispatcher.UIThread.CheckAccess() ? Task.FromResult(func()) : Dispatcher.UIThread.InvokeAsync(func).GetTask();

    private static Task RunInline(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private static void RunWatched(Action action, string file, int line)
    {
        var outer = Interlocked.Exchange(ref s_runningJob, $"{Path.GetFileName(file)}:{line}");
        try
        {
            action();
        }
        finally
        {
            Volatile.Write(ref s_runningJob, outer);
        }
    }
}
