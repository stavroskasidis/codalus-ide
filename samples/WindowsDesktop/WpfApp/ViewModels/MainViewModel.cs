using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WpfApp.Models;

namespace WpfApp.ViewModels;

/// <summary>The main window's view model: a to-do list, the title being typed, and the command that adds it.</summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _newTitle = string.Empty;
    private int _rating = 3;

    public MainViewModel()
    {
        AddCommand = new RelayCommand(Add, () => NewTitle.Trim().Length > 0);
        Items.Add(new TodoItem("Open the WPF fixture"));
        Items.Add(new TodoItem("Edit its XAML") { IsDone = true });
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(RemainingCount));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The window's title.</summary>
    public string Title => "To do";

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
                AddCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>How many items aren't done.</summary>
    public int RemainingCount => Items.Count(i => !i.IsDone);

    /// <summary>The list's rating.</summary>
    public int Rating
    {
        get => _rating;
        set
        {
            _rating = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Adds <see cref="NewTitle"/> to the list.</summary>
    public RelayCommand AddCommand { get; }

    private void Add()
    {
        Items.Add(new TodoItem(NewTitle.Trim()));
        NewTitle = string.Empty;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
