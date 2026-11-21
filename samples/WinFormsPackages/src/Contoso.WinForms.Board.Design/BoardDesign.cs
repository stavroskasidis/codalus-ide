using System.Collections;
using System.ComponentModel;
using DesignerVerb = System.ComponentModel.Design.DesignerVerb;
using DesignerVerbCollection = System.ComponentModel.Design.DesignerVerbCollection;
using Microsoft.DotNet.DesignTools.Designers;
using Microsoft.DotNet.DesignTools.Designers.Actions;

namespace Contoso.WinForms.Board.Design;

// The library's designers, built as a control library builds them for the classic IDE's out-of-process designer: against
// the designer server's API (here Codalus's replacements of it, #461, ADR 0106).

/// <summary>The board's designer: a container that gives a new board a column, with a smart tag, verbs and a dialog.</summary>
public class BoardDesigner : ParentControlDesigner
{
    private ContosoBoard Board => (ContosoBoard)Component;

    public override void InitializeNewComponent(IDictionary? defaultValues)
    {
        base.InitializeNewComponent(defaultValues);
        Board.Title = "New board";
        AddColumn("To do");
    }

    public override DesignerActionListCollection ActionLists => [new BoardActions(this)];

    public override DesignerVerbCollection Verbs => new([
        new DesignerVerb("Add Column", (_, _) => AddColumn("Column")),
        new DesignerVerb("Board Settings...", (_, _) => ShowSettings()),
        new DesignerVerb("Board Designer...", (_, _) => StartBoardDesigner()),
    ]);

    /// <summary>The board whose designer dialog the IDE's side is to continue (<c>ContinueHandler</c>).</summary>
    public static ContosoBoard? Pending { get; private set; }

    /// <summary>
    /// The library's big dialog, as DevExpress runs its Grid Designer: the designer tells the IDE's process to run a modal
    /// loop and returns; the dialog shows when that process asks the server to continue (the routing assembly's handler).
    /// </summary>
    private void StartBoardDesigner()
    {
        Pending = Board;
        GetService<Microsoft.DotNet.DesignTools.Services.INotificationService>()?.SendNotification("MessageLoopNotification/StartModalMessageLoopWithDialog", new NoData());
    }

    protected override void PreFilterProperties(IDictionary properties)
    {
        base.PreFilterProperties(properties);
        properties.Remove("BackgroundImage");
    }

    internal void AddColumn(string caption)
    {
        var column = (ContosoBoardColumn)Host.CreateComponent(typeof(ContosoBoardColumn));
        column.Caption = caption;
        Board.Columns.Add(column);
    }

    /// <summary>The library's dialog, through the surface's UI service as a vendor's are: it sets the title and closes itself.</summary>
    private void ShowSettings()
    {
        using var form = new BoardSettingsForm(Board);
        _ = GetService<System.Windows.Forms.Design.IUIService>()?.ShowDialog(form);
    }
}

/// <summary>The board's smart tag: its title and a link that adds a column.</summary>
public sealed class BoardActions(BoardDesigner designer) : DesignerActionList(designer.Component)
{
    public string Title
    {
        get => ((ContosoBoard)Component!).Title;
        set => TypeDescriptor.GetProperties(Component!)["Title"]!.SetValue(Component, value);
    }

    public void AddColumn() => designer.AddColumn("Column");

    public override DesignerActionItemCollection GetSortedActionItems() =>
    [
        new DesignerActionHeaderItem("Board"),
        new DesignerActionPropertyItem(nameof(Title), "Title", "Board"),
        new DesignerActionMethodItem(this, nameof(AddColumn), "Add Column", "Board", true),
    ];
}

/// <summary>A notification with no data.</summary>
public sealed class NoData : Microsoft.DotNet.DesignTools.Protocol.Notifications.NotificationData
{
}

/// <summary>The board's settings dialog: it shows, sets the board's title, and closes itself.</summary>
public sealed class BoardSettingsForm : Form
{
    private readonly ContosoBoard _board;
    private readonly string _title;

    public BoardSettingsForm(ContosoBoard board, string title = "From the dialog")
    {
        _board = board;
        _title = title;
        Text = "Board Settings";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(300, 200);
        ShowInTaskbar = false;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var timer = new System.Windows.Forms.Timer { Interval = 300 };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            TypeDescriptor.GetProperties(_board)["Title"]!.SetValue(_board, _title);
            DialogResult = DialogResult.OK;
        };
        timer.Start();
    }
}

/// <summary>The card's designer, which the routing assembly routes the card's older designer name to.</summary>
public class CardDesigner : ControlDesigner
{
    public override SelectionRules SelectionRules => SelectionRules.Visible | SelectionRules.Moveable;

    public override DesignerActionListCollection ActionLists => [new CardActions(Component)];
}

/// <summary>The card's smart tag: a line of text.</summary>
public sealed class CardActions(IComponent component) : DesignerActionList(component)
{
    public override DesignerActionItemCollection GetSortedActionItems() => [new DesignerActionTextItem("A card of the board", "Card")];
}

/// <summary>A column's designer: a verb.</summary>
public class BoardColumnDesigner : ComponentDesigner
{
    public override DesignerVerbCollection Verbs => new([new DesignerVerb("Rename Column", (_, _) => ((ContosoBoardColumn)Component).Caption = "Renamed")]);
}

/// <summary>A designer that fails as it initializes: its panel is designed as a Panel.</summary>
public class BrokenDesigner : ParentControlDesigner
{
    public override void Initialize(IComponent component) => throw new InvalidOperationException("The broken designer fails as designed.");
}
