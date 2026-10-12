using System.Collections.ObjectModel;

namespace AvaloniaApp.ViewModels;

/// <summary>The main window: a greeting, a counter and a to-do list.</summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private string _greeting = "Welcome to Avalonia!";
    private int _count;
    private TodoItemViewModel? _selectedItem;

    public MainWindowViewModel()
    {
        Items.Add(new TodoItemViewModel { Title = "Write the XAML", Priority = Priority.High });
        Items.Add(new TodoItemViewModel { Title = "Preview it", IsDone = true });
        Items.Add(new TodoItemViewModel { Title = "Ship it", Priority = Priority.Low });
    }

    /// <summary>The text at the top of the window.</summary>
    public string Greeting
    {
        get => _greeting;
        set => Set(ref _greeting, value);
    }

    public int Count
    {
        get => _count;
        private set => Set(ref _count, value);
    }

    public ObservableCollection<TodoItemViewModel> Items { get; } = [];

    public TodoItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set => Set(ref _selectedItem, value);
    }

    public void Increment() => Count++;
}
