using Daedalus.Workbench.ToolWindows;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace Daedalus.Shell.Docking;

public sealed class ShellDockFactory : Factory
{
    public ShellDockFactory()
    {
        // Closing a tool window removes it; showing it again re-adds the same instance (see LayoutManager).
        HideToolsOnClose = false;
        HideDocumentsOnClose = false;
    }
}

/// <summary>A tool window in the dock layout. Its view is chosen by the type of <see cref="Content"/>.</summary>
public sealed class ToolWindowDockable : Tool
{
    public ToolWindowDockable(ToolWindowDescriptor descriptor, object? content)
    {
        Descriptor = descriptor;
        Content = content;
        Id = descriptor.Id;
        Title = descriptor.Title;
        CanClose = true;
        CanPin = true;
        CanFloat = true;
    }

    public ToolWindowDescriptor Descriptor { get; }

    public object? Content { get; }
}

/// <summary>A document tab (Settings, Keyboard Shortcuts, a text file…).</summary>
public abstract class ShellDocument : Document
{
    private bool _closeConfirmed;

    /// <summary>The value of the <c>activeDocumentKind</c> context key while this document is active.</summary>
    public abstract string Kind { get; }

    /// <summary>Raised instead of closing when the document has unsaved changes.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Closes without asking again (after the user chose Save or Discard).</summary>
    public void CloseConfirmed()
    {
        _closeConfirmed = true;
        Factory?.CloseDockable(this);
    }

    public override bool OnClose()
    {
        if (IsModified && !_closeConfirmed)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return false;
        }

        return base.OnClose();
    }
}
