using DaedalusStudio.Workbench.ToolWindows;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace DaedalusStudio.Shell.Docking;

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
    private bool _isPreview;
    private bool _isPinned;
    private Avalonia.Media.IBrush? _tabAccent;

    /// <summary>The value of the <c>activeDocumentKind</c> context key while this document is active.</summary>
    public abstract string Kind { get; }

    /// <summary>The tab's icon (a file's type, M34a), or null for none.</summary>
    public virtual string? TabIcon => null;

    public bool HasTabIcon => TabIcon is not null;

    /// <summary>
    /// A preview tab (italic title): the next preview replaces it, until it is edited, double-clicked or kept open.
    /// </summary>
    public bool IsPreview
    {
        get => _isPreview;
        set
        {
            if (SetProperty(ref _isPreview, value))
            {
                OnPropertyChanged(nameof(TitleFontStyle));
            }
        }
    }

    /// <summary>The tab title's style: italic for preview tabs.</summary>
    public Avalonia.Media.FontStyle TitleFontStyle => IsPreview ? Avalonia.Media.FontStyle.Italic : Avalonia.Media.FontStyle.Normal;

    /// <summary>A pinned tab (M34d): kept at the left of its group, with a pin, and left open by Close All But Pinned.</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set => SetProperty(ref _isPinned, value);
    }

    /// <summary>
    /// The tab's color by project (M34d, <c>workbench.editor.colorTabsByProject</c>): a stripe along the tab's bottom
    /// edge, or null for none.
    /// </summary>
    public Avalonia.Media.IBrush? TabAccent
    {
        get => _tabAccent;
        set
        {
            if (SetProperty(ref _tabAccent, value))
            {
                OnPropertyChanged(nameof(TabAccentThickness));
            }
        }
    }

    public Avalonia.Thickness TabAccentThickness => _tabAccent is null ? default : new Avalonia.Thickness(0, 0, 0, 3);

    /// <summary>The file the document shows, for its project's color (null for pages).</summary>
    public virtual string? FilePath => null;

    /// <summary>Whether closing must ask to save first (by default: when modified).</summary>
    protected virtual bool NeedsSaveOnClose => IsModified;

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
        if (NeedsSaveOnClose && !_closeConfirmed)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return false;
        }

        return base.OnClose();
    }
}
