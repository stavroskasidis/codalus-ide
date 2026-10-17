using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Codalus.Shell.ViewModels;

namespace Codalus.Shell.Views;

/// <summary>The Toolbox (M41a): a click arms an item (the Pointer disarms); a drag carries it onto the designer's surface.</summary>
public partial class ToolboxView : UserControl
{
    private PointerPressedEventArgs? _pressed;

    public ToolboxView()
    {
        InitializeComponent();
    }

    private ToolboxViewModel? ViewModel => DataContext as ToolboxViewModel;

    private void OnItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ToolboxItemViewModel item } && ViewModel is { } toolbox)
        {
            toolbox.Armed = item.IsPointer || toolbox.Armed == item ? null : item;
        }
    }

    private void OnItemPressed(object? sender, PointerPressedEventArgs e) => _pressed = e;

    private async void OnItemMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } pressed || sender is not Button { DataContext: ToolboxItemViewModel { IsPointer: false } item } || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var point = e.GetPosition(this);
        var start = pressed.GetPosition(this);
        if (Math.Abs(point.X - start.X) < 4 && Math.Abs(point.Y - start.Y) < 4)
        {
            return;
        }

        _pressed = null;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(WinFormsDesignerView.ToolboxFormat, item.Type));
        await DragDrop.DoDragDropAsync(pressed, transfer, DragDropEffects.Copy).ConfigureAwait(true);
    }
}
