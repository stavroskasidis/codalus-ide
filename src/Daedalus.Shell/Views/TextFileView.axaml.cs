using Avalonia.Controls;
using Daedalus.Shell.Controls;

namespace Daedalus.Shell.Views;

public sealed partial class TextFileView : UserControl, IPaneView
{
    public TextFileView() => InitializeComponent();

    public void FocusPrimary() => Editor.TextArea.Focus();
}
