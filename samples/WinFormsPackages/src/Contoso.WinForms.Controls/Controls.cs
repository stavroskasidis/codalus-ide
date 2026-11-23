using System.ComponentModel;
using System.ComponentModel.Design;

namespace Contoso.WinForms;

/// <summary>
/// The form a vendor's project template derives its forms from. Its designer is named in a design-time assembly the
/// package doesn't ship, as a vendor's are when its design-time package isn't referenced.
/// </summary>
[Designer("Contoso.WinForms.Design.ContosoFormDesigner, Contoso.WinForms.Design", typeof(IRootDesigner))]
public class ContosoForm : Form
{
    public ContosoForm()
    {
        BackColor = Color.Lavender;
    }

    [Category("Appearance")]
    [DefaultValue("Classic")]
    [Description("The look of the form and its controls.")]
    public string Skin { get; set; } = "Classic";
}

/// <summary>A button, with a designer named in the design-time assembly the package doesn't ship.</summary>
[Designer("Contoso.WinForms.Design.ContosoButtonDesigner, Contoso.WinForms.Design")]
[Description("A button in the form's skin.")]
public class ContosoButton : Button
{
}

/// <summary>A component (not a control): it goes to the component tray.</summary>
[Description("Ticks while the form is open.")]
public class ContosoClock : Component
{
    [DefaultValue(1000)]
    public int Interval { get; set; } = 1000;
}

/// <summary>The editors' base: not a Toolbox item itself.</summary>
[ToolboxItem(false)]
public class ContosoEditBase : Control
{
}

/// <summary>An editor: a Toolbox item again, though its base isn't.</summary>
[ToolboxItem(true)]
public class ContosoTextEdit : ContosoEditBase
{
}

/// <summary>What <see cref="ContosoToolboxItemAttribute"/> says about a type.</summary>
public enum ContosoToolboxItemKind
{
    Free,
    Regular,
    Hidden,
}

/// <summary>
/// The library's own Toolbox attribute, derived from <see cref="ToolboxItemAttribute"/> as a control library's is: a
/// type marked with it is a Toolbox item unless it's given <c>false</c> or <see cref="ContosoToolboxItemKind.Hidden"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ContosoToolboxItemAttribute : ToolboxItemAttribute
{
    public ContosoToolboxItemAttribute(bool defaultType)
        : base(defaultType)
    {
    }

    public ContosoToolboxItemAttribute(ContosoToolboxItemKind kind)
        : base(kind != ContosoToolboxItemKind.Hidden)
    {
    }
}

/// <summary>The Toolbox tab a library names for its controls (a vendor's own attribute, as DevExpress's is).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ToolboxTabNameAttribute(string tabName) : Attribute
{
    public string TabName { get; } = tabName;
}

/// <summary>The namespace the library's Toolbox pictures are embedded under (<c>ToolboxIcons/</c>).</summary>
internal sealed class ToolboxIconsRoot
{
}

/// <summary>An editor marked with the library's own attribute: a Toolbox item again, though its base isn't. Its tab and picture are the library's.</summary>
[ContosoToolboxItem(true)]
[ToolboxTabName("Contoso: Editors")]
[ToolboxBitmap(typeof(ToolboxIconsRoot), "ToolboxIcons.SpinEdit.bmp")]
public class ContosoSpinEdit : ContosoEditBase
{
}

/// <summary>An editor marked with the library's own attribute and a kind that shows it, in the library's tab.</summary>
[ContosoToolboxItem(ContosoToolboxItemKind.Regular)]
[ToolboxTabName("Contoso: Editors")]
public class ContosoDateEdit : ContosoEditBase
{
}

/// <summary>Hidden by the library's own attribute and its kind.</summary>
[ContosoToolboxItem(ContosoToolboxItemKind.Hidden)]
public class ContosoPopupEdit : Control
{
}

/// <summary>Hidden by the library's own attribute given <c>false</c>.</summary>
[ContosoToolboxItem(false)]
public class ContosoInnerEdit : Control
{
}

/// <summary>Derives from a base that isn't a Toolbox item, and isn't one either (the attribute is inherited).</summary>
public class ContosoEditHelper : ContosoEditBase
{
}

/// <summary>Not a Toolbox item.</summary>
[ToolboxItem(false)]
public class ContosoHiddenControl : Control
{
}

/// <summary>Not visible at design time.</summary>
[DesignTimeVisible(false)]
public class ContosoInvisibleComponent : Component
{
}

/// <summary>Abstract: nothing to create.</summary>
public abstract class ContosoAbstractControl : Control
{
}

/// <summary>Internal: the app can't name it.</summary>
internal class ContosoInternalControl : Control
{
}

/// <summary>No parameterless constructor.</summary>
public class ContosoConfiguredControl(string configuration) : Control
{
    public string Configuration { get; } = configuration;
}

/// <summary>A form: the Toolbox doesn't list forms.</summary>
public class ContosoDialog : ContosoForm
{
}
