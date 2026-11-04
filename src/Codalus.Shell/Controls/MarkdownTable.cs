using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Codalus.Shell.Controls;

/// <summary>
/// The cells of a Markdown table, row by row, laid out as a browser lays out an automatic table: each column as wide as
/// its widest cell when they all fit; otherwise each at least as wide as its longest word, and the width left shared by
/// how much more each column wants, the cells wrapping (a cell is a <see cref="Border"/> around a <see cref="TextBlock"/>). Every cell of a row is as high as the highest, so their borders line up.
/// </summary>
public sealed class MarkdownTable : Panel
{
    private double[] _widths = [];
    private double[] _heights = [];

    public MarkdownTable(int columns) => Columns = Math.Max(1, columns);

    public int Columns { get; }

    /// <summary>The columns' widths of the last measure (tests).</summary>
    public IReadOnlyList<double> ColumnWidths => _widths;

    protected override Size MeasureOverride(Size availableSize)
    {
        var cells = Children;
        var min = new double[Columns];
        var max = new double[Columns];
        for (var k = 0; k < cells.Count; k++)
        {
            var column = k % Columns;
            min[column] = Math.Max(min[column], Narrowest(cells[k]));
            cells[k].Measure(Size.Infinity);
            max[column] = Math.Max(max[column], cells[k].DesiredSize.Width);
        }

        _widths = Widths(min, max, availableSize.Width);
        _heights = new double[(cells.Count + Columns - 1) / Columns];
        for (var k = 0; k < cells.Count; k++)
        {
            cells[k].Measure(new Size(_widths[k % Columns], double.PositiveInfinity));
            _heights[k / Columns] = Math.Max(_heights[k / Columns], cells[k].DesiredSize.Height);
        }

        return new Size(_widths.Sum(), _heights.Sum());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cells = Children;
        var y = 0.0;
        for (var row = 0; row < _heights.Length; row++)
        {
            var x = 0.0;
            for (var column = 0; column < Columns && row * Columns + column < cells.Count; column++)
            {
                cells[row * Columns + column].Arrange(new Rect(x, y, _widths[column], _heights[row]));
                x += _widths[column];
            }

            y += _heights[row];
        }

        return finalSize;
    }

    /// <summary>
    /// How narrow a cell can be: its longest word in its own font, or its widest inline control (a link). Neither a desired
    /// size (never wider than the space it was measured in) nor a layout at no width (empty) can tell.
    /// </summary>
    private static double Narrowest(Control cell)
    {
        if (cell is not Border { Child: TextBlock text } border)
        {
            cell.Measure(new Size(0, double.PositiveInfinity));
            return cell.DesiredSize.Width;
        }

        var widest = 0.0;
        void Words(string? source, FontFamily family, FontStyle style, FontWeight weight, double size)
        {
            foreach (var word in (source ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                using var layout = new TextLayout(word, new Typeface(family, style, weight), size, null);
                widest = Math.Max(widest, layout.Width);
            }
        }

        if (text.Inlines is { Count: > 0 } inlines)
        {
            foreach (var inline in inlines)
            {
                if (inline is Run run)
                {
                    Words(run.Text, run.FontFamily, run.FontStyle, run.FontWeight, run.FontSize);
                }
                else if (inline is InlineUIContainer { Child: { } child })
                {
                    child.Measure(Size.Infinity);
                    widest = Math.Max(widest, child.DesiredSize.Width);
                }
            }
        }
        else
        {
            Words(text.Text, text.FontFamily, text.FontStyle, text.FontWeight, text.FontSize);
        }

        var insets = border.Padding.Left + border.Padding.Right + border.BorderThickness.Left + border.BorderThickness.Right
            + text.Margin.Left + text.Margin.Right + text.Padding.Left + text.Padding.Right;
        return Math.Ceiling(widest + insets);
    }

    /// <summary>
    /// The columns' widths in <paramref name="available"/>: their widest (<paramref name="max"/>) when they fit; their
    /// narrowest (<paramref name="min"/>, the longest word) when not even those fit; otherwise the narrowest plus a share
    /// of what is left, by how much wider each would be.
    /// </summary>
    internal static double[] Widths(double[] min, double[] max, double available)
    {
        var widths = new double[min.Length];
        var widest = 0.0;
        var narrowest = 0.0;
        for (var c = 0; c < min.Length; c++)
        {
            max[c] = Math.Max(max[c], min[c]);
            widest += max[c];
            narrowest += min[c];
        }

        for (var c = 0; c < min.Length; c++)
        {
            widths[c] = double.IsInfinity(available) || widest <= available ? max[c]
                : narrowest >= available ? min[c]
                : Math.Floor(min[c] + (available - narrowest) * (max[c] - min[c]) / (widest - narrowest));
        }

        return widths;
    }
}
