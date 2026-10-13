using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace MauiCounter;

/// <summary>The page's state: a count, and the platform it runs on.</summary>
public sealed class CounterViewModel : INotifyPropertyChanged
{
    private int _count;

    public CounterViewModel() => IncrementCommand = new Command(Increment);

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Platform { get; } = PlatformName.Current;

    public ICommand IncrementCommand { get; }

    public string Text => _count switch
    {
        0 => "Click me",
        1 => "Clicked 1 time",
        _ => $"Clicked {_count} times",
    };

    public void Increment()
    {
        _count++; // [debug] the debugger stops here
        OnPropertyChanged(nameof(Text));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
