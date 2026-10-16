using System.Windows;
using WpfApp.ViewModels;

namespace WpfApp;

/// <summary>The main window: its view model is the data context; the named text box gets the focus back after an add.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    private void OnAddClicked(object sender, RoutedEventArgs e) => NewTitleBox.Focus();
}
