namespace AvaloniaApp.ViewModels;

public enum Priority
{
    Low,
    Normal,
    High,
}

/// <summary>One task in the list.</summary>
public sealed class TodoItemViewModel : ViewModelBase
{
    private string _title = string.Empty;
    private bool _isDone;
    private Priority _priority = Priority.Normal;

    /// <summary>What to do.</summary>
    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public bool IsDone
    {
        get => _isDone;
        set => Set(ref _isDone, value);
    }

    public Priority Priority
    {
        get => _priority;
        set => Set(ref _priority, value);
    }
}
