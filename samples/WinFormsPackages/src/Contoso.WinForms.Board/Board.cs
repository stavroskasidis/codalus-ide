using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Contoso.WinForms.Board;

/// <summary>A board: a container whose designer (in the design package) gives it a column when it's dropped, a smart tag and verbs.</summary>
[Designer("Contoso.WinForms.Board.Design.BoardDesigner, Contoso.WinForms.Board.Design")]
public class ContosoBoard : Panel
{
    [DefaultValue("")]
    public string Title { get; set; } = string.Empty;

    /// <summary>The board's columns, components of the form, which the code adds with <c>Columns.AddRange</c> (as a DevExpress grid view's are).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
    public ContosoBoardColumnCollection Columns { get; } = [];

    /// <summary>
    /// The board's lanes, a tree of objects that aren't components: the code declares each as a local and fills
    /// <c>Lanes.Nodes</c> with them, as the classic IDE writes a DevExpress grid's levels (<c>LevelTree.Nodes</c>).
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
    [Browsable(false)]
    public ContosoLaneTree Lanes { get; } = new();
}

/// <summary>A board's lanes' root.</summary>
public class ContosoLaneTree
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
    public ContosoLaneCollection Nodes { get; } = [];
}

/// <summary>A lane: a title, the column it shows, and lanes of its own.</summary>
public class ContosoLane
{
    [DefaultValue("")]
    public string Title { get; set; } = string.Empty;

    [DefaultValue(null)]
    public ContosoBoardColumn? Column { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
    [Browsable(false)]
    public ContosoLaneCollection Nodes { get; } = [];
}

/// <summary>Lanes.</summary>
public class ContosoLaneCollection : Collection<ContosoLane>
{
    public void AddRange(ContosoLane[] lanes)
    {
        foreach (var lane in lanes)
        {
            Add(lane);
        }
    }
}

/// <summary>A board's columns.</summary>
public class ContosoBoardColumnCollection : Collection<ContosoBoardColumn>
{
    public void AddRange(ContosoBoardColumn[] columns)
    {
        foreach (var column in columns)
        {
            Add(column);
        }
    }
}

/// <summary>
/// A card, whose designer is named as the library's older design assembly named it: the routing assembly routes the name.
/// In design mode a click on its right half (its designer's hit test lets that click through, as the classic IDE's
/// surface does for a control's live area) opens a menu whose item pins it, as DevExpress's level designer opens its
/// Change view menu; with no one to pick it, the item picks itself.
/// </summary>
[Designer("Contoso.Legacy.Design.CardDesigner, Contoso.Legacy.Design")]
public class ContosoCard : Label
{
    [DefaultValue(false)]
    public bool Pinned { get; set; }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (DesignMode && e.Button == MouseButtons.Left)
        {
            var menu = new ContextMenuStrip();
            var pin = menu.Items.Add("Pin", null, (_, _) => TypeDescriptor.GetProperties(this)[nameof(Pinned)]!.SetValue(this, !Pinned));
            var timer = new System.Windows.Forms.Timer { Interval = 300 };
            timer.Tick += (_, _) =>
            {
                timer.Dispose();
                pin.PerformClick();
                menu.Close();
            };
            menu.Show(this, e.Location);
            timer.Start();
        }
    }
}

/// <summary>A board's column: a component with a designer of its own.</summary>
[Designer("Contoso.WinForms.Board.Design.BoardColumnDesigner, Contoso.WinForms.Board.Design")]
[ToolboxItem(false)]
[DesignTimeVisible(false)]
public class ContosoBoardColumn : Component
{
    [DefaultValue("")]
    public string Caption { get; set; } = string.Empty;
}

/// <summary>A panel whose designer throws as it initializes: it's designed as a Panel.</summary>
[Designer("Contoso.WinForms.Board.Design.BrokenDesigner, Contoso.WinForms.Board.Design")]
public class ContosoBrokenPanel : Panel
{
}
