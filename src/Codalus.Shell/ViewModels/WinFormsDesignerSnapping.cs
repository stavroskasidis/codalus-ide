using Codalus.Workbench.WinForms;

namespace Codalus.Shell.ViewModels;

/// <summary>A snap line the surface draws while a control is moved or resized: from (X1, Y1) to (X2, Y2), an edge, a baseline or a margin (or padding).</summary>
public sealed record DesignerSnapLine(double X1, double Y1, double X2, double Y2, string Kind)
{
    public const string Edge = "edge";
    public const string Baseline = "baseline";
    public const string Margin = "margin";
}

/// <summary>A move or resize snapped: the bounds it ends at and the lines that show why.</summary>
public sealed record DesignerSnap(DesignerRect Bounds, IReadOnlyList<DesignerSnapLine> Lines);

/// <summary>
/// Visual Studio's snap lines (M41b, ADR 0073), in the shell, over the bounds the designer host reported: a moved or
/// resized control's edges snap to its siblings' edges (left to left, right to right, top to top, bottom to bottom),
/// its text baseline to theirs, its margin to theirs (the 3 pixels each side of a control: 6 between two) and to its
/// container's padding (a form's 9 with the control's 3: 12 from the form's edge), when within 8 pixels.
/// </summary>
public static class DesignerSnapping
{
    /// <summary>How near a line must be to snap (Visual Studio's).</summary>
    public const double Distance = 8;

    private static readonly int[] s_margin = [3, 3, 3, 3];

    private sealed record Candidate(double Target, DesignerComponentView? Other, string Kind, string Side);

    private static int[] MarginOf(DesignerComponentView view) => view.Margin ?? s_margin;

    private static int[] PaddingOf(DesignerComponentView view, bool isRoot) => view.Padding ?? (isRoot ? [9, 9, 9, 9] : [0, 0, 0, 0]);

    /// <summary>The client area of a container in the frame (inside its border; as wide at the bottom and right as at the left).</summary>
    public static DesignerRect ClientOf(DesignerComponentView parent)
    {
        var border = parent.ClientX - parent.X;
        return new DesignerRect(parent.ClientX, parent.ClientY, parent.Width - (2 * border), parent.Height - (parent.ClientY - parent.Y) - border);
    }

    /// <summary>A control moved to <paramref name="proposed"/> (its frame bounds), snapped to its siblings and its container.</summary>
    public static DesignerSnap Move(WinFormsDesignerDocument document, DesignerComponentView moving, DesignerRect proposed, IReadOnlySet<string> except)
    {
        var (parent, siblings, isRoot) = Context(document, moving, except);
        if (parent is null)
        {
            return new DesignerSnap(proposed, []);
        }

        var m = MarginOf(moving);
        var client = ClientOf(parent);
        var padding = PaddingOf(parent, isRoot);

        // Where the left (x) and the top (y) would go for each line.
        var xs = new List<Candidate> { new(client.X + padding[0] + m[0], null, DesignerSnapLine.Margin, "left"), new(client.Right - padding[2] - m[2] - proposed.Width, null, DesignerSnapLine.Margin, "right") };
        var ys = new List<Candidate> { new(client.Y + padding[1] + m[1], null, DesignerSnapLine.Margin, "top"), new(client.Bottom - padding[3] - m[3] - proposed.Height, null, DesignerSnapLine.Margin, "bottom") };
        foreach (var other in siblings)
        {
            var o = MarginOf(other);
            xs.Add(new(other.X, other, DesignerSnapLine.Edge, "left"));
            xs.Add(new(other.X + other.Width - proposed.Width, other, DesignerSnapLine.Edge, "right"));
            xs.Add(new(other.X + other.Width + o[2] + m[0], other, DesignerSnapLine.Margin, "left"));
            xs.Add(new(other.X - o[0] - m[2] - proposed.Width, other, DesignerSnapLine.Margin, "right"));
            ys.Add(new(other.Y, other, DesignerSnapLine.Edge, "top"));
            ys.Add(new(other.Y + other.Height - proposed.Height, other, DesignerSnapLine.Edge, "bottom"));
            ys.Add(new(other.Y + other.Height + o[3] + m[1], other, DesignerSnapLine.Margin, "top"));
            ys.Add(new(other.Y - o[1] - m[3] - proposed.Height, other, DesignerSnapLine.Margin, "bottom"));
            if (moving.Baseline is { } baseline && other.Baseline is { } otherBaseline)
            {
                ys.Add(new(other.Y + otherBaseline - baseline, other, DesignerSnapLine.Baseline, "baseline"));
            }
        }

        var x = Nearest(xs, proposed.X);
        var y = Nearest(ys, proposed.Y, prefer: DesignerSnapLine.Baseline);
        var bounds = proposed with { X = x?.Target ?? proposed.X, Y = y?.Target ?? proposed.Y };
        return new DesignerSnap(bounds, Lines(bounds, client, padding, xs, ys, moving.Baseline));
    }

    /// <summary>A control resized from a grab handle to <paramref name="proposed"/>: the edges the handle moves snap.</summary>
    public static DesignerSnap Resize(WinFormsDesignerDocument document, DesignerComponentView view, DesignerRect proposed, int handle)
    {
        var (parent, siblings, isRoot) = Context(document, view, new HashSet<string>(StringComparer.Ordinal));
        if (parent is null || view == document.Root)
        {
            return new DesignerSnap(proposed, []);
        }

        var m = MarginOf(view);
        var client = ClientOf(parent);
        var padding = PaddingOf(parent, isRoot);
        bool left = handle is 0 or 6 or 7, right = handle is 2 or 3 or 4, top = handle is 0 or 1 or 2, bottom = handle is 4 or 5 or 6;
        var lefts = new List<Candidate> { new(client.X + padding[0] + m[0], null, DesignerSnapLine.Margin, "left") };
        var rights = new List<Candidate> { new(client.Right - padding[2] - m[2], null, DesignerSnapLine.Margin, "right") };
        var tops = new List<Candidate> { new(client.Y + padding[1] + m[1], null, DesignerSnapLine.Margin, "top") };
        var bottoms = new List<Candidate> { new(client.Bottom - padding[3] - m[3], null, DesignerSnapLine.Margin, "bottom") };
        foreach (var other in siblings)
        {
            var o = MarginOf(other);
            lefts.Add(new(other.X, other, DesignerSnapLine.Edge, "left"));
            lefts.Add(new(other.X + other.Width + o[2] + m[0], other, DesignerSnapLine.Margin, "left"));
            rights.Add(new(other.X + other.Width, other, DesignerSnapLine.Edge, "right"));
            rights.Add(new(other.X - o[0] - m[2], other, DesignerSnapLine.Margin, "right"));
            tops.Add(new(other.Y, other, DesignerSnapLine.Edge, "top"));
            tops.Add(new(other.Y + other.Height + o[3] + m[1], other, DesignerSnapLine.Margin, "top"));
            bottoms.Add(new(other.Y + other.Height, other, DesignerSnapLine.Edge, "bottom"));
            bottoms.Add(new(other.Y - o[1] - m[3], other, DesignerSnapLine.Margin, "bottom"));
        }

        double x1 = proposed.X, y1 = proposed.Y, x2 = proposed.Right, y2 = proposed.Bottom;
        var lines = new List<DesignerSnapLine>();
        void Edge(bool moves, List<Candidate> candidates, ref double value, bool vertical)
        {
            if (moves && Nearest(candidates, value) is { } snap)
            {
                value = snap.Target;
                lines.Add(vertical ? new DesignerSnapLine(value, Math.Min(y1, snap.Other?.Y ?? y1), value, Math.Max(y2, snap.Other is { } o ? o.Y + o.Height : y2), snap.Kind)
                    : new DesignerSnapLine(Math.Min(x1, snap.Other?.X ?? x1), value, Math.Max(x2, snap.Other is { } p ? p.X + p.Width : x2), value, snap.Kind));
            }
        }

        Edge(left, lefts, ref x1, vertical: true);
        Edge(right, rights, ref x2, vertical: true);
        Edge(top, tops, ref y1, vertical: false);
        Edge(bottom, bottoms, ref y2, vertical: false);
        return new DesignerSnap(DesignerRect.Between(x1, y1, Math.Max(x1 + 1, x2), Math.Max(y1 + 1, y2)), lines);
    }

    private static (DesignerComponentView? Parent, List<DesignerComponentView> Siblings, bool IsRoot) Context(WinFormsDesignerDocument document, DesignerComponentView view, IReadOnlySet<string> except)
    {
        if (view.Parent is not { } parentName || document.Find(parentName) is not { } parent)
        {
            return (null, [], false);
        }

        var siblings = document.ChildrenOf(parentName).Where(c => c.Name != view.Name && !except.Contains(c.Name) && c.Visible && !c.IsPart).ToList();
        return (parent, siblings, parent == document.Root);
    }

    private static Candidate? Nearest(List<Candidate> candidates, double value, string? prefer = null)
    {
        Candidate? best = null;
        foreach (var candidate in candidates)
        {
            var distance = Math.Abs(candidate.Target - value);
            if (distance > Distance)
            {
                continue;
            }

            if (best is null || distance < Math.Abs(best.Target - value) - 0.01 || (Math.Abs(distance - Math.Abs(best.Target - value)) < 0.01 && candidate.Kind == prefer))
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>The lines that hold at the snapped bounds (every candidate exactly met), as Visual Studio draws them.</summary>
    private static List<DesignerSnapLine> Lines(DesignerRect r, DesignerRect client, int[] padding, List<Candidate> xs, List<Candidate> ys, double? baseline)
    {
        var lines = new List<DesignerSnapLine>();
        foreach (var c in xs.Where(c => Math.Abs(c.Target - r.X) < 0.5))
        {
            if (c.Other is not { } o)
            {
                var edge = c.Side == "left" ? client.X + padding[0] : client.Right - padding[2];
                var from = c.Side == "left" ? edge : r.Right;
                var to = c.Side == "left" ? r.X : edge;
                lines.Add(new DesignerSnapLine(from, r.Y + (r.Height / 2), to, r.Y + (r.Height / 2), DesignerSnapLine.Margin));
            }
            else if (c.Kind == DesignerSnapLine.Edge)
            {
                var at = c.Side == "left" ? r.X : r.Right;
                lines.Add(new DesignerSnapLine(at, Math.Min(r.Y, o.Y), at, Math.Max(r.Bottom, o.Y + o.Height), DesignerSnapLine.Edge));
            }
            else
            {
                var y = (Math.Max(r.Y, o.Y) + Math.Min(r.Bottom, o.Y + o.Height)) / 2;
                lines.Add(c.Side == "left"
                    ? new DesignerSnapLine(o.X + o.Width, y, r.X, y, DesignerSnapLine.Margin)
                    : new DesignerSnapLine(r.Right, y, o.X, y, DesignerSnapLine.Margin));
            }
        }

        foreach (var c in ys.Where(c => Math.Abs(c.Target - r.Y) < 0.5))
        {
            if (c.Other is not { } o)
            {
                var edge = c.Side == "top" ? client.Y + padding[1] : client.Bottom - padding[3];
                var from = c.Side == "top" ? edge : r.Bottom;
                var to = c.Side == "top" ? r.Y : edge;
                lines.Add(new DesignerSnapLine(r.X + (r.Width / 2), from, r.X + (r.Width / 2), to, DesignerSnapLine.Margin));
            }
            else if (c.Kind == DesignerSnapLine.Baseline && baseline is { } b)
            {
                lines.Add(new DesignerSnapLine(Math.Min(r.X, o.X), r.Y + b, Math.Max(r.Right, o.X + o.Width), r.Y + b, DesignerSnapLine.Baseline));
            }
            else if (c.Kind == DesignerSnapLine.Edge)
            {
                var at = c.Side == "top" ? r.Y : r.Bottom;
                lines.Add(new DesignerSnapLine(Math.Min(r.X, o.X), at, Math.Max(r.Right, o.X + o.Width), at, DesignerSnapLine.Edge));
            }
            else
            {
                var x = (Math.Max(r.X, o.X) + Math.Min(r.Right, o.X + o.Width)) / 2;
                lines.Add(c.Side == "top"
                    ? new DesignerSnapLine(x, o.Y + o.Height, x, r.Y, DesignerSnapLine.Margin)
                    : new DesignerSnapLine(x, r.Bottom, x, o.Y, DesignerSnapLine.Margin));
            }
        }

        return lines;
    }
}
