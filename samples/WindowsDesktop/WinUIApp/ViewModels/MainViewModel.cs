using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using WinUIApp.Models;

namespace WinUIApp.ViewModels;

/// <summary>The main page's view model: a to-do list, the title being typed, and the command that adds it.</summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _newTitle = string.Empty;
    private double _rating = 3;

    public MainViewModel()
    {
        AddCommand = new RelayCommand(Add);
        Items.Add(new TodoItem("Open the WinUI fixture"));
        Items.Add(new TodoItem("Edit its XAML") { IsDone = true });
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(RemainingCount));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The to-do items.</summary>
    public ObservableCollection<TodoItem> Items { get; } = [];

    /// <summary>The title of the next item.</summary>
    public string NewTitle
    {
        get => _newTitle;
        set
        {
            if (_newTitle != value)
            {
                _newTitle = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsTyping));
            }
        }
    }

    /// <summary>Whether a title is being typed.</summary>
    public bool IsTyping => NewTitle.Length > 0;

    /// <summary>How many items aren't done.</summary>
    public int RemainingCount => Items.Count(i => !i.IsDone);

    /// <summary>The list's rating.</summary>
    public double Rating
    {
        get => _rating;
        set
        {
            _rating = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Adds <see cref="NewTitle"/> to the list.</summary>
    public ICommand AddCommand { get; }

    private void Add()
    {
        if (NewTitle.Trim().Length > 0)
        {
            Items.Add(new TodoItem(NewTitle.Trim()));
            NewTitle = string.Empty;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>A command that runs an action.</summary>
    private sealed class RelayCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }
}
