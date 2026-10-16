namespace Codalus.Shell.Infrastructure;

/// <summary>
/// Whether AI assistance is on (M28b), for views that show an AI button on another feature's surface: bound as
/// <c>{Binding IsEnabled, Source={x:Static l:AiUi.Current}}</c>, so no AI UI shows while it is off (ADR 0062).
/// </summary>
public sealed class AiUi : ObservableObject
{
    private bool _isEnabled;

    public static AiUi Current { get; } = new();

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetField(ref _isEnabled, value);
    }
}
