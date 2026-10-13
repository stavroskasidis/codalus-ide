namespace Codalus.Shell.Views;

/// <summary>Add New Item: the search box has the focus first; Enter adds.</summary>
public sealed partial class AddNewItemView : LoadingView
{
    public AddNewItemView() => InitializeComponent();

    public override void FocusPrimary() => SearchBox.Focus();
}
