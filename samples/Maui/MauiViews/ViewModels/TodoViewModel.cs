using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MauiViews.Models;

namespace MauiViews.ViewModels;

public sealed class TodoViewModel : INotifyPropertyChanged
{
    private string _newTitle = string.Empty;

    public TodoViewModel()
    {
        AddCommand = new Command(Add, () => NewTitle.Length > 0);
        Items.CollectionChanged += (_, _) => Changed(nameof(RemainingCount));
        Items.Add(new TodoItem("Install the Android SDK"));
        Items.Add(new TodoItem("Start an emulator"));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<TodoItem> Items { get; } = [];

    public string NewTitle
    {
        get => _newTitle;
        set
        {
            _newTitle = value ?? string.Empty;
            Changed();
            ((Command)AddCommand).ChangeCanExecute();
        }
    }

    public int RemainingCount => Items.Count(i => !i.IsDone);

    public ICommand AddCommand { get; }

    private void Add()
    {
        Items.Add(new TodoItem(NewTitle));
        NewTitle = string.Empty;
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
