using Avalonia.Controls;
using Codalus.Shell.Controls;

namespace Codalus.Shell.Views;

public sealed partial class TemplateManagerView : LoadingView
{
    public TemplateManagerView() => InitializeComponent();

    public override void FocusPrimary() => PackageBox.Focus();
}

public sealed partial class ExportTemplateView : UserControl, IPaneView
{
    public ExportTemplateView() => InitializeComponent();

    public void FocusPrimary() => KindPicker.Focus();
}
