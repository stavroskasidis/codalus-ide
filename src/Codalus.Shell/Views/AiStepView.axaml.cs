using Avalonia.Controls;
using Avalonia.Interactivity;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>
/// A step of an agent's turn in AI Chat (M34h): an <see cref="AiStepItem"/>'s kind, title, status, locations and
/// details. The built-in agent's tool calls use it, and so can an external agent's.
/// </summary>
public sealed partial class AiStepView : UserControl
{
    public AiStepView()
    {
        InitializeComponent();
    }

    private void OnLocationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: AiStepLocation location } && DataContext is AiStepItem step)
        {
            step.OpenLocation(location);
        }
    }
}
