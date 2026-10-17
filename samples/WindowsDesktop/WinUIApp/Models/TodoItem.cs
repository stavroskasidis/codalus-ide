using System.ComponentModel;

namespace WinUIApp.Models;

/// <summary>A to-do item.</summary>
public sealed class TodoItem(string title) : INotifyPropertyChanged
{
    private bool _isDone;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>What there is to do.</summary>
    public string Title { get; } = title;

    /// <summary>Whether it's done.</summary>
    public bool IsDone
    {
        get => _isDone;
        set
        {
            _isDone = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDone)));
        }
    }
}
