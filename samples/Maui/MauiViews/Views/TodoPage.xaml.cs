using MauiViews.ViewModels;

namespace MauiViews.Views;

public partial class TodoPage : ContentPage
{
    public TodoPage()
    {
        InitializeComponent();
        BindingContext = new TodoViewModel();
    }

    private void OnAddClicked(object? sender, EventArgs e) => NewTitleEntry.Focus();
}
