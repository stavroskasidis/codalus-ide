using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Codalus.Shell.Views.PropertyEditors;

/// <summary>
/// A cursor's picture in Cursor's list (#68, D41 question 13): the shell's own 16 px drawings of Windows' cursors, in
/// Windows' own black and white in every theme (a picture of the cursor, as the form's picture is the form's), so they
/// aren't icons and take no palette color. A cursor without a drawing shows none.
/// </summary>
public sealed class CursorPicture : Control
{
    public static readonly StyledProperty<string?> CursorNameProperty = AvaloniaProperty.Register<CursorPicture, string?>(nameof(CursorName));

    private static readonly IBrush s_white = Brushes.White;
    private static readonly IBrush s_black = Brushes.Black;
    private static readonly IBrush s_red = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
    private static readonly IBrush s_blue = new SolidColorBrush(Color.FromRgb(0x1F, 0x7A, 0xE0));

    /// <summary>A drawing's layers: its path, fill and outline (a null fill or outline draws none).</summary>
    private sealed record Layer(string Path, IBrush? Fill, IBrush? Stroke, double Thickness = 1);

    private const string Arrow = "M3.5,1.5 V12.7 L6.2,10.1 L8.1,14.5 L10,13.7 L8.1,9.4 H11.8 Z";
    private const string SmallArrow = "M2.5,1.5 V10.5 L4.6,8.5 L6.1,11.9 L7.6,11.3 L6.1,7.9 H9 Z";

    private static readonly Dictionary<string, Layer[]> s_drawings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Arrow"] = [new(Arrow, s_white, s_black)],
        ["Default"] = [new(Arrow, s_white, s_black)],
        ["AppStarting"] = [new(SmallArrow, s_white, s_black), new("M11.6,8.7 A2.9,2.9 0 1 1 11.59,8.7", null, s_white, 2.8), new("M11.6,8.7 A2.9,2.9 0 1 1 8.7,11.6", null, s_blue, 1.6)],
        ["Cross"] = [new("M8,1.5 V14.5 M1.5,8 H14.5", null, s_white, 3), new("M8,1.5 V14.5 M1.5,8 H14.5", null, s_black)],
        ["Hand"] = [new("M5.6,1.6 C6.3,1.6 6.8,2.1 6.8,2.8 V7.4 C7,6.7 8.8,6.7 8.9,7.5 C9.2,6.9 10.8,7 10.9,7.8 C11.3,7.3 12.7,7.5 12.7,8.4 V11.8 C12.7,13.7 11.4,15.2 9.5,15.2 H7.9 C7,15.2 6.3,14.8 5.8,14.1 L3,10.3 C2.5,9.6 3.4,8.7 4.2,9.2 L5.1,10 V2.8 C5.1,2.1 5.6,1.6 5.6,1.6 Z", s_white, s_black), new("M8.9,7.6 V10 M10.9,7.9 V10", null, s_black)],
        ["Help"] = [new(SmallArrow, s_white, s_black), new("M10.6,6.4 A1.9,1.9 0 1 1 13.2,8.2 C12.6,8.5 12.3,8.8 12.3,9.5 V10", null, s_white, 3), new("M10.6,6.4 A1.9,1.9 0 1 1 13.2,8.2 C12.6,8.5 12.3,8.8 12.3,9.5 V10", null, s_black, 1.3), new("M11.55,12 A0.75,0.75 0 1 1 13.05,12 A0.75,0.75 0 1 1 11.55,12 Z", s_black, s_white, 0.6)],
        ["HSplit"] = [new("M2,6.8 H14 M2,9.2 H14", null, s_white, 2.6), new("M2,6.8 H14 M2,9.2 H14", null, s_black), new("M8,1.2 L5.4,3.8 H7.3 V5.8 H8.7 V3.8 H10.6 Z M8,14.8 L5.4,12.2 H7.3 V10.2 H8.7 V12.2 H10.6 Z", s_black, s_white, 0.8)],
        ["VSplit"] = [new("M6.8,2 V14 M9.2,2 V14", null, s_white, 2.6), new("M6.8,2 V14 M9.2,2 V14", null, s_black), new("M1.2,8 L3.8,5.4 V7.3 H5.8 V8.7 H3.8 V10.6 Z M14.8,8 L12.2,5.4 V7.3 H10.2 V8.7 H12.2 V10.6 Z", s_black, s_white, 0.8)],
        ["IBeam"] = [new("M5.5,2 H10.5 M5.5,14 H10.5 M8,2.5 V13.5", null, s_white, 3), new("M5.5,2 H10.5 M5.5,14 H10.5 M8,2.5 V13.5", null, s_black, 1.1)],
        ["No"] = [new("M2.4,8 A5.6,5.6 0 1 1 13.6,8 A5.6,5.6 0 1 1 2.4,8 Z M4.2,4.2 L11.8,11.8", null, s_white, 3.8), new("M2.4,8 A5.6,5.6 0 1 1 13.6,8 A5.6,5.6 0 1 1 2.4,8 Z M4.2,4.2 L11.8,11.8", null, s_red, 2)],
        ["NoMove2D"] = [new("M1.5,8 A6.5,6.5 0 1 1 14.5,8 A6.5,6.5 0 1 1 1.5,8 Z", s_white, s_black), new("M8,3.2 L6.4,5.2 H9.6 Z M8,12.8 L6.4,10.8 H9.6 Z M3.2,8 L5.2,6.4 V9.6 Z M12.8,8 L10.8,6.4 V9.6 Z M7,8 A1,1 0 1 1 9,8 A1,1 0 1 1 7,8 Z", s_black, null)],
        ["SizeAll"] = [new("M8,1 L5.5,3.5 H7.2 V7.2 H3.5 V5.5 L1,8 L3.5,10.5 V8.8 H7.2 V12.5 H5.5 L8,15 L10.5,12.5 H8.8 V8.8 H12.5 V10.5 L15,8 L12.5,5.5 V7.2 H8.8 V3.5 H10.5 Z", s_black, s_white, 0.8)],
        ["SizeNS"] = [new("M8,1 L4.5,4.5 H7.2 V11.5 H4.5 L8,15 L11.5,11.5 H8.8 V4.5 H11.5 Z", s_black, s_white, 0.8)],
        ["SizeWE"] = [new("M1,8 L4.5,4.5 V7.2 H11.5 V4.5 L15,8 L11.5,11.5 V8.8 H4.5 V11.5 Z", s_black, s_white, 0.8)],
        ["SizeNWSE"] = [new("M2,2 H7 L5.3,3.7 L12.3,10.7 L14,9 V14 H9 L10.7,12.3 L3.7,5.3 L2,7 Z", s_black, s_white, 0.8)],
        ["SizeNESW"] = [new("M14,2 V7 L12.3,5.3 L5.3,12.3 L7,14 H2 V9 L3.7,10.7 L10.7,3.7 L9,2 Z", s_black, s_white, 0.8)],
        ["UpArrow"] = [new("M8,1 L3,7 H6.5 V15 H9.5 V7 H13 Z", s_black, s_white, 0.8)],
        ["WaitCursor"] = [new("M4,1.5 H12 V3 C12,5.5 9.5,6.8 9.5,8 C9.5,9.2 12,10.5 12,13 V14.5 H4 V13 C4,10.5 6.5,9.2 6.5,8 C6.5,6.8 4,5.5 4,3 Z", s_white, s_black), new("M5.5,13.5 C5.5,11.5 8,10.5 8,10.5 C8,10.5 10.5,11.5 10.5,13.5 Z", s_black, null)],
    };

    private static readonly Dictionary<string, Geometry> s_geometries = new(StringComparer.Ordinal);

    static CursorPicture()
    {
        AffectsRender<CursorPicture>(CursorNameProperty);
    }

    /// <summary>The cursor's name (Cursor's standard value: Hand, IBeam…).</summary>
    public string? CursorName
    {
        get => GetValue(CursorNameProperty);
        set => SetValue(CursorNameProperty, value);
    }

    /// <summary>Whether the shell has a drawing of a cursor.</summary>
    public static bool HasDrawing(string cursor) => s_drawings.ContainsKey(cursor);

    protected override Size MeasureOverride(Size availableSize) => new(16, 16);

    public override void Render(DrawingContext context)
    {
        if (CursorName is not { } cursor || !s_drawings.TryGetValue(cursor, out var layers))
        {
            return;
        }

        foreach (var layer in layers)
        {
            if (!s_geometries.TryGetValue(layer.Path, out var geometry))
            {
                s_geometries[layer.Path] = geometry = Geometry.Parse(layer.Path);
            }

            context.DrawGeometry(layer.Fill, layer.Stroke is null ? null : new Pen(layer.Stroke, layer.Thickness, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round), geometry);
        }
    }
}
