using Avalonia.Controls;
using Codalus.Shell.Controls;

namespace Codalus.Shell.Views;

/// <summary>Reverse engineering (M27a): <c>dotnet ef dbcontext scaffold</c>'s options as a form.</summary>
public sealed partial class EfScaffoldView : UserControl, IPaneView
{
    public EfScaffoldView() => InitializeComponent();

    public void FocusPrimary() => ProjectBox.Focus();
}
