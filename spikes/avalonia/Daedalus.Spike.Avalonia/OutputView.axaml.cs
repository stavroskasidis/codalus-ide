using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Daedalus.Spike.Avalonia;

public sealed partial class OutputView : UserControl
{
    public OutputView() => AvaloniaXamlLoader.Load(this);
}
