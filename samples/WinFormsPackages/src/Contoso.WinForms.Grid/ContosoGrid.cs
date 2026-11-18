using System.ComponentModel;

namespace Contoso.WinForms;

/// <summary>A grid. The app's code doesn't use it, so the app's assembly doesn't reference this package's.</summary>
[Description("Shows rows and columns.")]
public class ContosoGrid : Control
{
    public ContosoGrid()
    {
        Size = new Size(240, 150);
        BackColor = SystemColors.Window;
    }

    [DefaultValue(3)]
    public int Columns { get; set; } = 3;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var width = Math.Max(1, ClientSize.Width / Math.Max(1, Columns));
        for (var x = width; x < ClientSize.Width; x += width)
        {
            e.Graphics.DrawLine(SystemPens.ControlDark, x, 0, x, ClientSize.Height);
        }

        e.Graphics.DrawRectangle(SystemPens.ControlDark, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }
}
