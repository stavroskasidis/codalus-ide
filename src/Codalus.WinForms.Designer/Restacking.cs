namespace Codalus.WinForms.Designer;

/// <summary>
/// Which of a container's controls the designer host draws again over the first pass, and in what order (#78). Pure
/// geometry over the controls' bounds, so it's tested on every OS (the drawing itself, <c>DesignerSession.Restack</c>,
/// is Windows only); the tests link this file.
/// </summary>
internal static class Restacking
{
    /// <summary>
    /// A container's controls from the back (the last in <c>Controls</c>) to the front, each with whether it's drawn
    /// again: it overlaps another of <paramref name="siblings"/> and some of it is inside <paramref name="area"/> (its
    /// parent's client area within its ancestors'). <paramref name="siblings"/> are the visible controls' bounds in
    /// <c>Controls</c> order (the front first), in the frame's coordinates, as is <paramref name="area"/>. Controls that
    /// only touch don't overlap.
    /// </summary>
    public static IEnumerable<(int Index, bool Redraw)> BackToFront(IReadOnlyList<System.Drawing.Rectangle> siblings, System.Drawing.Rectangle area)
    {
        for (var i = siblings.Count - 1; i >= 0; i--)
        {
            var overlaps = false;
            for (var j = 0; j < siblings.Count && !overlaps; j++)
            {
                overlaps = j != i && siblings[i].IntersectsWith(siblings[j]);
            }

            yield return (i, overlaps && !area.IsEmpty && siblings[i].IntersectsWith(area));
        }
    }
}
