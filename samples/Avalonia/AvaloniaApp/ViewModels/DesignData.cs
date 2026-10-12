namespace AvaloniaApp.ViewModels;

/// <summary>Design-time data for the previewer (<c>d:DataContext</c>).</summary>
public static class DesignData
{
    public static MainWindowViewModel Main { get; } = new() { Greeting = "Hello from design-time data" };
}
