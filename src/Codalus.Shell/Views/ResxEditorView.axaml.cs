using Avalonia.Controls;
using Codalus.Shell.Controls;

namespace Codalus.Shell.Views;

/// <summary>The <c>.resx</c> resource editor: see <see cref="ResxEditorDocument"/>.</summary>
public sealed partial class ResxEditorView : UserControl, IPaneView
{
    public ResxEditorView() => InitializeComponent();

    public void FocusPrimary() => List.Focus();
}
