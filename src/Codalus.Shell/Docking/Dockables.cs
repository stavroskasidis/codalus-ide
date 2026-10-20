using Codalus.Workbench.ToolWindows;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace Codalus.Shell.Docking;

public sealed class ShellDockFactory : Factory
{
    public ShellDockFactory()
    {
        // Closing a tool window removes it; showing it again re-adds the same instance (see LayoutManager).
        HideToolsOnClose = false;
        HideDocumentsOnClose = false;
    }
}

/// <summary>
/// What a tab shows, for tool windows and documents alike. Either can be dragged into the other kind of group, or into
/// a floating window, and every tab strip's header template and tab style is typed to this, never to one kind: a header
/// template that didn't match its dockable made Avalonia look further up for one, and the view locator, which matches
/// every dockable, put the whole view in the tab (a tool window docked among the documents).
/// </summary>
public interface IShellTab
{
    string Title { get; }

    /// <summary>The tab's icon (<c>kind</c> or <c>kind@Family</c>), or null for none.</summary>
    string? TabIcon { get; }

    bool HasTabIcon { get; }

    /// <summary>The tab's tooltip: a document's file, a tool window's title; null for none.</summary>
    string? TabToolTip { get; }

    /// <summary>The title's style: italic for a preview tab.</summary>
    Avalonia.Media.FontStyle TitleFontStyle { get; }

    /// <summary>A preview tab (documents only).</summary>
    bool IsPreview { get; }

    /// <summary>A pinned tab (documents only).</summary>
    bool IsPinned { get; }

    /// <summary>Pins or unpins the tab (its pin button beside the close button), or null when it can't be pinned.</summary>
    System.Windows.Input.ICommand? PinCommand { get; }

    /// <summary>The stripe along the tab's bottom edge (a document colored by project), or null.</summary>
    Avalonia.Media.IBrush? TabAccent { get; }

    Avalonia.Thickness TabAccentThickness { get; }
}

/// <summary>A tool window in the dock layout. Its view is chosen by the type of <see cref="Content"/>.</summary>
public sealed class ToolWindowDockable : Tool, IShellTab
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

    /// <summary>
    /// The tab's icon: the one its View menu entry shows, in its command family's color (<c>kind@Family</c>), or null.
    /// A tab too narrow for its title shows only this icon.
    /// </summary>
    public string? TabIcon => Controls.CommandIcons.For(BuiltIns.ViewCommandPrefix + Descriptor.Id, _ => Descriptor.Icon) is { } icon
        ? icon.Accent is { } accent ? $"{icon.Kind}@{char.ToUpperInvariant(accent[0])}{accent[1..]}" : icon.Kind
        : null;

    public bool HasTabIcon => TabIcon is not null;

    public string? TabToolTip => Title;

    // A tool window docked among the documents: a plain tab, never a preview, pinned or colored one.
    public Avalonia.Media.FontStyle TitleFontStyle => Avalonia.Media.FontStyle.Normal;

    public bool IsPreview => false;

    public bool IsPinned => false;

    public System.Windows.Input.ICommand? PinCommand => null;

    public Avalonia.Media.IBrush? TabAccent => null;

    public Avalonia.Thickness TabAccentThickness => default;
}

/// <summary>A document tab (Settings, Keyboard Shortcuts, a text file…).</summary>
public abstract class ShellDocument : Document, IShellTab
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

    /// <summary>Pins or unpins the tab (the layout sets it as the document joins a group).</summary>
    public System.Windows.Input.ICommand? PinCommand { get; set; }

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

    public string? TabToolTip => FilePath;

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
