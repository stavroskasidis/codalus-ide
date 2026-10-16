using System.ComponentModel;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Docking;

namespace Codalus.Shell.Editor;

/// <summary>
/// A document tab showing a <see cref="CodeDocument"/>. A file can have several (split views); they share the model,
/// and only closing the last one asks to save.
/// </summary>
public sealed class CodeEditorDocument : ShellDocument
{
    private readonly Func<CodeDocument, int> _viewCount;
    private Position? _pendingCaret;
    private int _pendingSelection;

    public CodeEditorDocument(CodeDocument model, string id, Func<CodeDocument, int> viewCount, IEditorServices services, Infrastructure.EditorFont font)
    {
        Model = model;
        Services = services;
        Font = font;
        _viewCount = viewCount;
        Id = id;
        CanClose = true;
        CanFloat = true;
        UpdateTitle();
        model.PropertyChanged += OnModelChanged;
    }

    /// <summary>Kept as <c>textFile</c>: key bindings and plugins use it for any text document.</summary>
    public override string Kind => "textFile";

    public override string? TabIcon => Controls.FileIcons.ForFile(Path.GetFileName(Model.Path));

    public CodeDocument Model { get; }

    public override string? FilePath => Model.Path;

    public IEditorServices Services { get; }

    public Infrastructure.EditorFont Font { get; }

    /// <summary>The tab's view. It lives as long as the tab (Dock detaches inactive tabs' views and reuses them).</summary>
    public CodeEditorView? View { get; internal set; }

    /// <summary>The pane wrapping <see cref="View"/>, reused when Dock asks for the view again.</summary>
    internal Avalonia.Controls.Control? Host { get; set; }

    /// <summary>Raised when the view's editor controller is created.</summary>
    public event EventHandler? ControllerAttached;

    /// <summary>The view's editor controller while the tab is shown (Dock may recreate views).</summary>
    public CodeEditorController? Controller { get; private set; }

    protected override bool NeedsSaveOnClose => Model.IsModified && _viewCount(Model) <= 1;

    /// <summary>Moves the caret to <paramref name="position"/> now, or when the view is created.</summary>
    public void Navigate(Position position, int selectLength = 0)
    {
        if (Controller is { } controller)
        {
            controller.Navigate(position, selectLength);
        }
        else
        {
            _pendingCaret = position;
            _pendingSelection = selectLength;
        }
    }

    internal void Attach(CodeEditorController controller)
    {
        Controller = controller;
        ControllerAttached?.Invoke(this, EventArgs.Empty);
        if (_pendingCaret is { } caret)
        {
            _pendingCaret = null;
            controller.Navigate(caret, _pendingSelection);
        }
    }

    internal void Detach(CodeEditorController controller)
    {
        if (Controller == controller)
        {
            Controller = null;
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CodeDocument.IsModified) or nameof(CodeDocument.IsDeletedOnDisk))
        {
            UpdateTitle();
        }
    }

    private void UpdateTitle()
    {
        IsModified = Model.IsModified;
        Title = Model.Name + (Model.ReadOnlyLabel is { } label ? $" [{label}]" : string.Empty) + (Model.IsDeletedOnDisk ? " (deleted)" : string.Empty) + (Model.IsModified ? " ●" : string.Empty);
    }

    public override bool OnClose()
    {
        var close = base.OnClose();
        if (close)
        {
            Model.PropertyChanged -= OnModelChanged;
            View?.Close();
            View = null;
            Host = null;
        }

        return close;
    }
}
