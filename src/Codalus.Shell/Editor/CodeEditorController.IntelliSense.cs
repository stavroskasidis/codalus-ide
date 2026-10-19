using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Codalus.LanguageServices.Protocol;
using Codalus.Shell.Editor.IntelliSense;

namespace Codalus.Shell.Editor;

/// <summary>
/// The IntelliSense popups' coordination: the completion list below the caret's line and signature help above it,
/// never overlapping, flipping at the screen's edges; both placed again when the editor scrolls or the window moves, and
/// closed when the line scrolls out of view, the window loses the focus or Escape is pressed. Quick Info doesn't open
/// while either is open.
/// </summary>
public sealed partial class CodeEditorController
{
    private Window? _popupWindow;
    private bool _popupEventsAttached;

    /// <summary>Whether the completion list or signature help is open.</summary>
    public bool IsIntelliSenseOpen => _completion is not null || _signature is not null;

    /// <summary>The last Quick Info shown (tests).</summary>
    internal QuickInfoView? LastQuickInfo { get; private set; }

    /// <summary>Quick Info's popup (the smoke test's screenshot).</summary>
    internal Avalonia.Controls.Primitives.Popup HoverPopup => _hoverPopup;

    /// <summary>Whether Quick Info (the hover) is showing.</summary>
    public bool IsQuickInfoOpen => _hoverPopup.IsOpen;

    private CompletionPopup NewCompletionPopup(int start, int caret) =>
        new(Editor.TextArea, () => _colorizer.Theme, _services.KeyLabel) { StartOffset = start, EndOffset = caret };

    private void OpenCompletion(CompletionPopup window)
    {
        window.CompletionList.SelectionChanged += (_, _) => window.CompletionList.SelectedItem?.Resolve();
        window.Closed += (_, _) =>
        {
            if (_completion == window)
            {
                _completion = null;
                OnPopupsChanged();
            }
        };
        window.CompletionList.BuildFilters();
        window.CompletionList.Refresh();
        _completion = window;
        _hoverPopup.IsOpen = false;
        window.Show();
        OnPopupsChanged();
    }

    private void OpenSignature(SignatureHelpPopup window)
    {
        window.Closed += (_, _) =>
        {
            if (_signature == window)
            {
                _signature = null;
                OnPopupsChanged();
            }
        };
        _signature = window;
        _hoverPopup.IsOpen = false;
        window.Show();
        OnPopupsChanged();
    }

    /// <summary>A popup opened or closed: the context keys, the events that move or close them, and their places.</summary>
    private void OnPopupsChanged()
    {
        _services.SetIntelliSenseContext(_completion is not null, _signature is not null);
        if (IsIntelliSenseOpen && !_popupEventsAttached)
        {
            _popupEventsAttached = true;
            var view = Editor.TextArea.TextView;
            view.ScrollOffsetChanged += OnPopupViewScrolled;
            Editor.TextArea.LostFocus += OnPopupFocusLost;
            Editor.TextArea.DocumentChanged += OnPopupDocumentChanged;
            _popupWindow = TopLevel.GetTopLevel(Editor.TextArea) as Window;
            if (_popupWindow is not null)
            {
                _popupWindow.PositionChanged += OnPopupWindowMoved;
                _popupWindow.Deactivated += OnPopupWindowDeactivated;
            }
        }
        else if (!IsIntelliSenseOpen && _popupEventsAttached)
        {
            _popupEventsAttached = false;
            Editor.TextArea.TextView.ScrollOffsetChanged -= OnPopupViewScrolled;
            Editor.TextArea.LostFocus -= OnPopupFocusLost;
            Editor.TextArea.DocumentChanged -= OnPopupDocumentChanged;
            if (_popupWindow is not null)
            {
                _popupWindow.PositionChanged -= OnPopupWindowMoved;
                _popupWindow.Deactivated -= OnPopupWindowDeactivated;
                _popupWindow = null;
            }
        }

        LayoutPopups();
    }

    /// <summary>Closes the completion list and signature help.</summary>
    public void CloseIntelliSense()
    {
        _completion?.Hide();
        _signature?.Hide();
    }

    private void OnPopupViewScrolled(object? sender, EventArgs e)
    {
        // Placed again while the caret's line is in view; closed once it scrolls out.
        if (IntelliSenseLayout.IsCaretLineVisible(Editor.TextArea))
        {
            LayoutPopups();
        }
        else
        {
            CloseIntelliSense();
        }
    }

    private void OnPopupWindowMoved(object? sender, PixelPointEventArgs e) => LayoutPopups();

    private void OnPopupWindowDeactivated(object? sender, EventArgs e) => CloseIntelliSense();

    private void OnPopupDocumentChanged(object? sender, EventArgs e) => CloseIntelliSense();

    private void OnPopupFocusLost(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        // A click in a popup leaves the focus in the editor; anything else that takes it closes them.
        if (!Editor.TextArea.IsFocused && _completion?.Child?.IsKeyboardFocusWithin != true && _signature?.Child?.IsKeyboardFocusWithin != true)
        {
            CloseIntelliSense();
        }
    }, DispatcherPriority.Background);

    /// <summary>Places the open popups: the list below the line and signature help above it, or the other way at the screen's bottom.</summary>
    private void LayoutPopups() => IntelliSenseLayout.Arrange(Editor.TextArea, _completion, _signature);

    /// <summary>
    /// The popups' keys: the list's arrows, Page Up and Down, Home, End, Enter and Tab while it's open; signature help's
    /// Up and Down through the overloads when the list isn't; Escape closes the list, then signature help.
    /// </summary>
    private bool HandlePopupKey(KeyEventArgs e)
    {
        if (e.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Shift))
        {
            return false;
        }

        if (_completion is { } completion)
        {
            if (e.Key == Key.Escape)
            {
                completion.Hide();
                return true;
            }

            return e.KeyModifiers == KeyModifiers.None && completion.CompletionList.HandleKey(e.Key);
        }

        if (_signature is { } signature && e.KeyModifiers == KeyModifiers.None)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    signature.Hide();
                    return true;
                case Key.Up:
                    return signature.ChangeIndex(-1) && Relayout();
                case Key.Down:
                    return signature.ChangeIndex(+1) && Relayout();
            }
        }

        return false;

        bool Relayout()
        {
            LayoutPopups();
            return true;
        }
    }

    /// <summary>Turns a completion list filter on or off (Alt and its letter while the list is open); false when it has none of that kind.</summary>
    public bool ToggleCompletionFilter(string id)
    {
        if (_completion is not { } completion || !completion.CompletionList.ToggleFilter(id))
        {
            return false;
        }

        LayoutPopups();
        return true;
    }

    /// <summary>Quick Info's content: the symbol, the diagnostics and their buttons.</summary>
    private QuickInfoView QuickInfo(SymbolDescription? description, Control? markdown, IReadOnlyList<Diagnostic> diagnostics)
    {
        var editable = !Model.IsReadOnly && Client?.Capabilities?.CodeActionProvider == true;
        var view = new QuickInfoView(description, markdown, diagnostics, _colorizer.Theme, new QuickInfoActions
        {
            Fix = editable ? d => _services.ApplyTopFixAsync(this, d) : null,
            OtherFixes = editable ? d => _services.ShowCodeActionsAtAsync(this, d) : null,
            AddToChat = _services.CanAskAi ? d => _services.AddDiagnosticToChatAsync(this, d) : null,
            FixKey = _services.KeyLabel("editor.action.applyTopFix"),
            OtherFixesKey = _services.KeyLabel("editor.action.quickFix"),
            OpenLink = _services.OpenLink,
        });
        view.ActionInvoked += (_, _) => _hoverPopup.IsOpen = false;
        LastQuickInfo = view;
        return view;
    }
}
