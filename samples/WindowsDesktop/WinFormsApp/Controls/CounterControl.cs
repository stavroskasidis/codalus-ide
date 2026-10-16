namespace WinFormsApp.Controls;

/// <summary>A user control: a count and a button that adds one.</summary>
public partial class CounterControl : UserControl
{
    private readonly Counter _counter = new();

    public CounterControl()
    {
        InitializeComponent();
        Show();
    }

    public void Reset()
    {
        _counter.Reset();
        Show();
    }

    private void OnIncrementClicked(object? sender, EventArgs e)
    {
        _counter.Increment();
        Show();
    }

    private new void Show() => countLabel.Text = _counter.Value.ToString(System.Globalization.CultureInfo.CurrentCulture);
}
