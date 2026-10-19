using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using AvaloniaEdit;
using AvaloniaEdit.Editing;

namespace Codalus.Shell.Editor;

/// <summary>
/// The code editor's text editor: AvaloniaEdit's, with a text area (<see cref="CodeTextArea"/>) that can hold a band
/// pinned over its top (sticky scroll) in the editor's own visual layer. It keeps AvaloniaEdit's control themes.
/// </summary>
public sealed class CodeTextEditor : TextEditor
{
    public CodeTextEditor()
        : base(new CodeTextArea())
    {
    }

    /// <summary>The text area, which holds the header band.</summary>
    public CodeTextArea CodeArea => (CodeTextArea)TextArea;

    protected override Type StyleKeyOverride => typeof(TextEditor);
}

/// <summary>
/// AvaloniaEdit's text area with a <see cref="Header"/>: a control over the top of the margins and the text view.
/// </summary>
/// <remarks>
/// The header is a child of the text area, after its template (the margins and the text view) and before the find
/// widget, which AvaloniaEdit adds to the text area when it opens. So it draws over the text and under the find widget,
/// and everything outside the editor (the quick pick and command palette, Find and Replace, dialogs, the document
/// switcher, Code Search, notifications, popups) draws over it. It was an adorner in the window's adorner layer, which
/// draws above all of the window's content: the sticky scroll covered the quick pick's first rows (the maintainer's
/// report: the branch picker's Create New Branch and first branches) and the find widget.
/// </remarks>
public sealed class CodeTextArea : TextArea
{
    private Control? _header;

    /// <summary>The band over the top of the text area (the sticky scroll), or null.</summary>
    public Control? Header
    {
        get => _header;
        set
        {
            if (ReferenceEquals(_header, value))
            {
                return;
            }

            if (_header is { } old)
            {
                VisualChildren.Remove(old);
                LogicalChildren.Remove(old);
            }

            _header = value;
            AttachHeader();
        }
    }

    protected override Type StyleKeyOverride => typeof(TextArea);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        // Applying a template clears the visual children: the header goes back over the new template's root.
        AttachHeader();
    }

    private void AttachHeader()
    {
        if (_header is not { } header)
        {
            return;
        }

        if (!LogicalChildren.Contains(header))
        {
            LogicalChildren.Add(header);
        }

        if (!VisualChildren.Contains(header))
        {
            // Right after the template's root (the margins and the text view), before the find widget.
            VisualChildren.Insert(Math.Min(1, VisualChildren.Count), header);
        }

        InvalidateMeasure();
    }
}
