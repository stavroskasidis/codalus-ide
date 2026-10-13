using Avalonia.Controls;
using DaedalusStudio.Shell.Controls;

namespace DaedalusStudio.Shell.Views;

/// <summary>Tools → Fonts and Colors (M23a): type a color and leave the cell to set it.</summary>
public sealed partial class FontsAndColorsView : UserControl, IPaneView
{
    public FontsAndColorsView() => InitializeComponent();

    public void FocusPrimary() => FilterBox.Focus();
}
