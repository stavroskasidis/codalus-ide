using Avalonia.Controls;

namespace AvaloniaApp.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        GreetingText.PointerPressed += (_, _) => IncrementButton.Focus();
    }
}
