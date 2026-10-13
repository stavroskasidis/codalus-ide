using Codalus.Shell.Controls;
using Avalonia.Controls;

namespace Codalus.Shell.Views;

/// <summary>Create Pull Request (ADR 0031): title, description, the branch to merge into, draft.</summary>
public sealed partial class CreatePullRequestView : UserControl, IPaneView
{
    public CreatePullRequestView()
    {
        InitializeComponent();
    }

    public void FocusPrimary() => TitleBox.Focus();
}
