using Avalonia.Controls;
using Avalonia.Input;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

public partial class DocumentSwitcherView : UserControl
{
    public DocumentSwitcherView()
    {
        InitializeComponent();
        // A click switches to the document.
        List.AddHandler(PointerReleasedEvent, (_, _) => (DataContext as DocumentSwitcherViewModel)?.Commit(), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
    }
}
