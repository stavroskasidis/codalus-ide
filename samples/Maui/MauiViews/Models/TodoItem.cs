using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MauiViews.Models;

public sealed class TodoItem(string title) : INotifyPropertyChanged
{
    private bool _isDone;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; } = title;

    public bool IsDone
    {
        get => _isDone;
        set
        {
            if (_isDone != value)
            {
                _isDone = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDone)));
            }
        }
    }
}
