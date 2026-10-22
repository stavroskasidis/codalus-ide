using System.Drawing;
using Codalus.WinForms.Designer;

namespace Codalus.LanguageServices.Host.Tests;

/// <summary>
/// #78 on every OS: which of a container's controls the designer host draws again over <c>DrawToBitmap</c>'s first
/// pass, and in what order (<c>Restacking</c>, linked from the host). The drawing itself is checked on Windows by
/// <c>WinFormsDesignerHostTests</c> and the <c>winforms.designer</c> smoke step.
/// </summary>
public sealed class WinFormsDesignerRestackTests
{
    private static readonly Rectangle s_form = new(0, 0, 400, 450);

    [Fact]
    public void A_control_dropped_on_another_is_drawn_again_after_it_and_the_others_arent()
    {
        // OrderForm with a button dropped on the counter control (#78's smoke picture): Controls.Add(button1) came first,
        // so the button is in front; the label overlaps nothing.
        Rectangle[] siblings = [new(290, 300, 75, 23), new(250, 290, 200, 32), new(12, 9, 60, 15)];
        Assert.Equal([(2, false), (1, true), (0, true)], Restacking.BackToFront(siblings, s_form));

        // Sent to back: the same two, in the other order.
        Rectangle[] sent = [siblings[1], siblings[2], siblings[0]];
        Assert.Equal([(2, true), (1, false), (0, true)], Restacking.BackToFront(sent, s_form));
    }

    [Fact]
    public void Controls_that_only_touch_dont_overlap()
    {
        Rectangle[] siblings = [new(10, 10, 50, 20), new(60, 10, 50, 20), new(10, 30, 50, 20)];
        Assert.All(Restacking.BackToFront(siblings, s_form), c => Assert.False(c.Redraw));
    }

    [Fact]
    public void Three_overlapping_controls_are_drawn_again_from_the_back_to_the_front()
    {
        Rectangle[] siblings = [new(20, 20, 40, 40), new(10, 10, 40, 40), new(0, 0, 40, 40)];
        Assert.Equal([(2, true), (1, true), (0, true)], Restacking.BackToFront(siblings, s_form));
    }

    [Fact]
    public void Only_overlaps_inside_the_parents_visible_area_are_drawn_again()
    {
        // A scrolled panel's controls: the pair below the visible area isn't drawn again, the visible pair is; a parent
        // with no visible area draws none.
        var area = new Rectangle(0, 0, 200, 100);
        Rectangle[] siblings = [new(10, 10, 50, 20), new(30, 15, 50, 20), new(10, 300, 50, 20), new(30, 305, 50, 20)];
        Assert.Equal([(3, false), (2, false), (1, true), (0, true)], Restacking.BackToFront(siblings, area));
        Assert.All(Restacking.BackToFront(siblings, Rectangle.Empty), c => Assert.False(c.Redraw));
        Assert.Empty(Restacking.BackToFront([], s_form));
    }
}
