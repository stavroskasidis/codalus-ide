using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// A theme variant's preview: the shell's chrome in that variant's own resources (M34h; M45's Options cards, M46's setup
/// cards), from its <see cref="ThemePicture"/>.
/// </summary>
public sealed partial class ThemePictureView : UserControl
{
    public ThemePictureView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ApplyVariant();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyVariant();
    }

    /// <summary>
    /// Gives the scope the preview's variant. A scope whose variant is the one it inherits raises no change, so what its
    /// content looked up before it was in a window (nothing: there were no resources to find) stayed unresolved, and the
    /// preview of the window's own theme drew no fills. Passing through another variant makes its content look again.
    /// </summary>
    private void ApplyVariant()
    {
        if (DataContext is not ThemePicture picture)
        {
            return;
        }

        var target = picture.Variant;
        Scope.RequestedThemeVariant = target == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
        Scope.RequestedThemeVariant = target;
    }
}
