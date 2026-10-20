using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.LogicalTree;
using AvaloniaEdit.Editing;

namespace Codalus.Shell.Editor.IntelliSense;

/// <summary>Which side of the caret's line an IntelliSense popup is on.</summary>
public enum PopupSide
{
    Below,
    Above,
}

/// <summary>
/// Where the completion list and signature help go, as a classic IDE puts them: the list below the caret's line and
/// signature help above it, so the two never overlap; the list goes above when there isn't room below (the screen's
/// bottom edge) and signature help then goes below. Alone, signature help prefers above the line.
/// </summary>
public static class IntelliSensePlacement
{
    public static (PopupSide Completion, PopupSide Signature) Decide(double spaceAbove, double spaceBelow, double completionHeight, double signatureHeight, bool completionOpen)
    {
        if (completionOpen)
        {
            var completion = spaceBelow >= completionHeight || spaceBelow >= spaceAbove ? PopupSide.Below : PopupSide.Above;
            return (completion, completion == PopupSide.Below ? PopupSide.Above : PopupSide.Below);
        }

        return (PopupSide.Below, spaceAbove >= signatureHeight || spaceAbove >= spaceBelow ? PopupSide.Above : PopupSide.Below);
    }
}

/// <summary>
/// Places an editor's open IntelliSense popups against its caret's line (<see cref="IntelliSensePlacement"/>): the
/// completion list lined up with the text being completed, signature help with where it opened.
/// </summary>
public static class IntelliSenseLayout
{
    /// <summary>Places the open popups; false when the caret's line isn't laid out.</summary>
    public static bool Arrange(TextArea area, CompletionPopup? completion, SignatureHelpPopup? signature)
    {
        if ((completion is null && signature is null) || CaretLine(area) is not { } line)
        {
            return false;
        }

        var (above, below) = RoomAround(area.TextView, line);
        var (completionSide, signatureSide) = IntelliSensePlacement.Decide(above, below, completion?.PreferredHeight ?? 0, signature?.PreferredHeight ?? 0, completion is not null);
        var both = completion is not null && signature is not null;
        completion?.Place(line, XOf(area, completion.StartOffset), completionSide, allowFlip: !both);
        signature?.Place(line, XOf(area, signature.AnchorOffset), signatureSide, allowFlip: !both);
        return true;
    }

    /// <summary>Whether the caret's line is in the text view (popups close once it scrolls out).</summary>
    public static bool IsCaretLineVisible(TextArea area) =>
        CaretLine(area) is { } line && line.Bottom > 0 && line.Top < area.TextView.Bounds.Height;

    /// <summary>The caret's line in the text view's coordinates, or null when it isn't laid out.</summary>
    public static Rect? CaretLine(TextArea area)
    {
        var view = area.TextView;
        if (!view.VisualLinesValid)
        {
            view.EnsureVisualLines();
        }

        var position = area.Caret.Position;
        var top = view.GetVisualPosition(position, AvaloniaEdit.Rendering.VisualYPosition.LineTop) - view.ScrollOffset;
        var bottom = view.GetVisualPosition(position, AvaloniaEdit.Rendering.VisualYPosition.LineBottom) - view.ScrollOffset;
        return double.IsNaN(top.Y) || double.IsNaN(bottom.Y) ? null : new Rect(0, top.Y, view.Bounds.Width, Math.Max(1, bottom.Y - top.Y));
    }

    /// <summary>The horizontal position of a document offset in the text view.</summary>
    private static double XOf(TextArea area, int offset)
    {
        var view = area.TextView;
        var document = area.Document;
        var location = document.GetLocation(Math.Clamp(offset, 0, document.TextLength));
        return view.GetVisualPosition(new AvaloniaEdit.TextViewPosition(location), AvaloniaEdit.Rendering.VisualYPosition.LineTop).X - view.ScrollOffset.X;
    }

    /// <summary>The room above and below the line on its screen (the window's when the screen isn't known).</summary>
    private static (double Above, double Below) RoomAround(Visual view, Rect line)
    {
        if (TopLevel.GetTopLevel(view) is not { } topLevel)
        {
            return (line.Top, view.Bounds.Height - line.Bottom);
        }

        if (topLevel.Screens?.ScreenFromVisual(view) is { } screen)
        {
            var scaling = screen.Scaling > 0 ? screen.Scaling : topLevel.RenderScaling;
            var top = view.PointToScreen(new Point(0, line.Top));
            var bottom = view.PointToScreen(new Point(0, line.Bottom));
            return ((top.Y - screen.WorkingArea.Y) / scaling, (screen.WorkingArea.Bottom - bottom.Y) / scaling);
        }

        var origin = view.TranslatePoint(new Point(0, line.Top), topLevel) ?? new Point(0, line.Top);
        return (origin.Y, topLevel.Bounds.Height - origin.Y - line.Height);
    }
}

/// <summary>
/// A popup of the editor's IntelliSense (the completion list, signature help): placed against the caret's line, above
/// or below it, by the editor (<see cref="Place"/>), with a thin themed border and the window manager's shadow. It never
/// takes the focus: keys stay in the editor, which passes the popup's own (the arrows, Enter, Escape) to it.
/// </summary>
public abstract class IntelliSensePopup : Popup
{
    protected IntelliSensePopup(TextArea textArea)
    {
        TextArea = textArea;
        IsLightDismissEnabled = false;
        WindowManagerAddShadowHint = true;
        Focusable = false;
        PlacementTarget = textArea.TextView;
        Placement = PlacementMode.AnchorAndGravity;
        Closed += (_, _) => ((ISetLogicalParent)this).SetParent(null);
    }

    public TextArea TextArea { get; }

    /// <summary>The side of the line it was last placed on.</summary>
    public PopupSide Side { get; private set; } = PopupSide.Below;

    /// <summary>The height it wants, to decide whether it fits on a side of the line.</summary>
    public abstract double PreferredHeight { get; }

    /// <summary>How far left of its anchor it starts (the completion list's icon column, so the names line up with the typed text).</summary>
    public virtual double AnchorInset => 0;

    /// <summary>The window it opened on: its placement target's, which Avalonia's popup host keeps for good.</summary>
    private TopLevel? _openedOn;

    /// <summary>
    /// Opens it (styled by the editor's window: its theme and resources), on the window its editor's text view is in;
    /// not while the view isn't in one or isn't showing.
    /// </summary>
    public bool Show()
    {
        if (TextArea.TextView is not { IsEffectivelyVisible: true } view || TopLevel.GetTopLevel(view) is not { } topLevel)
        {
            return false;
        }

        if (((ILogical)this).LogicalParent is { } parent && parent != topLevel)
        {
            ((ISetLogicalParent)this).SetParent(null);
        }

        if (((ILogical)this).LogicalParent is null)
        {
            ((ISetLogicalParent)this).SetParent(topLevel);
        }

        _openedOn = topLevel;
        IsOpen = true;
        return IsOpen;
    }

    /// <summary>
    /// Whether it's open on the window its editor is in now. A popup placed again against a view under another root
    /// throws ("Target control is not in the same tree as the popup parent"), so one that isn't closes instead.
    /// </summary>
    internal bool StillOnItsWindow()
    {
        if (IsOpen && TopLevel.GetTopLevel(TextArea.TextView) != _openedOn)
        {
            Hide();
        }

        return IsOpen;
    }

    /// <summary>Closes it.</summary>
    public void Hide() => IsOpen = false;

    /// <summary>
    /// Places it against <paramref name="line"/> (the caret's line, in the text view's coordinates) at
    /// <paramref name="x"/>: below or above it, flipping to the other side at a screen edge only when
    /// <paramref name="allowFlip"/> (the other popup isn't open).
    /// </summary>
    public void Place(Rect line, double x, PopupSide side, bool allowFlip)
    {
        if (IsOpen && !StillOnItsWindow())
        {
            return;
        }

        Side = side;
        var changed = PlacementAnchor != Anchor(side) || PlacementGravity != Gravity(side)
            || HorizontalOffset != x - AnchorInset || VerticalOffset != line.Top;
        PlacementRect = new Rect(0, 0, 1, Math.Max(1, line.Height));
        PlacementAnchor = Anchor(side);
        PlacementGravity = Gravity(side);
        PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.SlideX | PopupPositionerConstraintAdjustment.ResizeY
            | (allowFlip ? PopupPositionerConstraintAdjustment.FlipY : PopupPositionerConstraintAdjustment.None);
        HorizontalOffset = x - AnchorInset;
        VerticalOffset = line.Top;
        if (!changed && IsOpen)
        {
            // The window moved: the same placement, positioned again on the screen.
            HorizontalOffset = x - AnchorInset + 0.01;
            HorizontalOffset = x - AnchorInset;
        }
    }

    private static PopupAnchor Anchor(PopupSide side) => side == PopupSide.Below ? PopupAnchor.BottomLeft : PopupAnchor.TopLeft;

    private static PopupGravity Gravity(PopupSide side) => side == PopupSide.Below ? PopupGravity.BottomRight : PopupGravity.TopRight;
}
