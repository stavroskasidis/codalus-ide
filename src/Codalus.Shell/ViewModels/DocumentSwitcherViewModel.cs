using System.Collections.ObjectModel;
using Codalus.Shell.Docking;
using Codalus.Shell.Infrastructure;

namespace Codalus.Shell.ViewModels;

/// <summary>A document in the switcher: its title, folder and icon.</summary>
public sealed record DocumentSwitcherItem(ShellDocument Document, string Title, string? Folder, string? Icon)
{
    public bool HasIcon => Icon is not null;

    public bool HasFolder => Folder is not null;
}

/// <summary>
/// The Ctrl+Tab switcher (deferred from M12b): the open documents, the most recently used first. Ctrl+Tab opens it on
/// the document used before the active one; each Tab (Shift+Tab back, or the arrow keys) moves on while Ctrl is held,
/// and releasing Ctrl, <c>Enter</c> or a click switches to the one selected; <c>Esc</c> leaves the active one.
/// </summary>
public sealed class DocumentSwitcherViewModel : ObservableObject
{
    private int _selectedIndex = -1;
    private bool _isOpen;

    public ObservableCollection<DocumentSwitcherItem> Items { get; } = [];

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetField(ref _isOpen, value);
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => SetField(ref _selectedIndex, value);
    }

    public DocumentSwitcherItem? Selected => SelectedIndex >= 0 && SelectedIndex < Items.Count ? Items[SelectedIndex] : null;

    /// <summary>Raised with the document chosen when the switcher commits.</summary>
    public event EventHandler<ShellDocument>? Chosen;

    /// <summary>Opens on <paramref name="documents"/> (most recently used first), selecting the next one in <paramref name="direction"/>.</summary>
    public void Open(IReadOnlyList<DocumentSwitcherItem> documents, int direction)
    {
        Items.Clear();
        foreach (var document in documents)
        {
            Items.Add(document);
        }

        SelectedIndex = Items.Count < 2 ? Items.Count - 1 : direction > 0 ? 1 : Items.Count - 1;
        IsOpen = Items.Count > 0;
    }

    /// <summary>Moves the selection, wrapping around.</summary>
    public void Move(int direction)
    {
        if (Items.Count > 0)
        {
            SelectedIndex = (SelectedIndex + direction + Items.Count) % Items.Count;
        }
    }

    /// <summary>Switches to the selected document and closes.</summary>
    public void Commit()
    {
        if (!IsOpen)
        {
            return;
        }

        var chosen = Selected?.Document;
        Close();
        if (chosen is not null)
        {
            Chosen?.Invoke(this, chosen);
        }
    }

    /// <summary>Closes without switching.</summary>
    public void Close()
    {
        IsOpen = false;
        Items.Clear();
        SelectedIndex = -1;
    }
}
