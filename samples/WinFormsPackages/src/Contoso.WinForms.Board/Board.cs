using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Contoso.WinForms.Board;

/// <summary>A board: a container whose designer (in the design package) gives it a column when it's dropped, a smart tag and verbs.</summary>
[Designer("Contoso.WinForms.Board.Design.BoardDesigner, Contoso.WinForms.Board.Design")]
public class ContosoBoard : Panel
{
    [DefaultValue("")]
    public string Title { get; set; } = string.Empty;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
    public Collection<ContosoBoardColumn> Columns { get; } = [];
}

/// <summary>A card, whose designer is named as the library's older design assembly named it: the routing assembly routes the name.</summary>
[Designer("Contoso.Legacy.Design.CardDesigner, Contoso.Legacy.Design")]
public class ContosoCard : Label
{
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
