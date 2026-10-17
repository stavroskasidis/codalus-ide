using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIApp.ViewModels;

namespace WinUIApp;

/// <summary>The main page: its compiled bindings (<c>x:Bind</c>) start from this class, so the view model is a property.</summary>
public sealed partial class MainPage : Page
{
    public MainPage() => InitializeComponent();

    /// <summary>The page's view model.</summary>
    public MainViewModel ViewModel { get; } = new();

    /// <summary>A function binding's function: "2 remaining".</summary>
    public static string FormatRemaining(int count) => string.Format(CultureInfo.CurrentCulture, "{0} remaining", count);

    private void OnAddClicked(object sender, RoutedEventArgs e) => NewTitleBox.Focus(FocusState.Programmatic);
}
