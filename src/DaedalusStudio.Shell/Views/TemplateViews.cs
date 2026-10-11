using Avalonia.Controls;
using DaedalusStudio.Shell.Controls;

namespace DaedalusStudio.Shell.Views;

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
